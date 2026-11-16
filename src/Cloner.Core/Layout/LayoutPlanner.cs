using System.Globalization;
using Cloner.Core.Inventory;
using Cloner.Core.PartitionTables;

namespace Cloner.Core.Layout;

/// <summary>
/// Calcula la distribución de las particiones en el destino: alineación a 1 MiB (RF-16),
/// reparto del espacio sobrante (RF-13) y reducción para un destino más pequeño (RF-14).
/// No toca discos.
/// </summary>
internal static class LayoutPlanner
{
    public const long MiB = 1024 * 1024;

    /// <summary>Margen mínimo sobre lo usado que conserva una partición reducida.</summary>
    public const long ShrinkMargin = 16 * MiB;

    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("es-ES");

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
        List<string> warnings,
        PartitionStyle? targetStyle = null,
        bool createEfi = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(table);

        // RF-17: al convertir, todas las particiones se colocan alineadas a 1 MiB (fuera de la tabla
        // nueva) y, si hace falta una partición EFI, detrás del espacio reservado para ella.
        var converting = StyleConversion.Requested(table, targetStyle);
        var style = converting ? targetStyle!.Value : table.Style;
        var partitions = source.Partitions
            .Where(p => converting ? StyleConversion.Keeps(p, style) : p.Kind != PartitionKind.Extended)
            .OrderBy(p => p.OffsetBytes)
            .ToList();
        var same = TargetLayout.Same(partitions);
        var sectorSize = source.LogicalSectorSize;
        var smaller = target.SizeBytes < source.SizeBytes;
        if (partitions.Count == 0 || sectorSize <= 0 || table.Style == PartitionStyle.Raw)
        {
            if (smaller)
            {
                problems.Add(SmallerMessage(source, target) + " El origen no tiene particiones que se puedan ajustar: hace falta un destino al menos igual de grande.");
            }

            return same;
        }

        var wantAlign = alignment == AlignmentMode.Always || (alignment == AlignmentMode.Auto && target.Media == MediaKind.Ssd);
        var misaligned = partitions.Any(p => p.OffsetBytes % MiB != 0);
        var wantGrow = sizeMode != SizeMode.Keep && target.SizeBytes > source.SizeBytes;
        if (!(wantAlign && misaligned) && !wantGrow && !smaller && !converting)
        {
            return same;
        }

        // Las particiones lógicas dependen de la cadena de EBR, que esta versión no reescribe.
        if (source.Partitions.Any(p => p.Kind == PartitionKind.Extended))
        {
            problems.Add(smaller
                ? SmallerMessage(source, target) + " El origen tiene particiones lógicas (partición extendida MBR), que no se pueden mover ni reducir."
                : "El origen tiene particiones lógicas (partición extendida MBR): no se pueden mover ni ampliar. Elige mantener los tamaños.");
            return same;
        }

        var usableEnd = UsableEnd(style, table, target.SizeBytes, sectorSize);
        var reserve = createEfi ? StyleConversion.EfiBytes + StyleConversion.MsrBytes : 0;
        var align = (wantAlign && misaligned) || converting;
        var placed = Place(partitions, align, reserve);
        IReadOnlyList<CreatedPartition> created = createEfi
            ? [
                new CreatedPartition(PartitionKind.EfiSystem, PartitionTable.HeadBytes, StyleConversion.EfiBytes),
                new CreatedPartition(PartitionKind.MicrosoftReserved, PartitionTable.HeadBytes + StyleConversion.EfiBytes, StyleConversion.MsrBytes),
            ]
            : [];
        if (smaller || (converting && placed[^1].TargetEnd > usableEnd))
        {
            var prefix = smaller ? SmallerMessage(source, target) : $"Con la tabla {StyleConversion.Name(style)} las particiones no caben en el destino.";
            var fitted = FitSmaller(source, target, partitions, placed, usableEnd, align, converting ? AlignmentMode.Always : alignment, prefix, problems, warnings);
            return fitted is null ? same : fitted with { Style = converting ? style : null, Created = created };
        }

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

