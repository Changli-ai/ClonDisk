using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.Vss;

namespace Cloner.Core.Snapshots;

/// <summary>
/// Conjunto de instantáneas VSS de varios volúmenes, tomadas a la vez (RF-10). Usa el contexto
/// de copia de seguridad con escritores, así que los programas que lo admiten (registro,
/// bases de datos…) dejan sus datos coherentes. Las instantáneas no son persistentes: Windows
/// las borra al liberar el conjunto, también si el proceso termina de forma inesperada.
/// </summary>
internal sealed class VssSnapshotSet : IDisposable
{
    /// <summary>VSS_S_ASYNC_FINISHED: la operación asíncrona terminó bien.</summary>
    private const int AsyncFinished = 0x0004230A;

    private readonly Dictionary<string, string> _devices;
    private IVssBackupComponents? _backup;
    private bool _completed;

    private VssSnapshotSet(IVssBackupComponents backup, Dictionary<string, string> devices)
    {
        _backup = backup;
        _devices = devices;
    }

    /// <summary>Volúmenes incluidos y la ruta del dispositivo de su instantánea.</summary>
    public IReadOnlyDictionary<string, string> Devices => _devices;

    /// <param name="volumePaths">Rutas <c>\\?\Volume{GUID}\</c> de los volúmenes.</param>
    /// <exception cref="CloneException">Windows no pudo crear las instantáneas.</exception>
    public static VssSnapshotSet Create(IReadOnlyList<string> volumePaths)
    {
        ArgumentNullException.ThrowIfNull(volumePaths);
        if (volumePaths.Count == 0)
        {
            throw new ArgumentException("Hace falta al menos un volumen.", nameof(volumePaths));
        }

        IVssBackupComponents? backup = null;
        var stage = "iniciar VSS";
        try
        {
            PInvoke.CreateVssBackupComponentsInternal(out backup).ThrowOnFailure();
            backup.InitializeForBackup(default);
            backup.SetContext((int)VSS_SNAPSHOT_CONTEXT.VSS_CTX_BACKUP);

            // Copia de tipo COPY: no altera el historial de copias de seguridad de los programas.
            backup.SetBackupState(0, 0, VSS_BACKUP_TYPE.VSS_BT_COPY, 0);

            stage = "consultar los escritores de VSS";
            backup.GatherWriterMetadata(out var gather);
            Wait(gather);

            stage = "preparar las instantáneas";
            var snapshots = StartSet(backup, volumePaths);

            backup.PrepareForBackup(out var prepare);
            Wait(prepare);

            stage = "crear las instantáneas";
            backup.DoSnapshotSet(out var create);
            Wait(create);

            var devices = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (volume, id) in snapshots)
            {
                devices[volume] = DeviceOf(backup, id);
            }

            return new VssSnapshotSet(backup, devices);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            if (backup is not null)
            {
                TryAbort(backup);
                Marshal.FinalReleaseComObject(backup);
            }

            throw new CloneException($"Windows no pudo {stage} (VSS): {ex.Message}", ex);
        }
    }

    /// <summary>Ruta del dispositivo de la instantánea de un volumen.</summary>
    public string DeviceFor(string volumePath) =>
        _devices.TryGetValue(Normalize(volumePath), out var device)
            ? device
            : throw new KeyNotFoundException($"No hay instantánea de {volumePath}.");

    /// <summary>Indica a los escritores que la copia terminó bien. Sin llamarlo, se toma como abortada.</summary>
    public void Complete()
    {
        if (_backup is null || _completed)
        {
            return;
        }

        try
        {
            _backup.BackupComplete(out var complete);
            Wait(complete);
            _completed = true;
        }
        catch (COMException)
        {
            // Las instantáneas se liberan igualmente en Dispose.
        }
    }

    /// <summary>
    /// Cuándo se liberó la última instantánea en este proceso. Quitar un volumen poco después de
    /// liberar una instantánea suya puede bloquear el equipo (carrera de volsnap).
    /// </summary>
    public static DateTime? LastReleasedUtc { get; private set; }

    public void Dispose()
    {
        if (_backup is null)
        {
            return;
        }

        if (!_completed)
        {
            TryAbort(_backup);
        }

        Marshal.FinalReleaseComObject(_backup);
        _backup = null;
        LastReleasedUtc = DateTime.UtcNow;
    }

    private static unsafe List<(string Volume, Guid Id)> StartSet(IVssBackupComponents backup, IReadOnlyList<string> volumePaths)
    {
        Guid setId;
        backup.StartSnapshotSet(&setId);

        var snapshots = new List<(string, Guid)>();
        foreach (var path in volumePaths)
        {
            // VSS exige la ruta del volumen con la barra final.
            var volume = Normalize(path);
            var name = (volume + "\0").ToCharArray();
            Guid snapshotId;
            backup.AddToSnapshotSet(in MemoryMarshal.GetReference(MemoryMarshal.Cast<char, ushort>(name.AsSpan())), Guid.Empty, &snapshotId);
            snapshots.Add((volume, snapshotId));
        }

        return snapshots;
    }

    private static unsafe string DeviceOf(IVssBackupComponents backup, Guid snapshotId)
    {
        VSS_SNAPSHOT_PROP properties;
        backup.GetSnapshotProperties(snapshotId, &properties);
        try
        {
            return new string((char*)properties.m_pwszSnapshotDeviceObject);
        }
        finally
        {
            PInvoke.VssFreeSnapshotPropertiesInternal(&properties);
        }
    }

    private static unsafe void Wait(IVssAsync operation)
    {
        try
        {
            operation.Wait(uint.MaxValue);
            HRESULT result;
            var reserved = 0;
            operation.QueryStatus(&result, ref reserved);
            if (result.Value != AsyncFinished)
            {
                Marshal.ThrowExceptionForHR(result.Value);
                throw new InvalidOperationException($"VSS terminó con el estado 0x{result.Value:X8}.");
            }
        }
        finally
        {
            Marshal.FinalReleaseComObject(operation);
        }
    }

    private static void TryAbort(IVssBackupComponents backup)
    {
        try
        {
            backup.AbortBackup();
        }
        catch (COMException)
        {
            // Si no se llegó a preparar la copia no hay nada que abortar.
        }
    }

    private static string Normalize(string volumePath) => volumePath.TrimEnd('\\') + "\\";
}
