using Cloner.Core.Devices;
using Cloner.Core.Inventory;
using Cloner.Core.Layout;
using Cloner.Core.Logging;
using Cloner.Core.PartitionTables;
using Microsoft.Extensions.Logging;

namespace Cloner.Core.Cloning;

/// <summary>Resultado de reparar el arranque de un clon.</summary>
public sealed record BootRepairResult(bool Succeeded, string Message)
{
    /// <summary>Archivo de registro de la reparación.</summary>
    public string? LogPath { get; init; }
}

/// <summary>Rehace el arranque de un disco de Windows clonado (por ejemplo, el que no arranca con 0xc000000e).</summary>
public interface IBootRepairService
{
    /// <summary>Repara el arranque del disco indicado. No lanza: los fallos van en el resultado.</summary>
    Task<BootRepairResult> RepairAsync(int diskNumber, IProgress<string>? progress);
}

/// <summary>
/// Reparación del arranque de un clon contra los discos reales. Pensada para el clon que se puso
/// en línea junto al original: Windows le cambió los identificadores y su arranque (BCD) apunta a
/// los antiguos. Requiere administrador.
/// </summary>
public sealed class BootRepairService(IDiskInventory inventory, string? logDirectory = null) : IBootRepairService
{
    /// <summary>
    /// Motivo por el que no tiene sentido reparar el arranque de ese disco, o nulo. No sabe desde
    /// qué disco se ejecuta la aplicación: eso se comprueba al reparar.
    /// </summary>
    public static string? CannotRepair(DiskInfo disk) => BootRepairer.Check(disk, hostDiskNumber: null);

    public Task<BootRepairResult> RepairAsync(int diskNumber, IProgress<string>? progress) =>
        Task.Factory.StartNew(() => Repair(diskNumber, progress), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private BootRepairResult Repair(int diskNumber, IProgress<string>? progress)
    {
        if (!Elevation.IsAdministrator())
        {
            return new BootRepairResult(false, "Para reparar el arranque hay que ejecutar la aplicación como administrador.");
        }

        using var log = FileLogger.Create(logDirectory ?? CloneService.DefaultLogDirectory, "reparacion");
        BootRepairResult result;
        try
        {
            var disks = inventory.GetDisks();
            var disk = disks.FirstOrDefault(d => d.Number == diskNumber);
            result = disk is null
                ? new BootRepairResult(false, $"No se encuentra el disco {diskNumber}. Actualiza la lista de discos.")
                : new BootRepairer(new Win32DiskAccess(virtualTargetOnly: false, inventory), log)
                    .Repair(disk, HostDisk.FindDiskNumber(AppContext.BaseDirectory, disks), progress);
        }
        catch (DiskInventoryException ex)
        {
            result = new BootRepairResult(false, ex.Message);
        }

        return result with { LogPath = log.FilePath };
    }
}

/// <summary>
/// Pone el clon en línea, busca su Windows y su partición de arranque, traduce en su registro la
/// letra C: a la identidad actual de su partición de Windows y rehace su BCD con bcdboot.
/// </summary>
internal sealed class BootRepairer(IDiskAccess access, ILogger log)
{
    public static string? Check(DiskInfo disk, int? hostDiskNumber)
    {
        ArgumentNullException.ThrowIfNull(disk);
        if (disk.IsSystemDisk)
        {
            return "Es el disco del sistema en uso: su arranque es el que está funcionando ahora.";
        }

        if (hostDiskNumber == disk.Number)
        {
            return "La aplicación se está ejecutando desde este disco.";
        }

        if (disk.PartitionStyle == PartitionStyle.Raw)
        {
            return "El disco no tiene tabla de particiones.";
        }

        if (!disk.Partitions.Any(p => p.Kind == PartitionKind.BasicData))
        {
            return "El disco no tiene ninguna partición de datos donde pueda estar Windows.";
        }

        if (disk.PartitionStyle == PartitionStyle.Gpt && !disk.Partitions.Any(p => p.Kind == PartitionKind.EfiSystem))
        {
            return "El disco es GPT y no tiene partición EFI, que es de donde arranca Windows en UEFI.";
        }

        return null;
    }

