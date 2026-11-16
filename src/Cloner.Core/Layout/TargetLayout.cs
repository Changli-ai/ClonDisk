using Cloner.Core.Inventory;

namespace Cloner.Core.Layout;

/// <summary>Qué hacer con el espacio sobrante cuando el destino es mayor (RF-13).</summary>
public enum SizeMode
{
    /// <summary>Las particiones conservan su tamaño; lo sobrante queda sin asignar.</summary>
    Keep,

    /// <summary>La partición principal (la de Windows o la de datos mayor) crece hasta ocupar lo sobrante.</summary>
    ExtendMain,

    /// <summary>Lo sobrante se reparte entre las particiones de datos NTFS en proporción a su tamaño.</summary>
    Proportional,
}

/// <summary>Alineación de las particiones a 1 MiB (RF-16).</summary>
public enum AlignmentMode
{
    /// <summary>Solo si el destino es un SSD.</summary>
    Auto,
    Always,
    Never,
}

/// <summary>Dónde queda una partición del origen en el destino y con qué tamaño.</summary>
public sealed record PartitionPlacement(PartitionInfo Partition, long TargetOffset, long TargetSize)
{
    public long TargetEnd => TargetOffset + TargetSize;

    public bool Moves => TargetOffset != Partition.OffsetBytes;

    public bool Grows => TargetSize > Partition.SizeBytes;

    /// <summary>
    /// La partición se reduce para caber en un destino más pequeño (RF-14): se formatea de nuevo
    /// y su contenido se copia archivo a archivo.
    /// </summary>
    public bool Shrinks => TargetSize < Partition.SizeBytes;

    public long Growth => TargetSize - Partition.SizeBytes;
}

/// <summary>Distribución de las particiones en el destino.</summary>
public sealed record TargetLayout
{
    public required IReadOnlyList<PartitionPlacement> Placements { get; init; }

    /// <summary>Se alinearon las particiones a 1 MiB.</summary>
    public bool Aligned { get; init; }

    /// <summary>El destino es más pequeño que el origen y la distribución se ajustó a él (RF-14).</summary>
    public bool FitsSmallerTarget { get; init; }

    /// <summary>Estilo de la tabla del destino si se convierte (RF-17); nulo si es el del origen.</summary>
    public PartitionStyle? Style { get; init; }

    /// <summary>Particiones que no están en el origen y se crean en el destino.</summary>
    public IReadOnlyList<CreatedPartition> Created { get; init; } = [];

    /// <summary>Alguna partición cambia de posición o de tamaño respecto al origen.</summary>
    public bool Changes => FitsSmallerTarget || Style is not null || Created.Count > 0 || Placements.Any(p => p.Moves || p.Grows || p.Shrinks);

    public PartitionPlacement? Find(PartitionInfo partition) => Placements.FirstOrDefault(p => p.Partition == partition);

    /// <summary>Distribución idéntica a la del origen.</summary>
    public static TargetLayout Same(IEnumerable<PartitionInfo> partitions) => new()
    {
        Placements = partitions
            .Where(p => p.Kind != PartitionKind.Extended)
            .Select(p => new PartitionPlacement(p, p.OffsetBytes, p.SizeBytes))
            .ToList(),
    };
}
