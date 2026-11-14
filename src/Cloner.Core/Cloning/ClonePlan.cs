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
}

/// <summary>Modo de copia (RF-12).</summary>
public enum CloneMode
{
    /// <summary>Copia íntegra de todos los sectores del origen.</summary>
    SectorBySector,
}

/// <summary>Tipo de operación de un paso del plan.</summary>
public enum CloneStepKind
{
    /// <summary>Escribir ceros en el destino.</summary>
    Clear,

    /// <summary>Copiar un tramo del origen a la misma posición del destino.</summary>
    Copy,

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
    public CloneMode Mode { get; init; } = CloneMode.SectorBySector;

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

    /// <summary>Bytes de datos que se escriben en el destino (sin contar el borrado previo).</summary>
    public long BytesToCopy => Steps.Where(s => s.WritesData).Sum(s => s.Length);

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
