using Cloner.Core.Devices;
using Cloner.Core.Inventory;

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
}

/// <summary>Acceso real a los discos del equipo. Requiere administrador.</summary>
internal sealed class Win32DiskAccess(bool virtualTargetOnly) : IDiskAccess
{
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
}
