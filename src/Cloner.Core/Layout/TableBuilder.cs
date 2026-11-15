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

    /// <summary>Identificadores aleatorios nuevos para todas las particiones.</summary>
    public static TableIdentity CreateNew(PartitionStyle style, IEnumerable<PartitionInfo> partitions)
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

    public static BuiltTable Build(
        ReadOnlySpan<byte> sourceHead,
        PartitionTableInfo table,
        TargetLayout layout,
        long targetSectors,
        int sectorSize,
        TableIdentity? identity)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(layout);

        var head = sourceHead[..PartitionTable.HeadBytes].ToArray();

        // Lo que haya de una partición dentro del primer MiB se copia en su sitio nuevo, no aquí.
        foreach (var placement in layout.Placements.Where(p => p.Moves && p.Partition.OffsetBytes < PartitionTable.HeadBytes))
        {
            var start = (int)placement.Partition.OffsetBytes;
            var end = (int)Math.Min(placement.Partition.OffsetBytes + placement.Partition.SizeBytes, PartitionTable.HeadBytes);
            head.AsSpan(start, end - start).Clear();
        }

        return table.Gpt is { } gpt
            ? BuildGpt(head, gpt, layout, targetSectors, sectorSize, identity)
            : BuildMbr(head, layout, sectorSize, identity);
    }

    private static BuiltTable BuildGpt(byte[] head, GptHeader gpt, TargetLayout layout, long targetSectors, int sectorSize, TableIdentity? identity)
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
