using System.Buffers.Binary;
using System.IO.Hashing;
using System.Security.Cryptography;
using Cloner.Core.Inventory;
using Cloner.Core.PartitionTables;

namespace Cloner.Core.Layout;

/// <summary>
/// Identificadores que tendrá el clon (RF-18): GUID del disco y de cada partición (GPT) o firma
/// del disco (MBR). Las particiones se indican por su posición en el origen.
/// </summary>
public sealed record TableIdentity
{
    public Guid? DiskGuid { get; init; }
    public uint? MbrSignature { get; init; }
    public IReadOnlyDictionary<long, Guid> PartitionGuids { get; init; } = new Dictionary<long, Guid>();

    /// <summary>GUID de las particiones que se crean en el destino (<see cref="TargetLayout.Created"/>), en orden.</summary>
    public IReadOnlyList<Guid> CreatedGuids { get; init; } = [];

    /// <summary>Identificadores aleatorios nuevos para todas las particiones.</summary>
    public static TableIdentity CreateNew(PartitionStyle style, IEnumerable<PartitionInfo> partitions, int created = 0)
    {
        ArgumentNullException.ThrowIfNull(partitions);
        if (style == PartitionStyle.Mbr)
        {
            uint signature;
            do
            {
                signature = BinaryPrimitives.ReadUInt32LittleEndian(RandomNumberGenerator.GetBytes(4));
            }
            while (signature == 0);

            return new TableIdentity { MbrSignature = signature };
        }

        return new TableIdentity
        {
            DiskGuid = Guid.NewGuid(),
            PartitionGuids = partitions
                .Where(p => p.Kind != PartitionKind.Extended)
                .ToDictionary(p => p.OffsetBytes, _ => Guid.NewGuid()),
            CreatedGuids = Enumerable.Range(0, created).Select(_ => Guid.NewGuid()).ToList(),
        };
    }
}

/// <summary>Principio del disco y, en GPT, copia de respaldo, ya preparados para el destino.</summary>
internal sealed record BuiltTable(byte[] Head, byte[]? Backup);

/// <summary>
/// Genera la tabla de particiones del destino a partir de la del origen: mismas entradas (tipo,
/// atributos, nombres) con la posición y el tamaño de la nueva distribución y, si se piden, otros
/// identificadores. No toca discos.
/// </summary>
internal static class TableBuilder
{
    private const int MbrEntries = 446;
    private const int MbrEntrySize = 16;

    /// <param name="provisional">
    /// Tabla para montar el clon antes de terminarlo. En GPT, ninguna partición recibe letra de
    /// unidad (así Windows no ofrece formatear las que aún están vacías) y la partición EFI que se
    /// crea figura como de datos, para que Windows la monte y se pueda formatear y llenar.
    /// </param>
    public static BuiltTable Build(
        ReadOnlySpan<byte> sourceHead,
        PartitionTableInfo table,
        TargetLayout layout,
        long targetSectors,
        int sectorSize,
        TableIdentity? identity,
        bool provisional = false)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(layout);

        if (layout.Style is { } style && style != table.Style)
        {
            return style == PartitionStyle.Gpt
                ? BuildNewGpt(Entries(layout, identity, style, provisional), targetSectors, sectorSize, identity, provisional)
                : BuildNewMbr(Entries(layout, identity, style, provisional), sectorSize, identity);
        }

        var head = sourceHead[..PartitionTable.HeadBytes].ToArray();

        // Lo que haya de una partición dentro del primer MiB se copia en su sitio nuevo, no aquí.
        foreach (var placement in layout.Placements.Where(p => p.Moves && p.Partition.OffsetBytes < PartitionTable.HeadBytes))
        {
            var start = (int)placement.Partition.OffsetBytes;
            var end = (int)Math.Min(placement.Partition.OffsetBytes + placement.Partition.SizeBytes, PartitionTable.HeadBytes);
            head.AsSpan(start, end - start).Clear();
        }

