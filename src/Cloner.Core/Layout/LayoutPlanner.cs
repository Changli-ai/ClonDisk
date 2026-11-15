using Cloner.Core.Inventory;
using Cloner.Core.PartitionTables;

namespace Cloner.Core.Layout;

/// <summary>
/// Calcula la distribución de las particiones en el destino: alineación a 1 MiB (RF-16) y
/// reparto del espacio sobrante (RF-13). No toca discos.
/// </summary>
internal static class LayoutPlanner
{
    public const long MiB = 1024 * 1024;

    /// <summary>Límite de un disco MBR: 2^32 sectores.</summary>
    private const long MbrMaxSectors = 1L << 32;

    /// <param name="problems">Motivos que impiden la distribución pedida.</param>
    /// <param name="warnings">Ajustes que se hicieron por el camino.</param>
    public static TargetLayout Plan(
        DiskInfo source,
        DiskInfo target,
        PartitionTableInfo table,
        SizeMode sizeMode,
        AlignmentMode alignment,
        List<string> problems,
        List<string> warnings)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(table);

        var partitions = source.Partitions.Where(p => p.Kind != PartitionKind.Extended).OrderBy(p => p.OffsetBytes).ToList();
        var same = TargetLayout.Same(partitions);
        var sectorSize = source.LogicalSectorSize;
        if (partitions.Count == 0 || sectorSize <= 0 || table.Style == PartitionStyle.Raw)
        {
            return same;
        }

        var wantAlign = alignment == AlignmentMode.Always || (alignment == AlignmentMode.Auto && target.Media == MediaKind.Ssd);
        var misaligned = partitions.Any(p => p.OffsetBytes % MiB != 0);
        var wantGrow = sizeMode != SizeMode.Keep && target.SizeBytes > source.SizeBytes;
        if (!(wantAlign && misaligned) && !wantGrow)
        {
            return same;
        }

        // Las particiones lógicas dependen de la cadena de EBR, que esta versión no reescribe.
        if (source.Partitions.Any(p => p.Kind == PartitionKind.Extended))
        {
            problems.Add("El origen tiene particiones lógicas (partición extendida MBR): no se pueden mover ni ampliar. Elige mantener los tamaños.");
            return same;
        }

        var usableEnd = UsableEnd(table, target.SizeBytes, sectorSize);
        var placed = Place(partitions, wantAlign && misaligned);
        if (placed[^1].TargetEnd > usableEnd)
        {
            if (alignment == AlignmentMode.Always)
            {
                problems.Add("Al alinear las particiones a 1 MiB no caben en el destino.");
                return same;
            }

            warnings.Add("Las particiones no se alinean a 1 MiB porque, alineadas, no cabrían en el destino.");
            placed = Place(partitions, align: false);
            wantAlign = false;
        }

        if (wantGrow)
        {
            var free = usableEnd - placed[^1].TargetEnd;
            var growth = Distribute(placed, sizeMode, free, warnings);
            placed = Grow(placed, growth);
        }

        return new TargetLayout { Placements = placed, Aligned = wantAlign && misaligned };
    }

    /// <summary>Partición que se considera principal: la de Windows o, si no, la de datos NTFS mayor.</summary>
    public static PartitionInfo? MainPartition(IEnumerable<PartitionInfo> partitions)
    {
        var growable = partitions.Where(IsGrowable).ToList();
        return growable.FirstOrDefault(p => p.ContainsWindows) ?? growable.MaxBy(p => p.SizeBytes);
    }

    /// <summary>Solo se amplían volúmenes NTFS de datos: Windows sabe extenderlos sin desmontarlos.</summary>
    public static bool IsGrowable(PartitionInfo partition) =>
        partition.Kind == PartitionKind.BasicData
        && string.Equals(partition.Volume?.FileSystem, "NTFS", StringComparison.OrdinalIgnoreCase);

    /// <summary>Final (exclusivo) del espacio que pueden ocupar las particiones en el destino.</summary>
    private static long UsableEnd(PartitionTableInfo table, long targetBytes, int sectorSize)
    {
        var targetSectors = targetBytes / sectorSize;
        return table.Gpt is { } gpt
            ? (targetSectors - 1 - gpt.EntriesSectors) * sectorSize
            : Math.Min(targetSectors, MbrMaxSectors) * sectorSize;
    }

    /// <summary>
    /// Coloca las particiones en orden y con su tamaño, conservando los huecos entre ellas. Al
    /// alinear, ninguna empieza por debajo de 1 MiB ni fuera de un múltiplo de 1 MiB.
    /// </summary>
    private static List<PartitionPlacement> Place(List<PartitionInfo> partitions, bool align)
    {
        var placed = new List<PartitionPlacement>();
        long shift = 0;
        long previousEnd = 0;
        foreach (var partition in partitions)
        {
            var offset = partition.OffsetBytes + shift;
            if (align)
            {
                offset = RoundUp(Math.Max(offset, PartitionTable.HeadBytes), MiB);
            }

            offset = Math.Max(offset, align ? RoundUp(previousEnd, MiB) : previousEnd);
            shift = offset - partition.OffsetBytes;
            placed.Add(new PartitionPlacement(partition, offset, partition.SizeBytes));
            previousEnd = offset + partition.SizeBytes;
        }

        return placed;
    }

    private static Dictionary<PartitionInfo, long> Distribute(List<PartitionPlacement> placed, SizeMode mode, long free, List<string> warnings)
    {
        var growth = new Dictionary<PartitionInfo, long>();
        var available = RoundDown(free, MiB);
        if (available <= 0)
        {
            return growth;
        }

        var partitions = placed.Select(p => p.Partition).ToList();
        if (mode == SizeMode.ExtendMain)
        {
            if (MainPartition(partitions) is { } main)
            {
                growth[main] = available;
            }
        }
        else
        {
            var growable = partitions.Where(IsGrowable).ToList();
            var total = growable.Sum(p => (double)p.SizeBytes);
            foreach (var partition in growable)
            {
                growth[partition] = RoundDown((long)(available * (partition.SizeBytes / total)), MiB);
            }
        }

        if (growth.Count == 0)
        {
            warnings.Add("No hay ninguna partición de datos NTFS que se pueda ampliar: se mantienen los tamaños.");
        }

        return growth;
    }

    /// <summary>Aplica el crecimiento y desplaza las particiones que van detrás de cada una que crece.</summary>
    private static List<PartitionPlacement> Grow(List<PartitionPlacement> placed, Dictionary<PartitionInfo, long> growth)
    {
        var result = new List<PartitionPlacement>();
        long shift = 0;
        foreach (var placement in placed)
        {
            var extra = growth.GetValueOrDefault(placement.Partition);
            result.Add(placement with { TargetOffset = placement.TargetOffset + shift, TargetSize = placement.TargetSize + extra });
            shift += extra;
        }

        return result;
    }

    private static long RoundUp(long value, long unit) => (value + unit - 1) / unit * unit;

    private static long RoundDown(long value, long unit) => value / unit * unit;
}