        return new TargetLayout
        {
            Placements = placed,
            Aligned = align && misaligned,
            Style = converting ? style : null,
            Created = created,
        };
    }

    /// <summary>
    /// Destino más pequeño (RF-14): si las particiones no caben tal cual, se reducen las NTFS de
    /// datos con más espacio libre, sin bajar de lo usado más un margen. Las que van detrás se
    /// desplazan. Devuelve nulo (con el motivo en <paramref name="problems"/>) si no caben.
    /// </summary>
    private static TargetLayout? FitSmaller(
        DiskInfo source,
        DiskInfo target,
        List<PartitionInfo> partitions,
        List<PartitionPlacement> placed,
        long usableEnd,
        bool aligned,
        AlignmentMode alignment,
        string prefix,
        List<string> problems,
        List<string> warnings)
    {
        var shrunk = Shrink(placed, usableEnd, out var missing);
        if (shrunk is null && aligned && alignment != AlignmentMode.Always)
        {
            var unaligned = Shrink(Place(partitions, align: false), usableEnd, out var missingUnaligned);
            if (unaligned is not null)
            {
                warnings.Add("Las particiones no se alinean a 1 MiB porque, alineadas, no cabrían en el destino.");
                shrunk = unaligned;
                aligned = false;
            }

            missing = Math.Min(missing, missingUnaligned);
        }

        if (shrunk is null)
        {
            problems.Add(prefix + (partitions.Any(IsShrinkable)
                ? $" Los datos no caben: aun reduciendo las particiones NTFS de datos hasta lo que ocupan, harían falta {Size(missing)} más. Elige un destino de al menos {Size(target.SizeBytes + missing)}."
                : " Los datos no caben y el origen no tiene particiones NTFS de datos que se puedan reducir: hace falta un destino tan grande como lo particionado del origen."));
            return null;
        }

        return new TargetLayout { Placements = shrunk, Aligned = aligned, FitsSmallerTarget = true };
    }

    /// <summary>Reduce lo justo para que todo acabe antes de <paramref name="usableEnd"/>; nulo si no es posible.</summary>
    private static List<PartitionPlacement>? Shrink(List<PartitionPlacement> placed, long usableEnd, out long missing)
    {
        missing = 0;
        var deficit = placed[^1].TargetEnd - usableEnd;
        if (deficit <= 0)
        {
            return placed;
        }

        deficit = RoundUp(deficit, MiB);
        var cuts = new Dictionary<PartitionInfo, long>();
        foreach (var placement in placed.Where(p => IsShrinkable(p.Partition)).OrderByDescending(p => p.TargetSize - MinimumSize(p.Partition)))
        {
            var room = RoundDown(placement.TargetSize - MinimumSize(placement.Partition), MiB);
            var cut = Math.Min(room, deficit);
            if (cut > 0)
            {
                cuts[placement.Partition] = -cut;
                deficit -= cut;
            }

            if (deficit == 0)
            {
                break;
            }
        }

        if (deficit > 0)
        {
            missing = deficit;
            return null;
        }

        return Grow(placed, cuts);
    }

    /// <summary>Tamaño mínimo de una partición reducida: lo usado más un 10 % (al menos <see cref="ShrinkMargin"/>), en MiB enteros.</summary>
    public static long MinimumSize(PartitionInfo partition)
    {
        ArgumentNullException.ThrowIfNull(partition);
        var used = partition.Volume?.UsedBytes ?? partition.SizeBytes;
        return RoundUp(used + Math.Max(used / 10, ShrinkMargin), MiB);
    }

    /// <summary>Se puede reducir una partición NTFS de datos con volumen montado: se copia archivo a archivo.</summary>
    public static bool IsShrinkable(PartitionInfo partition) => IsGrowable(partition) && partition.Volume is not null;

    private static string SmallerMessage(DiskInfo source, DiskInfo target)
    {
        var targetSize = Size(target.SizeBytes);
        var sourceSize = Size(source.SizeBytes);
        return targetSize == sourceSize
            ? $"El destino es {(source.SizeBytes - target.SizeBytes).ToString("N0", Culture)} bytes más pequeño que el origen ({target.SizeBytes.ToString("N0", Culture)} frente a {source.SizeBytes.ToString("N0", Culture)} bytes)."
            : $"El destino ({targetSize}) es más pequeño que el origen ({sourceSize}).";
    }

    private static string Size(long bytes) => ByteSize.Format(bytes, Culture);

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
    private static long UsableEnd(PartitionStyle style, PartitionTableInfo table, long targetBytes, int sectorSize)
    {
        var targetSectors = targetBytes / sectorSize;
        if (style == PartitionStyle.Gpt)
        {
            var entries = table.Gpt?.EntriesSectors ?? StyleConversion.GptEntriesSectors(sectorSize);
            return (targetSectors - 1 - entries) * sectorSize;
        }

        return Math.Min(targetSectors, MbrMaxSectors) * sectorSize;
    }

    /// <summary>
    /// Coloca las particiones en orden y con su tamaño, conservando los huecos entre ellas. Al
    /// alinear, ninguna empieza por debajo de 1 MiB ni fuera de un múltiplo de 1 MiB.
    /// </summary>
    private static List<PartitionPlacement> Place(List<PartitionInfo> partitions, bool align, long reserve = 0)
    {
        var placed = new List<PartitionPlacement>();
        long shift = 0;
        long previousEnd = 0;
        foreach (var partition in partitions)
        {
            var offset = partition.OffsetBytes + shift;
            if (align)
            {
                offset = RoundUp(Math.Max(offset, PartitionTable.HeadBytes + reserve), MiB);
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
