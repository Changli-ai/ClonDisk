using Cloner.Core.Cloning;
using Microsoft.Extensions.Logging;

namespace Cloner.Core.Logging;

/// <summary>Mensajes del registro de clonado (RF-33), generados en compilación.</summary>
internal static partial class CloneLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Clonado sector a sector. Verificación: {Verify}. Ajuste de la GPT: {AdjustGpt}.")]
    public static partial void PlanStarted(ILogger logger, string verify, string adjustGpt);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Role}: {Disk}; serie {Serial}; identificador {Identifier}; ubicación {Location}.")]
    public static partial void PlanDisk(ILogger logger, string role, string disk, string? serial, string? identifier, string? location);

    [LoggerMessage(Level = LogLevel.Information, Message = "Se borra del destino: {Partition}.")]
    public static partial void TargetPartitionErased(ILogger logger, string partition);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Warning}")]
    public static partial void PlanWarning(ILogger logger, string warning);

    [LoggerMessage(Level = LogLevel.Information, Message = "Volúmenes bloqueados. Empieza la copia.")]
    public static partial void VolumesLocked(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Paso: {Step} (bytes {Start}–{End}).")]
    public static partial void StepStarted(ILogger logger, string step, long start, long end);

    [LoggerMessage(Level = LogLevel.Information, Message = "Progreso: {Percent:P0}.")]
    public static partial void Progress(ILogger logger, double percent);

    [LoggerMessage(Level = LogLevel.Information, Message = "Copia terminada: {Bytes} bytes escritos. Huella XxHash128: {Hash}.")]
    public static partial void CopyFinished(ILogger logger, long bytes, string? hash);

    [LoggerMessage(Level = LogLevel.Information, Message = "Verificación correcta.")]
    public static partial void VerificationPassed(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "La verificación no coincide en el bloque que empieza en el byte {Offset}.")]
    public static partial void VerificationMismatch(ILogger logger, long offset);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Se ha borrado de nuevo la tabla de particiones del destino.")]
    public static partial void TargetInvalidated(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Error durante el clonado.")]
    public static partial void CloneError(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Windows no pudo releer la tabla de particiones del destino.")]
    public static partial void RefreshFailed(ILogger logger, Exception exception);

    [LoggerMessage(Message = "Resultado: {Outcome}. {Message} Duración: {Elapsed}.")]
    public static partial void Finished(ILogger logger, LogLevel level, CloneOutcome outcome, string message, TimeSpan elapsed);

    [LoggerMessage(Level = LogLevel.Error, Message = "Revalidación fallida: {Change}")]
    public static partial void RevalidationFailed(ILogger logger, string change);

    [LoggerMessage(Level = LogLevel.Error, Message = "No se pudo revalidar el plan.")]
    public static partial void RevalidationError(ILogger logger, Exception exception);
}
