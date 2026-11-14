using System.Buffers.Binary;
using System.Globalization;
using Cloner.Core.Inventory;

namespace Cloner.Core.PartitionTables;

/// <summary>Lo que se lee al principio de un disco: estilo de tabla, identificador y, si es GPT, su cabecera.</summary>
/// <param name="Defect">Por qué la tabla no es válida, o nulo si lo es.</param>
internal sealed record PartitionTableInfo(PartitionStyle Style, string? Identifier, GptHeader? Gpt, string? Defect = null);

/// <summary>Lee y ajusta tablas de particiones MBR y GPT a partir de los bytes del principio del disco.</summary>
internal static class PartitionTable
{
    /// <summary>Zona del principio del disco que contiene la tabla: se borra al empezar y se escribe al final (RS-05).</summary>
    public const int HeadBytes = 1024 * 1024;

    private const int MbrEntriesOffset = 446;
    private const int MbrEntrySize = 16;
    private const byte ProtectiveType = 0xEE;

    /// <summary>Interpreta el principio del disco. <paramref name="head"/> debe incluir al menos dos sectores.</summary>
    public static PartitionTableInfo Read(ReadOnlySpan<byte> head, int sectorSize)
    {
        if (sectorSize < 512 || head.Length < 2 * sectorSize)
        {
            throw new ArgumentException("Hacen falta al menos los dos primeros sectores del disco.", nameof(head));
        }

        var mbr = head[..sectorSize];
        if (mbr[510] != 0x55 || mbr[511] != 0xAA)
        {
            return new PartitionTableInfo(PartitionStyle.Raw, null, null);
        }

        if (!HasProtectiveEntry(mbr))
        {
            var signature = BinaryPrimitives.ReadUInt32LittleEndian(mbr[440..]);
            return new PartitionTableInfo(PartitionStyle.Mbr, signature.ToString("X8", CultureInfo.InvariantCulture), null);
        }

        var gpt = GptHeader.TryParse(head.Slice(sectorSize, sectorSize));
        if (gpt is null || gpt.MyLba != 1)
        {
            return new PartitionTableInfo(PartitionStyle.Gpt, null, null, "la cabecera GPT principal no es válida");
        }

        var identifier = gpt.DiskGuid.ToString("D");
        var entriesStart = gpt.PartitionEntryLba * sectorSize;
        var entriesEnd = entriesStart + (long)gpt.EntriesSectors * sectorSize;
        if (gpt.PartitionEntryLba < 2 || entriesEnd > head.Length)
        {
            return new PartitionTableInfo(PartitionStyle.Gpt, identifier, gpt, "las entradas GPT están fuera de la zona habitual");
        }

        if (!gpt.EntriesMatch(head[(int)entriesStart..(int)entriesEnd]))
        {
            return new PartitionTableInfo(PartitionStyle.Gpt, identifier, gpt, "las entradas GPT están dañadas");
        }

        return new PartitionTableInfo(PartitionStyle.Gpt, identifier, gpt);
    }

    /// <summary>Bytes que forman la tabla dentro de la cabeza del disco: MBR o MBR protector, cabecera y entradas.</summary>
    public static int TableBytes(PartitionTableInfo table, int sectorSize) => table.Gpt is { } gpt
        ? checked((int)((gpt.PartitionEntryLba + gpt.EntriesSectors) * sectorSize))
        : sectorSize;

    /// <summary>
    /// Ajusta la tabla GPT principal al tamaño del destino: la cabecera apunta a la copia de
    /// respaldo en el último sector y el espacio utilizable llega hasta ella. También se
    /// actualiza el tamaño de la partición protectora del MBR.
    /// </summary>
    public static void AdjustPrimaryGpt(Span<byte> head, GptHeader gpt, long targetSectors)
    {
        var lastLba = targetSectors - 1;
        var lastUsable = lastLba - gpt.EntriesSectors - 1;
        gpt.Encode(myLba: 1, alternateLba: lastLba, lastUsable, gpt.PartitionEntryLba)
            .CopyTo(head.Slice(gpt.SectorSize, gpt.SectorSize));

        var protectiveSectors = (uint)Math.Min(targetSectors - 1, uint.MaxValue);
        for (var i = 0; i < 4; i++)
        {
            var entry = head.Slice(MbrEntriesOffset + (i * MbrEntrySize), MbrEntrySize);
            if (entry[4] == ProtectiveType)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(entry[12..], protectiveSectors);
            }
        }
    }

    /// <summary>
    /// Copia de respaldo de la GPT para el final del destino: las mismas entradas seguidas de la
    /// cabecera de respaldo, que ocupa el último sector.
    /// </summary>
    public static byte[] BuildBackupGpt(ReadOnlySpan<byte> head, GptHeader gpt, long targetSectors)
    {
        var sectorSize = gpt.SectorSize;
        var lastLba = targetSectors - 1;
        var entriesLba = lastLba - gpt.EntriesSectors;
        var lastUsable = entriesLba - 1;

        var backup = new byte[(gpt.EntriesSectors + 1) * sectorSize];
        var entriesBytes = gpt.EntriesSectors * sectorSize;
        head.Slice((int)(gpt.PartitionEntryLba * sectorSize), entriesBytes).CopyTo(backup);
        gpt.Encode(myLba: lastLba, alternateLba: 1, lastUsable, entriesLba).CopyTo(backup.AsSpan(entriesBytes));
        return backup;
    }

    private static bool HasProtectiveEntry(ReadOnlySpan<byte> mbr)
    {
        for (var i = 0; i < 4; i++)
        {
            if (mbr[MbrEntriesOffset + (i * MbrEntrySize) + 4] == ProtectiveType)
            {
                return true;
            }
        }

        return false;
    }
}
