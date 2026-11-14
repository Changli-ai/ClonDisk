using Cloner.Core.Devices;
using Cloner.Core.Inventory;
using Cloner.Core.Logging;
using Cloner.Core.PartitionTables;

namespace Cloner.Core.Cloning;

/// <summary>Punto de entrada del clonado para la CLI y la interfaz.</summary>
public interface ICloneService
{
    /// <summary>Planifica un clonado sin escribir nada. Lee la tabla de particiones del origen.</summary>
    ClonePlanResult CreatePlan(int sourceNumber, int targetNumber, CloneOptions options);

    /// <summary>Ejecuta un plan en segundo plano. No lanza excepciones: el resultado describe cualquier fallo.</summary>
    Task<CloneResult> RunAsync(ClonePlan plan, IProgress<CloneProgress>? progress, CancellationToken cancellationToken);
}

/// <summary>Clonado contra los discos reales del equipo. Requiere administrador.</summary>
public sealed class CloneService : ICloneService
{
    private readonly IDiskInventory _inventory;

    public CloneService(IDiskInventory inventory, string? logDirectory = null)
    {
        _inventory = inventory;
        LogDirectory = logDirectory ?? DefaultLogDirectory;
    }

    /// <summary>Carpeta de los registros: %LOCALAPPDATA%\Cloner\Registros.</summary>
    public static string DefaultLogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cloner", "Registros");

    public string LogDirectory { get; }

    public ClonePlanResult CreatePlan(int sourceNumber, int targetNumber, CloneOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!Elevation.IsAdministrator())
        {
            return ClonePlanResult.Blocked("Para clonar hay que ejecutar la aplicación como administrador.");
        }

        IReadOnlyList<DiskInfo> disks;
        try
        {
            disks = _inventory.GetDisks();
        }
        catch (DiskInventoryException ex)
        {
            return ClonePlanResult.Blocked(ex.Message);
        }

        var source = disks.FirstOrDefault(d => d.Number == sourceNumber);
        var target = disks.FirstOrDefault(d => d.Number == targetNumber);
        if (source is null || target is null)
        {
            return ClonePlanResult.Blocked($"No existe el disco {(source is null ? sourceNumber : targetNumber)}.");
        }

        var environment = new CloneEnvironment { HostDiskNumber = HostDisk.FindDiskNumber(AppContext.BaseDirectory, disks) };
        return ClonePlanner.Create(source, target, TryReadTable(source), environment, options);
    }

    public Task<CloneResult> RunAsync(ClonePlan plan, IProgress<CloneProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return Task.Factory.StartNew(
            () => Run(plan, progress, cancellationToken),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    private CloneResult Run(ClonePlan plan, IProgress<CloneProgress>? progress, CancellationToken cancellationToken)
    {
        using var log = FileLogger.Create(LogDirectory, "clonado");
        CloneResult result;
        try
        {
            // RS-04: justo antes de escribir se comprueba con un inventario nuevo que los discos siguen siendo los del plan.
            var change = PlanValidation.FindChange(plan, _inventory.GetDisks());
            if (change is not null)
            {
                CloneLog.RevalidationFailed(log, change);
                result = new CloneResult
                {
                    Outcome = CloneOutcome.Failed,
                    Message = $"{change} No se ha escrito nada. Vuelve a revisar el clonado.",
                };
            }
            else
            {
                var executor = new CloneExecutor(new Win32DiskAccess(plan.Options.VirtualTargetOnly), log);
                result = executor.Execute(plan, progress, cancellationToken);
            }
        }
        catch (DiskInventoryException ex)
        {
            CloneLog.RevalidationError(log, ex);
            result = new CloneResult { Outcome = CloneOutcome.Failed, Message = $"{ex.Message} No se ha escrito nada." };
        }

        return result with { LogPath = log.FilePath };
    }

    private static PartitionTableInfo? TryReadTable(DiskInfo source)
    {
        try
        {
            using var device = DiskDevice.OpenDisk(source.Number, writable: false);
            if (DiskIdentity.From(source).FindMismatch(device.QueryFacts()) is not null)
            {
                return null;
            }

            using var buffer = new AlignedBuffer(PartitionTable.HeadBytes);
            device.Read(0, buffer.Span);
            return PartitionTable.Read(buffer.Span, device.SectorSize);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>Comprobaciones previas a la ejecución sobre un inventario recién leído.</summary>
internal static class PlanValidation
{
    /// <summary>Describe qué cambió desde que se hizo el plan, o nulo si nada.</summary>
    public static string? FindChange(ClonePlan plan, IReadOnlyList<DiskInfo> disks)
    {
        foreach (var (identity, partitions, role) in new[]
        {
            (plan.Source, plan.SourcePartitions, "origen"),
            (plan.Target, plan.TargetPartitions, "destino"),
        })
        {
            var current = disks.FirstOrDefault(d => d.Number == identity.Number);
            if (current is null)
            {
                return $"El disco de {role} ({identity.Number}) ya no está conectado.";
            }

            if (identity.FindMismatch(current) is { } mismatch)
            {
                return $"El disco {identity.Number} ya no es el {role} que se eligió: {mismatch}.";
            }

            if (!SameLayout(partitions, current.Partitions))
            {
                return $"Las particiones del disco de {role} han cambiado desde que se revisó el clonado.";
            }
        }

        return null;
    }

    private static bool SameLayout(IReadOnlyList<PartitionInfo> planned, IReadOnlyList<PartitionInfo> current) =>
        planned.Count == current.Count
        && planned.Zip(current).All(pair =>
            pair.First.OffsetBytes == pair.Second.OffsetBytes
            && pair.First.SizeBytes == pair.Second.SizeBytes
            && string.Equals(pair.First.Volume?.Path, pair.Second.Volume?.Path, StringComparison.OrdinalIgnoreCase));
}