    public BootRepairResult Repair(DiskInfo disk, int? hostDiskNumber, IProgress<string>? progress)
    {
        ArgumentNullException.ThrowIfNull(disk);
        BootRepairLog.Started(log, disk.Number, disk.Model, disk.SerialNumber);
        var problem = Check(disk, hostDiskNumber);
        if (problem is not null)
        {
            BootRepairLog.Refused(log, problem);
            return new BootRepairResult(false, problem);
        }

        try
        {
            using var device = access.Open(DiskIdentity.From(disk), writable: true);
            if (disk.IsOffline)
            {
                progress?.Report("Poniendo el disco en línea");
                if (!access.SetOffline(device, offline: false))
                {
                    return Fail($"No se pudo poner el disco {disk.Number} en línea.");
                }

                device.RefreshLayout();
                BootRepairLog.Online(log);
            }

            progress?.Report("Esperando a que Windows monte sus volúmenes");
            var offsets = disk.Partitions
                .Where(p => p.Kind is PartitionKind.BasicData or PartitionKind.EfiSystem)
                .Select(p => p.OffsetBytes)
                .ToList();
            var finisher = access.Finisher;
            var volumes = finisher.WaitForVolumes(disk.Number, offsets);
            var windows = finisher.FindWindowsVolume(volumes.Values);
            if (windows is null)
            {
                return Fail($"No se encontró ninguna instalación de Windows en el disco {disk.Number}.");
            }

            // La tabla se lee ahora, ya en línea: al ponerlo en línea junto a otro disco con los
            // mismos identificadores, Windows puede habérselos cambiado.
            var sectorSize = device.SectorSize;
            using var buffer = new AlignedBuffer(PartitionTable.HeadBytes);
            device.Read(0, buffer.Span);
            var head = buffer.Span.ToArray();
            var table = PartitionTable.Read(head, sectorSize);
            var windowsOffset = volumes.First(v => string.Equals(v.Value, windows, StringComparison.OrdinalIgnoreCase)).Key;
            var uefi = table.Style == PartitionStyle.Gpt;
            var systemOffset = uefi
                ? disk.Partitions.FirstOrDefault(p => p.Kind == PartitionKind.EfiSystem)?.OffsetBytes
                : disk.Partitions.FirstOrDefault(p => p.IsActive)?.OffsetBytes;
            var system = systemOffset is { } offset && volumes.TryGetValue(offset, out var path) ? path : windows;

            byte[] identity;
            if (uefi)
            {
                if (!MountedDevices.PartitionGuids(head, table, sectorSize).TryGetValue(windowsOffset, out var guid))
                {
                    return Fail("La partición de Windows no aparece en la tabla GPT del disco.");
                }

                identity = MountedDevices.GptValue(guid);
            }
            else
            {
                identity = MountedDevices.MbrValue(BitConverter.ToUInt32(head, 440), windowsOffset);
            }

            progress?.Report("Rehaciendo el arranque");
            var translated = finisher.RepairBootInPlace(windows, system, uefi, identity);
            BootRepairLog.Repaired(log, uefi ? "UEFI" : "BIOS", translated);
            var message = $"Arranque del disco {disk.Number} rehecho: su partición {(uefi ? "EFI" : "de arranque")} apunta ahora a su propio Windows.";
            if (translated > 0)
            {
                message += " También se corrigió en su registro la letra de la unidad de Windows (C:).";
            }

            message += " Apaga el equipo, quita el disco original, deja este en su lugar y enciende.";
            return new BootRepairResult(true, message);
        }
        catch (Exception ex) when (ex is CloneException or IOException or UnauthorizedAccessException)
        {
            return Fail(ex.Message);
        }

        BootRepairResult Fail(string reason)
        {
            BootRepairLog.Failed(log, reason);
            return new BootRepairResult(false, reason);
        }
    }
}

internal static partial class BootRepairLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Reparación del arranque del disco {Number} ({Model}, serie {Serial}).")]
    public static partial void Started(ILogger logger, int number, string model, string? serial);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No se repara: {Reason}")]
    public static partial void Refused(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Disco puesto en línea.")]
    public static partial void Online(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Arranque rehecho en modo {Mode}; {Translated} valores de MountedDevices corregidos.")]
    public static partial void Repaired(ILogger logger, string mode, int translated);

    [LoggerMessage(Level = LogLevel.Error, Message = "La reparación falló: {Reason}")]
    public static partial void Failed(ILogger logger, string reason);
}
