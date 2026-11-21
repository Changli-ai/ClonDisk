using System.Globalization;
using System.Management;
using Cloner.Core.Devices;
using Cloner.Core.Inventory;
using Cloner.Core.Logging;
using Cloner.Core.Snapshots;
using Microsoft.Extensions.Logging;

namespace Cloner.Core.Cloning;

/// <summary>Resultado de limpiar y formatear un disco.</summary>
public sealed record DiskPreparationResult(bool Succeeded, string Message)
{
    /// <summary>Letra que recibió el volumen nuevo, si Windows le dio una.</summary>
    public char? DriveLetter { get; init; }

    /// <summary>Archivo de registro de la operación.</summary>
    public string? LogPath { get; init; }
}

/// <summary>
/// Deja un disco como nuevo: borra todas sus particiones (también las de arranque y recuperación
/// que un formateo deja), lo inicializa en GPT, crea una sola partición NTFS que ocupa todo el disco
/// y le asigna una letra. Es lo mismo que «clean», «create partition primary», «format» y
/// «assign» de diskpart.
/// </summary>
public interface IDiskPreparationService
{
    /// <summary>Limpia y formatea el disco. No lanza: los fallos van en el resultado.</summary>
    Task<DiskPreparationResult> PrepareAsync(DiskIdentity disk, IProgress<string>? progress);
}

/// <summary>Limpieza de discos reales con la API de administración de almacenamiento (WMI). Requiere administrador.</summary>
public sealed class DiskPreparationService(IDiskInventory inventory, string? logDirectory = null, bool virtualOnly = false) : IDiskPreparationService
{
    /// <summary>Etiqueta del volumen que se crea.</summary>
    public const string VolumeLabel = "Datos";

    /// <summary>Motivo por el que no se puede limpiar el disco, o nulo.</summary>
    public static string? CannotPrepare(DiskInfo disk, int? hostDiskNumber) => DiskPreparer.Check(disk, hostDiskNumber, virtualOnly: false);