        return table.Gpt is { } gpt
            ? BuildGpt(head, gpt, layout, targetSectors, sectorSize, identity, provisional)
            : BuildMbr(head, layout, sectorSize, identity);
    }

    /// <summary>Atributo GPT de las particiones de datos: Windows no les asigna letra de unidad.</summary>
    public const ulong NoDriveLetterAttribute = 0x8000000000000000;

    private static BuiltTable BuildGpt(byte[] head, GptHeader gpt, TargetLayout layout, long targetSectors, int sectorSize, TableIdentity? identity, bool provisional)
    {
        var entriesStart = (int)(gpt.PartitionEntryLba * sectorSize);
        var entriesLength = gpt.EntriesSectors * sectorSize;
        var entries = head.AsSpan(entriesStart, entriesLength);

        for (var i = 0; i < gpt.EntryCount; i++)
        {
            var entry = entries.Slice(i * (int)gpt.EntrySize, (int)gpt.EntrySize);
            if (entry[..16].IndexOfAnyExcept((byte)0) < 0)
            {
                continue;
            }

            var sourceOffset = BinaryPrimitives.ReadInt64LittleEndian(entry[32..]) * sectorSize;
            var placement = FindPlacement(layout, sourceOffset);
            BinaryPrimitives.WriteInt64LittleEndian(entry[32..], placement.TargetOffset / sectorSize);
            BinaryPrimitives.WriteInt64LittleEndian(entry[40..], (placement.TargetEnd / sectorSize) - 1);
            if (identity is not null && identity.PartitionGuids.TryGetValue(sourceOffset, out var unique))
            {
                unique.TryWriteBytes(entry.Slice(16, 16));
            }

            if (provisional)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(entry[48..], BinaryPrimitives.ReadUInt64LittleEndian(entry[48..]) | NoDriveLetterAttribute);
            }
        }

        var header = gpt with
        {
            DiskGuid = identity?.DiskGuid ?? gpt.DiskGuid,
            EntriesCrc = Crc32.HashToUInt32(entries[..(int)gpt.EntriesBytes]),
        };

        var lastLba = targetSectors - 1;
        var lastUsable = lastLba - gpt.EntriesSectors - 1;
        header.Encode(1, lastLba, lastUsable, gpt.PartitionEntryLba).CopyTo(head, sectorSize);
        SetProtectiveSize(head, targetSectors);

        var backup = new byte[entriesLength + sectorSize];
        entries.CopyTo(backup);
        header.Encode(lastLba, 1, lastUsable, lastLba - gpt.EntriesSectors).CopyTo(backup, entriesLength);
        return new BuiltTable(head, backup);
    }

    private static BuiltTable BuildMbr(byte[] head, TargetLayout layout, int sectorSize, TableIdentity? identity)
    {
        for (var i = 0; i < 4; i++)
        {
            var entry = head.AsSpan(MbrEntries + (i * MbrEntrySize), MbrEntrySize);
            if (entry[4] == 0)
            {
                continue;
            }

            var sourceOffset = (long)BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]) * sectorSize;
            var placement = FindPlacement(layout, sourceOffset);
            if (!placement.Moves && !placement.Grows)
            {
                continue;
            }

            BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], checked((uint)(placement.TargetOffset / sectorSize)));
            BinaryPrimitives.WriteUInt32LittleEndian(entry[12..], checked((uint)(placement.TargetSize / sectorSize)));

            // Direcciones CHS fuera de rango: los sistemas actuales usan las LBA.
            entry[1] = entry[5] = 0xFE;
            entry[2] = entry[3] = entry[6] = entry[7] = 0xFF;
        }

        if (identity?.MbrSignature is { } signature)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(440), signature);
        }

        return new BuiltTable(head, null);
    }

    /// <summary>Bytes de la tabla dentro de la cabeza del destino (en la conversión, los de la tabla nueva).</summary>
    public static int TableBytes(PartitionTableInfo table, TargetLayout layout, int sectorSize)
    {
        ArgumentNullException.ThrowIfNull(layout);
        return layout.Style switch
        {
            PartitionStyle.Gpt when table.Style != PartitionStyle.Gpt => (2 + StyleConversion.GptEntriesSectors(sectorSize)) * sectorSize,
            PartitionStyle.Mbr when table.Style != PartitionStyle.Mbr => sectorSize,
            _ => PartitionTable.TableBytes(table, sectorSize),
        };
    }

    private sealed record NewEntry(long Offset, long Size, ConvertedType Type, Guid Unique, bool Active);

    /// <summary>Entradas de la tabla convertida, ordenadas por posición.</summary>
    private static List<NewEntry> Entries(TargetLayout layout, TableIdentity? identity, PartitionStyle style, bool provisional)
    {
        var entries = new List<NewEntry>();
        foreach (var placement in layout.Placements)
        {
            var partition = placement.Partition;
            var type = StyleConversion.TypeFor(partition)
                ?? throw new CloneException($"La partición {partition.Number} es de un tipo que no se sabe convertir.");
            var unique = identity is not null && identity.PartitionGuids.TryGetValue(partition.OffsetBytes, out var guid) ? guid : Guid.NewGuid();
            entries.Add(new NewEntry(placement.TargetOffset, placement.TargetSize, type, unique, Active: style == PartitionStyle.Mbr && partition.ContainsWindows));
        }

        for (var i = 0; i < layout.Created.Count; i++)
        {
            var created = layout.Created[i];
            var type = provisional && created.Kind == PartitionKind.EfiSystem
                ? new ConvertedType(PartitionTypes.BasicData, 0, "Datos", 0x07)
                : StyleConversion.TypeFor(created);
            var unique = identity is not null && i < identity.CreatedGuids.Count ? identity.CreatedGuids[i] : Guid.NewGuid();
            entries.Add(new NewEntry(created.Offset, created.Size, type, unique, Active: false));
        }

        return entries.OrderBy(e => e.Offset).ToList();
    }

    /// <summary>Tabla GPT nueva: MBR protector, cabecera y 128 entradas al principio y su copia al final.</summary>
    private static BuiltTable BuildNewGpt(List<NewEntry> entries, long targetSectors, int sectorSize, TableIdentity? identity, bool provisional)
    {
        var head = new byte[PartitionTable.HeadBytes];
        var entriesSectors = StyleConversion.GptEntriesSectors(sectorSize);
        var array = new byte[entriesSectors * sectorSize];
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var slot = array.AsSpan(i * StyleConversion.GptEntrySize, StyleConversion.GptEntrySize);
            entry.Type.GptType.TryWriteBytes(slot);
            entry.Unique.TryWriteBytes(slot[16..]);
            BinaryPrimitives.WriteInt64LittleEndian(slot[32..], entry.Offset / sectorSize);
            BinaryPrimitives.WriteInt64LittleEndian(slot[40..], ((entry.Offset + entry.Size) / sectorSize) - 1);
            BinaryPrimitives.WriteUInt64LittleEndian(slot[48..], entry.Type.GptAttributes | (provisional ? NoDriveLetterAttribute : 0));
            var name = entry.Type.GptName.Length > 36 ? entry.Type.GptName[..36] : entry.Type.GptName;
            System.Text.Encoding.Unicode.GetBytes(name).CopyTo(slot[56..]);
        }

        var lastLba = targetSectors - 1;
        var lastUsable = lastLba - entriesSectors - 1;
        var header = new GptHeader
        {
            SectorSize = sectorSize,
            Revision = 0x00010000,
            HeaderSize = GptHeader.MinimumHeaderSize,
            MyLba = 1,
            AlternateLba = lastLba,
            FirstUsableLba = 2 + entriesSectors,
            LastUsableLba = lastUsable,
            DiskGuid = identity?.DiskGuid ?? Guid.NewGuid(),
            PartitionEntryLba = 2,
            EntryCount = StyleConversion.GptEntryCount,
            EntrySize = StyleConversion.GptEntrySize,
            EntriesCrc = Crc32.HashToUInt32(array.AsSpan(0, StyleConversion.GptEntryCount * StyleConversion.GptEntrySize)),
        };

        // MBR protector: una sola entrada de tipo 0xEE que cubre todo el disco.
        var protective = head.AsSpan(MbrEntries, MbrEntrySize);
        protective[2] = 0x02;
        protective[4] = 0xEE;
        protective[5] = protective[6] = protective[7] = 0xFF;
        BinaryPrimitives.WriteUInt32LittleEndian(protective[8..], 1);
        SetProtectiveSize(head, targetSectors);
        head[510] = 0x55;
        head[511] = 0xAA;

        header.Encode(1, lastLba, lastUsable, 2).CopyTo(head, sectorSize);
        array.CopyTo(head, 2 * sectorSize);

        var backup = new byte[array.Length + sectorSize];
        array.CopyTo(backup, 0);
        header.Encode(lastLba, 1, lastUsable, lastLba - entriesSectors).CopyTo(backup, array.Length);
        return new BuiltTable(head, backup);
    }

    /// <summary>Tabla MBR nueva: código de arranque propio, firma y hasta cuatro particiones primarias.</summary>
    private static BuiltTable BuildNewMbr(List<NewEntry> entries, int sectorSize, TableIdentity? identity)
    {
        if (entries.Count > 4)
        {
            throw new CloneException("Una tabla MBR admite como mucho 4 particiones.");
        }

        var head = new byte[PartitionTable.HeadBytes];
        MbrBootCode.Create().CopyTo(head, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(440), identity?.MbrSignature ?? 0x434C4F4E);
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var slot = head.AsSpan(MbrEntries + (i * MbrEntrySize), MbrEntrySize);
            slot[0] = entry.Active ? (byte)0x80 : (byte)0x00;
            slot[1] = slot[5] = 0xFE;
            slot[2] = slot[3] = slot[6] = slot[7] = 0xFF;
            slot[4] = entry.Type.MbrType;
            BinaryPrimitives.WriteUInt32LittleEndian(slot[8..], checked((uint)(entry.Offset / sectorSize)));
            BinaryPrimitives.WriteUInt32LittleEndian(slot[12..], checked((uint)(entry.Size / sectorSize)));
        }

        head[510] = 0x55;
        head[511] = 0xAA;
        return new BuiltTable(head, null);
    }

    private static PartitionPlacement FindPlacement(TargetLayout layout, long sourceOffset) =>
        layout.Placements.FirstOrDefault(p => p.Partition.OffsetBytes == sourceOffset)
        ?? throw new CloneException("La tabla del origen tiene una partición que Windows no informa; no se puede reorganizar.");

    private static void SetProtectiveSize(Span<byte> head, long targetSectors)
    {
        var size = (uint)Math.Min(targetSectors - 1, uint.MaxValue);
        for (var i = 0; i < 4; i++)
        {
            var entry = head.Slice(MbrEntries + (i * MbrEntrySize), MbrEntrySize);
            if (entry[4] == 0xEE)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(entry[12..], size);
            }
        }
    }
}

/// <summary>Sector de arranque de un volumen: el campo «sectores ocultos» guarda dónde empieza la partición.</summary>
internal static class BootSector
{
    private const int HiddenSectorsOffset = 0x1C;

    private static ReadOnlySpan<byte> NtfsId => "NTFS    "u8;
    private static ReadOnlySpan<byte> Fat32Id => "FAT32   "u8;

    /// <summary>Actualiza el inicio de la partición en un sector de arranque NTFS o FAT32. Otros sectores no se tocan.</summary>
    public static bool SetHiddenSectors(Span<byte> sector, long startLba)
    {
        if (sector.Length < 512 || sector[510] != 0x55 || sector[511] != 0xAA || startLba > uint.MaxValue)
        {
            return false;
        }

        var isNtfs = sector.Slice(3, 8).SequenceEqual(NtfsId);
        var isFat32 = sector.Slice(0x52, 8).SequenceEqual(Fat32Id);
        if (!isNtfs && !isFat32)
        {
            return false;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(sector[HiddenSectorsOffset..], (uint)startLba);
        return true;
    }
}
