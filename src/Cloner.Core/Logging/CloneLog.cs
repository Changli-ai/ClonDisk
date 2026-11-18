using Cloner.Core.Cloning;
using Microsoft.Extensions.Logging;

namespace Cloner.Core.Logging;

/// <summary>Mensajes del registro de clonado (RF-33), generados en compilación.</summary>
internal static partial class CloneLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Clonado: {Mode}. Verificación: {Verify}. Ajuste de la GPT: {AdjustGpt}.")]
    public static partial void PlanStarted(ILogger logger, string mode, string verify, string adjustGpt);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Role}: {Disk}; serie {Serial}; identificador {Identifier}; ubicación {Location}.")]
    public static partial void PlanDisk(ILogger logger, string role, string disk, string? serial, string? identifier, string? location);

    [LoggerMessage(Level = LogLevel.Information, Message = "Se borra del destino: {Partition}.")]
    public static partial void TargetPartitionErased(ILogger logger, string partition);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Warning}")]
    public static partial void PlanWarning(ILogger logger, string warning);

    [LoggerMessage(Level = LogLevel.Information, Message = "Volúmenes bloqueados. Empieza la copia.")]
    public static partial void VolumesLocked(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Instantáneas creadas: {Volumes}.")]
    public static partial void SnapshotsCreated(ILogger logger, string volumes);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No se pudo bloquear {Volume}; se copia igualmente porque Windows no lo modifica en uso normal. {Reason}")]
    public static partial void LockSkipped(ILogger logger, string volume, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Plan detallado de «{Step}»: {Bytes} bytes en {Ranges} tramos.")]
    public static partial void StepExpanded(ILogger logger, string step, long bytes, int ranges);

    [LoggerMessage(Level = LogLevel.Information, Message = "Paso: {Step} (bytes {Start}–{End}).")]
    public static partial void StepStarted(ILogger logger, string step, long start, long end);

    [LoggerMessage(Level = LogLevel.Information, Message = "Progreso: {Percent:P0}.")]
    public static partial void Progress(ILogger logger, double percent);

    [LoggerMessage(Level = LogLevel.Information, Message = "Copia terminada: {Bytes} bytes escritos. Huella XxHash128: {Hash}.")]
    public static partial void CopyFinished(ILogger logger, long bytes, string? hash);

    [LoggerMessage(Level = LogLevel.Information, Message = "Windows montó {Count} volúmenes del clon.")]
    public static partial void CloneMounted(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Formateada {Partition} en el destino ({Bytes} bytes).")]
    public static partial void VolumeFormatted(ILogger logger, string partition, long bytes);

    [LoggerMessage(Level = LogLevel.Information, Message = "Copiados los archivos de {Partition}: {Files} archivos, {Directories} carpetas, {Links} enlaces duros, {Bytes} bytes; {Skipped} excluidos y {Errors} con error.")]
    public static partial void FilesCopied(ILogger logger, string partition, long files, long directories, long links, long bytes, long skipped, int errors);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No se pudo poner la etiqueta del origen al volumen del clon: {Reason}")]
    public static partial void LabelFailed(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Esperando a que Windows libere los volúmenes del clon antes de quitarlos.")]
    public static partial void QuietWait(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No copiado: {Error}")]
    public static partial void FileNotCopied(ILogger logger, string error);

    [LoggerMessage(Level = LogLevel.Error, Message = "No se pudo invalidar el destino tras el fallo.")]
    public static partial void InvalidateFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Ampliada {Partition} a {Bytes} bytes.")]
    public static partial void VolumeExtended(ILogger logger, string partition, long bytes);

    [LoggerMessage(Level = LogLevel.Information, Message = "Arranque del clon rehecho con bcdboot; {Count} valores de MountedDevices traducidos.")]
    public static partial void BootRepaired(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Escrita la tabla definitiva con los identificadores del origen.")]
    public static partial void FinalIdentity(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Verificación correcta.")]
    public static partial void VerificationPassed(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "La verificación no coincide en {Count} bloques; el primero empieza en el byte {Offset}.")]
    public static partial void VerificationMismatch(ILogger logger, int count, long offset);

    [LoggerMessage(Level = LogLevel.Error, Message = "Diagnóstico: {Text}")]
    public static partial void Diagnosis(ILogger logger, string text);

    [LoggerMessage(Level = LogLevel.Information, Message = "La tabla se copia con identificadores provisionales; los del origen se ponen al final, con el destino sin conexión.")]
    public static partial void ProvisionalIdentity(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Destino sin conexión para poner los identificadores definitivos: {Offline}.")]
    public static partial void TargetOffline(ILogger logger, bool offline);

    [LoggerMessage(Level = LogLevel.Warning, Message = "La tabla definitiva cambió después de escribirla: {Summary}")]
    public static partial void FinalTableChanged(ILogger logger, string summary);

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
