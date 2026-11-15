using Cloner.Core.Inventory;
using Cloner.Core.PartitionTables;

namespace Cloner.Core.Cloning;

/// <summary>Opciones que elige el usuario para un clonado.</summary>
public sealed record CloneOptions
{
    /// <summary>Releer el destino al terminar y compararlo con lo escrito (RF-32).</summary>
    public bool Verify { get; init; } = true;

    /// <summary>Solo admite destinos virtuales (VHD/VHDX). Para pruebas y desarrollo.</summary>
    public bool VirtualTargetOnly { get; init; }

    /// <summary>Modo de copia (RF-12). Por defecto, la copia inteligente.</summary>
    public CloneMode Mode { get; init; } = CloneMode.UsedBlocks;
}

/// <summary>Modo de copia (RF-12).</summary>
public enum CloneMode
{
    /// <summary>Copia íntegra de todos los sectores del origen.</summary>
    SectorBySector,

    /// <summary>
    /// Copia inteligente: de los volúmenes NTFS solo se copian los bloques en uso y el espacio
    /// sin particionar no se copia. El resto de particiones se copia entero.
    /// </summary>
    UsedBlocks,
}

/// <summary>Tipo de operación de un paso del plan.</summary>
public enum CloneStepKind
{
    /// <summary>Escribir ceros en el destino.</summary>
    Clear,

    /// <summary>Copiar un tramo del origen a la misma posición del destino.</summary>
    Copy,

    /// <summary>
    /// Copiar una partición (o la parte de ella fuera de la cabeza del disco) a la misma posición
    /// del destino, leyendo del disco o de la instantánea de su volumen.
    /// </summary>
    CopyPartition,

    /// <summary>Escribir la copia de respaldo GPT al final del destino, ajustada a su tamaño.</summary>
    WriteBackupGpt,

    /// <summary>Copiar el principio del disco, con la tabla de particiones. Siempre es el último paso.</summary>
    WritePartitionTable,
}

/// <summary>Una operación del plan sobre el tramo [Offset, Offset + Length) del destino.</summary>
public sealed record CloneStep(CloneStepKind Kind, long Offset, long Length, string Description)
{
    public long End => Offset + Length;

    /// <summary>Si el paso deja en el destino datos que la verificación debe comprobar.</summary>
    public bool WritesData => Kind != CloneStepKind.Clear;

    /// <summary>Partición del origen que se copia (pasos <see cref="CloneStepKind.CopyPartition"/>).</summary>
    public PartitionInfo? Partition { get; init; }

    /// <summary>Se lee de una instantánea VSS del volumen, no del disco.</summary>
    public bool FromSnapshot { get; init; }

    /// <summary>Solo se copian los bloques que el sistema de archivos tiene en uso.</summary>
    public bool UsedBlocksOnly { get; init; }

    /// <summary>Bytes que se espera escribir; con copia inteligente, menos que <see cref="Length"/>.</summary>
    public long EstimatedBytes { get; init; } = -1;

    internal long ExpectedBytes => EstimatedBytes >= 0 ? EstimatedBytes : Length;
}

/// <summary>
/// Plan inmutable de un clonado: todo lo que se leerá y escribirá. Se muestra al usuario para
/// la confirmación (RS-03) y el ejecutor lo aplica sin tomar decisiones propias.
/// </summary>
public sealed record ClonePlan
{
    public required DiskIdentity Source { get; init; }
    public required DiskIdentity Target { get; init; }
    public required CloneOptions Options { get; init; }
    public CloneMode Mode => Options.Mode;

    /// <summary>El origen es el disco del sistema en uso: se clona en caliente desde instantáneas.</summary>
    public bool SourceIsSystemDisk { get; init; }

    /// <summary>El disco de origen es de solo lectura: sus volúmenes no pueden cambiar durante la copia.</summary>
    public bool SourceIsReadOnly { get; init; }

    /// <summary>Particiones del origen, para mostrar el progreso por partición.</summary>
    public required IReadOnlyList<PartitionInfo> SourcePartitions { get; init; }

    /// <summary>Particiones del destino que se perderán.</summary>
    public required IReadOnlyList<PartitionInfo> TargetPartitions { get; init; }

    public required IReadOnlyList<CloneStep> Steps { get; init; }

    /// <summary>La tabla GPT se ajusta al tamaño del destino, que es mayor que el origen.</summary>
    public bool AdjustsGpt { get; init; }

    public required IReadOnlyList<string> Warnings { get; init; }

    /// <summary>Tabla de particiones del origen tal como se leyó al planificar; se revalida al ejecutar.</summary>
    internal PartitionTableInfo SourceTable { get; init; } = new(PartitionStyle.Raw, null, null, "sin leer");

    /// <summary>
    /// Bytes de datos que se escribirán en el destino, sin contar el borrado previo. Con copia
    /// inteligente es una estimación: el tamaño exacto se conoce al leer los volúmenes.
    /// </summary>
    public long BytesToCopy => Steps.Where(s => s.WritesData).Sum(s => s.ExpectedBytes);

    /// <summary>Volúmenes del origen que se leen desde una instantánea.</summary>
    public IReadOnlyList<PartitionInfo> SnapshotPartitions =>
        Steps.Where(s => s.FromSnapshot && s.Partition is not null).Select(s => s.Partition!).Distinct().ToList();

    /// <summary>Espacio del destino que queda sin asignar tras el clon.</summary>
    public long ExtraTargetBytes => Target.SizeBytes - Source.SizeBytes;
}

/// <summary>Resultado de planificar: un plan, o los motivos que lo impiden.</summary>
public sealed record ClonePlanResult
{
    public ClonePlan? Plan { get; init; }

    /// <summary>Motivos que impiden el clonado. Si hay alguno, <see cref="Plan"/> es nulo.</summary>
    public IReadOnlyList<string> Problems { get; init; } = [];

    /// <summary>Avisos que no impiden el clonado.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    public static ClonePlanResult Blocked(params string[] problems) => new() { Problems = problems };
}
