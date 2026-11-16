using Cloner.Core.Devices;
using Cloner.Core.Inventory;
using Cloner.Core.Snapshots;

namespace Cloner.Core.Cloning;

/// <summary>Acceso a los discos que necesita el ejecutor; en pruebas se sustituye por discos en memoria.</summary>
internal interface IDiskAccess
{
    /// <summary>Abre el disco y comprueba que es el esperado (RS-04). El origen se abre en solo lectura (RS-01).</summary>
    /// <exception cref="CloneException">El disco no es el esperado.</exception>
    IBlockDevice Open(DiskIdentity disk, bool writable);

    /// <summary>Bloquea los volúmenes; con <paramref name="dismount"/> también los desmonta.</summary>
    /// <exception cref="CloneException">Algún volumen está en uso (RS-06).</exception>
    IDisposable LockVolumes(IReadOnlyList<VolumeRef> volumes, bool dismount);

    /// <summary>Toma a la vez una instantánea de cada volumen (RF-10).</summary>
    /// <exception cref="CloneException">Windows no pudo crearlas.</exception>
    ISnapshotSet CreateSnapshots(IReadOnlyList<string> volumePaths, int sectorSize);

    /// <summary>Abre en solo lectura un volumen montado de un disco de solo lectura.</summary>
    IVolumeSource OpenVolume(string volumePath, int sectorSize);

    /// <summary>Trabajo sobre el clon montado, después de copiar.</summary>
    ITargetFinisher Finisher { get; }
}

/// <summary>Un volumen que se puede leer: una instantánea o un volumen que no puede cambiar.</summary>
internal interface IVolumeSource : IDisposable
{
    /// <summary>Contenido del volumen, desde su primer byte.</summary>
    IBlockDevice Device { get; }

    /// <summary>
    /// Bytes que se pueden leer del volumen. Puede ser menor que la partición: lo que quede
    /// después se lee del disco.
    /// </summary>
    long Length { get; }

    /// <summary>Tramos en uso según el sistema de archivos, o nulo si no se pueden saber.</summary>
    IReadOnlyList<ByteRange>? ReadUsedRanges();
}

/// <summary>Instantáneas de varios volúmenes, que existen mientras no se libere el conjunto.</summary>
internal interface ISnapshotSet : IDisposable
{
    IVolumeSource Open(string volumePath);

    /// <summary>Raíz de la instantánea de un volumen para leer sus archivos, con «\» final.</summary>
    string RootFor(string volumePath);

    /// <summary>Indica que la copia terminó bien.</summary>
    void Complete();
}

/// <summary>Acceso real a los discos del equipo. Requiere administrador.</summary>
internal sealed class Win32DiskAccess(bool virtualTargetOnly, IDiskInventory inventory) : IDiskAccess
{
    public ITargetFinisher Finisher { get; } = new TargetFinisher(inventory);

    public IBlockDevice Open(DiskIdentity disk, bool writable)
    {
        var device = DiskDevice.OpenDisk(disk.Number, writable);
        try
        {
            var facts = device.QueryFacts();
            var mismatch = disk.FindMismatch(facts);
            if (mismatch is not null)
            {
                throw new CloneException($"El disco {disk.Number} ya no es el que se eligió: {mismatch}.");
            }

            if (writable && virtualTargetOnly && facts.Bus != BusKind.Virtual)
            {
                throw new CloneException($"El disco {disk.Number} no es un disco virtual y solo se admiten destinos virtuales.");
            }

            return device;
        }
        catch
        {
            device.Dispose();
            throw;
        }
    }

    public IDisposable LockVolumes(IReadOnlyList<VolumeRef> volumes, bool dismount) => VolumeLock.Acquire(volumes, dismount);

    public ISnapshotSet CreateSnapshots(IReadOnlyList<string> volumePaths, int sectorSize) =>
        new Win32SnapshotSet(VssSnapshotSet.Create(volumePaths), sectorSize);

    public IVolumeSource OpenVolume(string volumePath, int sectorSize) =>
        new Win32VolumeSource(DiskDevice.OpenVolume(volumePath, sectorSize));

    private sealed class Win32SnapshotSet(VssSnapshotSet snapshots, int sectorSize) : ISnapshotSet
    {
        public IVolumeSource Open(string volumePath) =>
            new Win32VolumeSource(DiskDevice.OpenVolume(snapshots.DeviceFor(volumePath), sectorSize));

        public string RootFor(string volumePath) => snapshots.DeviceFor(volumePath) + "\\";

        public void Complete() => snapshots.Complete();

        public void Dispose() => snapshots.Dispose();
    }

    private sealed class Win32VolumeSource(DiskDevice device) : IVolumeSource
    {
        private readonly Lazy<long> _length = new(device.ReadableLength);

        public IBlockDevice Device => device;

        public long Length => _length.Value;

        public IReadOnlyList<ByteRange>? ReadUsedRanges() => device.ReadNtfsUsage(Length);

        public void Dispose() => device.Dispose();
    }
}