    public Task<DiskPreparationResult> PrepareAsync(DiskIdentity disk, IProgress<string>? progress)
    {
        ArgumentNullException.ThrowIfNull(disk);
        return Task.Factory.StartNew(() => Prepare(disk, progress), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    private DiskPreparationResult Prepare(DiskIdentity expected, IProgress<string>? progress)
    {
        if (!Elevation.IsAdministrator())
        {
            return new DiskPreparationResult(false, "Para limpiar un disco hay que ejecutar la aplicación como administrador.");
        }

        using var log = FileLogger.Create(logDirectory ?? CloneService.DefaultLogDirectory, "limpieza");
        DiskPreparationResult result;
        try
        {
            // RS-04: se revalida justo antes de borrar, por si el número de disco cambió.
            var disks = inventory.GetDisks();
            var disk = disks.FirstOrDefault(d => d.Number == expected.Number);
            var mismatch = disk is null ? "ya no existe" : expected.FindMismatch(disk);
            if (mismatch is not null)
            {
                result = new DiskPreparationResult(false, $"No se ha borrado nada: el disco {expected.Number} {mismatch}. Actualiza la lista de discos.");
                DiskPreparationLog.Refused(log, result.Message);
            }
            else
            {
                result = new DiskPreparer(log, virtualOnly).Prepare(disk!, DiskProtection.HostDiskNumber(disks), progress);
            }
        }
        catch (DiskInventoryException ex)
        {
            result = new DiskPreparationResult(false, ex.Message);
        }

        return result with { LogPath = log.FilePath };
    }
}

/// <summary>Los pasos de la limpieza sobre un disco concreto.</summary>
internal sealed class DiskPreparer(ILogger log, bool virtualOnly)
{
    private const string StorageNamespace = @"root\Microsoft\Windows\Storage";
    private const ushort GptStyle = 2;
    private static readonly TimeSpan QuietPeriod = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan VolumeTimeout = TimeSpan.FromSeconds(30);

    /// <summary>RS-02: nunca se limpia un disco que este equipo necesita para funcionar.</summary>
    public static string? Check(DiskInfo disk, int? hostDiskNumber, bool virtualOnly)
    {
        ArgumentNullException.ThrowIfNull(disk);
        if (DiskProtection.WhyLocked(disk, hostDiskNumber) is { } locked)
        {
            return locked;
        }

        if (disk.IsReadOnly)
        {
            return "El disco está marcado como de solo lectura.";
        }

        if (virtualOnly && disk.Bus != BusKind.Virtual)
        {
            return "Solo se admiten discos virtuales (VHD/VHDX) y este no lo es.";
        }

        return null;
    }

    public DiskPreparationResult Prepare(DiskInfo disk, int? hostDiskNumber, IProgress<string>? progress)
    {
        DiskPreparationLog.Started(log, disk.Number, disk.Model, disk.SerialNumber, disk.Partitions.Count);
        var problem = Check(disk, hostDiskNumber, virtualOnly);
        if (problem is not null)
        {
            DiskPreparationLog.Refused(log, problem);
            return new DiskPreparationResult(false, $"No se puede limpiar el disco {disk.Number}: {problem}");
        }

        try
        {
            WaitAfterSnapshots(progress);
            using var storageDisk = FindDisk(disk.Number);
            if (disk.IsOffline)
            {
                progress?.Report("Poniendo el disco en línea");
                Invoke(storageDisk, "Online", null, "poner el disco en línea");
            }

            if (disk.PartitionStyle != PartitionStyle.Raw)
            {
                progress?.Report("Borrando todas las particiones");
                Invoke(storageDisk, "Clear", new Dictionary<string, object>
                {
                    ["RemoveData"] = true,
                    ["RemoveOEM"] = true,
                    ["ZeroOutEntireDisk"] = false,
                }, "borrar las particiones");
                DiskPreparationLog.Cleared(log);
            }

            progress?.Report("Inicializando el disco en GPT");
            Invoke(storageDisk, "Initialize", new Dictionary<string, object> { ["PartitionStyle"] = GptStyle }, "inicializar el disco");

            progress?.Report("Creando la partición");
            using var created = Invoke(storageDisk, "CreatePartition", new Dictionary<string, object>
            {
                ["UseMaximumSize"] = true,
                ["AssignDriveLetter"] = true,
            }, "crear la partición");
            var partitionNumber = Convert.ToUInt32((created["CreatedPartition"] as ManagementBaseObject)?["PartitionNumber"] ?? 0u, CultureInfo.InvariantCulture);
            var volume = WaitForVolume(disk.Number, partitionNumber);

            progress?.Report("Formateando en NTFS");
            FormatNtfs(volume.Path);
            var letter = volume.Letter ?? LetterOf(disk.Number, partitionNumber);
            DiskPreparationLog.Prepared(log, partitionNumber, letter);

            var where = letter is { } l ? $" con la letra {l}:" : " (Windows no le ha dado letra; asígnala en Administración de discos)";
            return new DiskPreparationResult(true, $"El disco {disk.Number} está limpio y tiene una sola partición NTFS «{DiskPreparationService.VolumeLabel}»{where}.")
            {
                DriveLetter = letter,
            };
        }
        catch (Exception ex) when (ex is CloneException or ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            DiskPreparationLog.Failed(log, ex.Message);
            return new DiskPreparationResult(false, ex.Message);
        }
    }

    /// <summary>Quitar un volumen poco después de liberar una instantánea suya puede bloquear el equipo (volsnap).</summary>
    private static void WaitAfterSnapshots(IProgress<string>? progress)
    {
        if (VssSnapshotSet.LastReleasedUtc is not { } released)
        {
            return;
        }

        var end = released + QuietPeriod;
        while (DateTime.UtcNow < end)
        {
            progress?.Report($"Pausa de seguridad tras el clonado: {(int)Math.Ceiling((end - DateTime.UtcNow).TotalSeconds)} s");
            Thread.Sleep(TimeSpan.FromMilliseconds(Math.Min(1000, Math.Max(0, (end - DateTime.UtcNow).TotalMilliseconds))));
        }
    }

    private static ManagementObject FindDisk(int number)
    {
        using var searcher = new ManagementObjectSearcher(StorageNamespace, $"SELECT * FROM MSFT_Disk WHERE Number = {number.ToString(CultureInfo.InvariantCulture)}");
        using var results = searcher.Get();
        return results.Cast<ManagementObject>().FirstOrDefault()
            ?? throw new CloneException($"Windows no encuentra el disco {number}.");
    }

    /// <summary>Llama a un método de la API de almacenamiento y lanza con el mensaje de Windows si falla.</summary>
    private static ManagementBaseObject Invoke(ManagementObject target, string method, IReadOnlyDictionary<string, object>? arguments, string action)
    {
        using var input = arguments is null ? null : target.GetMethodParameters(method);
        if (input is not null)
        {
            foreach (var (name, value) in arguments!)
            {
                input[name] = value;
            }
        }

        var output = target.InvokeMethod(method, input, null)
            ?? throw new CloneException($"Windows no respondió al {action}.");
        var code = Convert.ToUInt32(output["ReturnValue"] ?? uint.MaxValue, CultureInfo.InvariantCulture);
        if (code != 0)
        {
            var detail = (output.Properties.Cast<PropertyData>().FirstOrDefault(p => p.Name == "ExtendedStatus")?.Value as ManagementBaseObject)?["Message"] as string;
            output.Dispose();
            throw new CloneException($"Windows no pudo {action} (código {code.ToString(CultureInfo.InvariantCulture)}){(string.IsNullOrWhiteSpace(detail) ? "." : $": {detail.Trim()}")}");
        }

        return output;
    }

    /// <summary>Espera a que Windows cree el volumen de la partición nueva.</summary>
    private static (string Path, char? Letter) WaitForVolume(int diskNumber, uint partitionNumber)
    {
        var deadline = DateTime.UtcNow + VolumeTimeout;
        while (true)
        {
            var paths = AccessPaths(diskNumber, partitionNumber);
            var volume = paths.FirstOrDefault(p => p.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase));
            if (volume is not null)
            {
                return (volume, Letter(paths));
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new CloneException("Windows no creó el volumen de la partición nueva a tiempo.");
            }

            Thread.Sleep(500);
        }
    }

    private static char? LetterOf(int diskNumber, uint partitionNumber) => Letter(AccessPaths(diskNumber, partitionNumber));

    private static char? Letter(IEnumerable<string> paths) =>
        paths.FirstOrDefault(p => p.Length == 3 && p[1] == ':' && char.IsAsciiLetter(p[0])) is { } path ? char.ToUpperInvariant(path[0]) : null;

    private static List<string> AccessPaths(int diskNumber, uint partitionNumber)
    {
        using var searcher = new ManagementObjectSearcher(
            StorageNamespace,
            $"SELECT AccessPaths FROM MSFT_Partition WHERE DiskNumber = {diskNumber.ToString(CultureInfo.InvariantCulture)} AND PartitionNumber = {partitionNumber.ToString(CultureInfo.InvariantCulture)}");
        using var results = searcher.Get();
        return results.Cast<ManagementObject>()
            .SelectMany(p => p["AccessPaths"] as string[] ?? [])
            .ToList();
    }

    private static void FormatNtfs(string volumePath)
    {
        using var searcher = new ManagementObjectSearcher(
            StorageNamespace,
            $"SELECT * FROM MSFT_Volume WHERE Path = '{volumePath.Replace(@"\", @"\\", StringComparison.Ordinal)}'");
        using var results = searcher.Get();
        using var volume = results.Cast<ManagementObject>().FirstOrDefault()
            ?? throw new CloneException($"No se encontró el volumen {volumePath} para formatearlo.");
        using var output = Invoke(volume, "Format", new Dictionary<string, object>
        {
            ["FileSystem"] = "NTFS",
            ["FileSystemLabel"] = DiskPreparationService.VolumeLabel,
            ["Full"] = false,
            ["Force"] = true,
        }, "formatear el volumen");
    }
}

internal static partial class DiskPreparationLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Limpieza del disco {Number} ({Model}, serie {Serial}), con {Partitions} particiones.")]
    public static partial void Started(ILogger logger, int number, string model, string? serial, int partitions);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No se limpia: {Reason}")]
    public static partial void Refused(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Particiones borradas.")]
    public static partial void Cleared(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Partición {Partition} creada y formateada en NTFS; letra {Letter}.")]
    public static partial void Prepared(ILogger logger, uint partition, char? letter);

    [LoggerMessage(Level = LogLevel.Error, Message = "La limpieza falló: {Reason}")]
    public static partial void Failed(ILogger logger, string reason);
}
