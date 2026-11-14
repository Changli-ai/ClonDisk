using Microsoft.Win32.SafeHandles;
using Windows.Win32;

namespace Cloner.Core.Devices;

/// <summary>
/// Bloqueo exclusivo de los volúmenes de un disco mientras dura el clonado. En el origen
/// impide que otros programas cambien los datos durante la copia; en el destino, además, se
/// desmontan los volúmenes, porque Windows no deja escribir sobre un volumen montado.
/// </summary>
internal sealed class VolumeLock : IDisposable
{
    private const int Attempts = 5;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(400);

    private readonly List<SafeFileHandle> _handles = [];

    private VolumeLock()
    {
    }

    /// <exception cref="CloneException">Algún volumen está en uso y no se pudo bloquear.</exception>
    public static VolumeLock Acquire(IReadOnlyList<VolumeRef> volumes, bool dismount)
    {
        var volumeLock = new VolumeLock();
        try
        {
            foreach (var volume in volumes)
            {
                volumeLock._handles.Add(LockOne(volume, dismount));
            }

            return volumeLock;
        }
        catch
        {
            volumeLock.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        foreach (var handle in _handles)
        {
            try
            {
                DiskDevice.Control(handle, PInvoke.FSCTL_UNLOCK_VOLUME);
            }
            catch (IOException)
            {
                // Cerrar el identificador también libera el bloqueo.
            }

            handle.Dispose();
        }

        _handles.Clear();
    }

    private static SafeFileHandle LockOne(VolumeRef volume, bool dismount)
    {
        // CreateFile abre el volumen solo sin la barra final; con ella abriría su directorio raíz.
        var path = volume.Path.TrimEnd('\\');
        const uint genericRead = 0x80000000;
        const uint genericWrite = 0x40000000;
        var access = dismount ? genericRead | genericWrite : genericRead;

        SafeFileHandle handle;
        try
        {
            handle = DiskDevice.OpenHandle(path, access, unbuffered: false, $"el volumen {volume.Name}");
        }
        catch (IOException ex)
        {
            throw new CloneException($"No se pudo abrir el volumen {volume.Name}: {ex.Message}", ex);
        }

        try
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    DiskDevice.Control(handle, PInvoke.FSCTL_LOCK_VOLUME);
                    break;
                }
                catch (IOException ex) when (attempt < Attempts)
                {
                    _ = ex;
                    Thread.Sleep(RetryDelay);
                }
                catch (IOException ex)
                {
                    throw new CloneException(
                        $"El volumen {volume.Name} está en uso. Cierra los programas y ventanas que lo estén usando e inténtalo de nuevo.",
                        ex);
                }
            }

            if (dismount)
            {
                DiskDevice.Control(handle, PInvoke.FSCTL_DISMOUNT_VOLUME);
            }

            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }
}
