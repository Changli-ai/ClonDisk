namespace Cloner.Core.Cloning;

/// <summary>Fase en curso de un clonado.</summary>
public enum ClonePhase
{
    Preparing,
    Copying,
    Verifying,
    Finishing,
}

/// <summary>Estado de avance (RF-30). Los bytes cuentan copia y verificación juntas.</summary>
public sealed record CloneProgress
{
    public required ClonePhase Phase { get; init; }
    public long BytesDone { get; init; }
    public long BytesTotal { get; init; }

    /// <summary>Velocidad reciente en bytes por segundo; 0 mientras no hay datos suficientes.</summary>
    public double BytesPerSecond { get; init; }

    /// <summary>Tiempo restante estimado; nulo mientras no se puede calcular.</summary>
    public TimeSpan? Remaining { get; init; }

    /// <summary>Partición o paso en curso, para mostrar.</summary>
    public string? CurrentItem { get; init; }

    public double Fraction => BytesTotal > 0 ? Math.Clamp((double)BytesDone / BytesTotal, 0, 1) : 0;
}

/// <summary>Cómo terminó un clonado.</summary>
public enum CloneOutcome
{
    Succeeded,
    Cancelled,
    Failed,
    VerificationFailed,
}

/// <summary>Resultado de ejecutar un plan.</summary>
public sealed record CloneResult
{
    public required CloneOutcome Outcome { get; init; }

    /// <summary>Resumen para el usuario.</summary>
    public required string Message { get; init; }

    /// <summary>Se llegó a escribir en el destino.</summary>
    public bool TargetModified { get; init; }

    /// <summary>El destino tiene la copia completa y su tabla de particiones.</summary>
    public bool TargetComplete { get; init; }

    /// <summary>La verificación se hizo y coincidió.</summary>
    public bool Verified { get; init; }

    public long BytesWritten { get; init; }
    public TimeSpan Elapsed { get; init; }

    /// <summary>Huella (XxHash128) de todo lo escrito, en hexadecimal.</summary>
    public string? DataHash { get; init; }

    /// <summary>Archivo de registro de la operación (RF-33).</summary>
    public string? LogPath { get; init; }
}
