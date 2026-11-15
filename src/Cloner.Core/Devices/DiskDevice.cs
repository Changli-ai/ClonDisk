using System.ComponentModel;
using System.Runtime.InteropServices;
using Cloner.Core.Inventory;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.FileSystem;

namespace Cloner.Core.Devices;

/// <summary>
/// Un disco (<c>\\.\PhysicalDriveN</c>) o un archivo abierto en crudo, sin caché del sistema
/// y con escritura directa. Se abre en solo lectura salvo que se pida escritura.
/// </summary>
internal sealed class DiskDevice : IBlockDevice
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorAccessDenied = 5;

    private readonly SafeFileHandle _handle;
    private readonly string _name;
    private readonly bool _isDisk;

    private DiskDevice(SafeFileHandle handle, string name, bool isDisk, long length, int sectorSize, bool canWrite)
    {
        _handle = handle;
        _name = name;
        _isDisk = isDisk;
        Length = length;
        SectorSize = sectorSize;
        CanWrite = canWrite;
    }

    public long Length { get; }
    public int SectorSize { get; }
    public bool CanWrite { get; }

    public static string PhysicalDrivePath(int number) => $@"\\.\PhysicalDrive{number}";

    /// <summary>Abre el disco <paramref name="number"/>. Requiere administrador.</summary>
    public static DiskDevice OpenDisk(int number, bool writable)
    {
        var name = $"el disco {number}";
        var handle = OpenHandle(PhysicalDrivePath(number), writable ? GenericRead | GenericWrite : GenericRead, unbuffered: true, name);
        try
        {
            var (sectorSize, size) = QueryGeometry(handle);
            return new DiskDevice(handle, name, isDisk: true, size, sectorSize, writable);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>Abre un archivo como si fuera un disco. Solo para pruebas.</summary>
    public static DiskDevice OpenFile(string path, int sectorSize, bool writable)
    {
        var handle = OpenHandle(path, writable ? GenericRead | GenericWrite : GenericRead, unbuffered: true, path);
        try
        {
            return new DiskDevice(handle, path, isDisk: false, RandomAccess.GetLength(handle), sectorSize, writable);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Abre en solo lectura un volumen o el dispositivo de una instantánea
    /// (<c>\\?\Volume{GUID}</c>, <c>\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopyN</c>).
    /// </summary>
    public static DiskDevice OpenVolume(string devicePath, int sectorSize)
    {
        ArgumentNullException.ThrowIfNull(devicePath);
        var name = $"el volumen {devicePath}";
        var handle = OpenHandle(devicePath.TrimEnd('\\'), GenericRead, unbuffered: true, name);
        try
        {
            Span<byte> length = stackalloc byte[8];
            Ioctl(handle, PInvoke.IOCTL_DISK_GET_LENGTH_INFO, [], length);
            return new DiskDevice(handle, name, isDisk: false, BitConverter.ToInt64(length), sectorSize, canWrite: false);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Bytes que se pueden leer realmente. El dispositivo de una instantánea declara la longitud de
    /// la partición, pero no deja leer el final que queda fuera de los clústeres de NTFS; se
    /// averigua con una búsqueda binaria de lecturas de un sector.
    /// </summary>
    public long ReadableLength()
    {
        var sectors = Length / SectorSize;
        using var buffer = new AlignedBuffer(Math.Max(SectorSize, AlignedBuffer.Alignment));
        var sector = buffer.Span[..SectorSize];
        if (sectors == 0 || CanRead(sectors - 1, sector))
        {
            return sectors * SectorSize;
        }

        // Invariante: el sector «readable» se lee y el «unreadable» no.
        long readable = 0;
        var unreadable = sectors - 1;
        while (unreadable - readable > 1)
        {
            var middle = readable + ((unreadable - readable) / 2);
            if (CanRead(middle, sector))
            {
                readable = middle;
            }
            else
            {
                unreadable = middle;
            }
        }

        return (readable + 1) * SectorSize;
    }

    private bool CanRead(long sector, Span<byte> buffer)
    {
        try
        {
            return RandomAccess.Read(_handle, buffer, sector * SectorSize) == buffer.Length;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Tramos en uso de un volumen NTFS según su mapa de bits, o nulo si el volumen no es NTFS.
    /// Solo tiene sentido en un volumen abierto con <see cref="OpenVolume"/>.
    /// </summary>
    /// <param name="readableLength">Final legible del volumen (<see cref="ReadableLength"/>).</param>
    public List<ByteRange>? ReadNtfsUsage(long readableLength)
    {
        Span<byte> data = stackalloc byte[128];
        try
        {
            Ioctl(_handle, PInvoke.FSCTL_GET_NTFS_VOLUME_DATA, [], data);
        }
        catch (IOException)
        {
            return null;
        }

        var (totalClusters, bytesPerCluster) = NtfsUsage.ParseVolumeData(data);
        var bitmap = new byte[NtfsUsage.BitmapHeaderSize + ((totalClusters + 7) / 8)];
        Span<byte> startingLcn = stackalloc byte[8];
        startingLcn.Clear();
        Ioctl(_handle, PInvoke.FSCTL_GET_VOLUME_BITMAP, startingLcn, bitmap);
        return NtfsUsage.UsedRanges(bitmap, totalClusters, bytesPerCluster, readableLength);
    }

    /// <summary>
    /// Consulta los datos del disco sin pedir acceso de lectura ni de escritura, así que no
    /// requiere administrador.
    /// </summary>
    public static DeviceFacts QueryFacts(int number)
    {
        using var handle = OpenHandle(PhysicalDrivePath(number), 0, unbuffered: false, $"el disco {number}");
        return QueryFacts(handle);
    }

    public DeviceFacts QueryFacts() => QueryFacts(_handle);

    public void Read(long offset, Span<byte> buffer)
    {
        var done = 0;
        while (done < buffer.Length)
        {
            var read = RandomAccess.Read(_handle, buffer[done..], offset + done);
            if (read == 0)
            {
                throw new IOException($"Lectura más allá del final de {_name} (byte {offset + done:N0}).");
            }

            done += read;
        }
    }

    public void Write(long offset, ReadOnlySpan<byte> data)
    {
        if (!CanWrite)
        {
            throw new InvalidOperationException($"{_name} se abrió en solo lectura.");
        }

        RandomAccess.Write(_handle, data, offset);
    }

    public void Flush()
    {
        if (CanWrite && !PInvoke.FlushFileBuffers(_handle))
        {
            throw new IOException($"No se pudieron vaciar las cachés de {_name}.", new Win32Exception(Marshal.GetLastPInvokeError()));
        }
    }

    public void RefreshLayout()
    {
        if (_isDisk)
        {
            Ioctl(_handle, PInvoke.IOCTL_DISK_UPDATE_PROPERTIES, [], []);
        }
    }

    public void Dispose() => _handle.Dispose();

    /// <summary>Envía un IOCTL o FSCTL sin estructuras de entrada ni de salida.</summary>
    internal static void Control(SafeFileHandle handle, uint code) => Ioctl(handle, code, [], []);

    /// <summary>Envía un IOCTL o FSCTL con una estructura de entrada y sin salida.</summary>
    internal static void Control(SafeFileHandle handle, uint code, ReadOnlySpan<byte> input) => Ioctl(handle, code, input, []);

    internal static SafeFileHandle OpenHandle(string path, uint access, bool unbuffered, string name)
    {
        var flags = unbuffered
            ? FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_NO_BUFFERING | FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_WRITE_THROUGH
            : FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_NORMAL;

        var handle = PInvoke.CreateFile(
            path,
            access,
            FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE,
            null,
            FILE_CREATION_DISPOSITION.OPEN_EXISTING,
            flags,
            null);

        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw error switch
            {
                ErrorAccessDenied => new UnauthorizedAccessException(
                    $"Windows no permitió abrir {name}. Hace falta ejecutar como administrador.", new Win32Exception(error)),
                ErrorFileNotFound or ErrorPathNotFound => new IOException($"No existe {name}.", new Win32Exception(error)),
                _ => new IOException($"No se pudo abrir {name}: {new Win32Exception(error).Message}", new Win32Exception(error)),
            };
        }

        return handle;
    }

    private static DeviceFacts QueryFacts(SafeFileHandle handle)
    {
        Span<byte> number = stackalloc byte[DeviceDescriptors.DeviceNumberSize];
        Ioctl(handle, PInvoke.IOCTL_STORAGE_GET_DEVICE_NUMBER, [], number);

        var (sectorSize, size) = QueryGeometry(handle);

        Span<byte> query = stackalloc byte[DeviceDescriptors.PropertyQuerySize];
        query.Clear();
        Span<byte> descriptor = stackalloc byte[1024];
        Ioctl(handle, PInvoke.IOCTL_STORAGE_QUERY_PROPERTY, query, descriptor);
        var (productId, serial, busType) = DeviceDescriptors.ParseDeviceDescriptor(descriptor);

        return new DeviceFacts
        {
            DeviceNumber = DeviceDescriptors.ParseDeviceNumber(number),
            SizeBytes = size,
            SectorSize = sectorSize,
            ProductId = productId,
            SerialNumber = serial,
            Bus = DiskInventoryBuilder.MapBus(checked((ushort)busType)),
        };
    }

    private static (int SectorSize, long DiskSize) QueryGeometry(SafeFileHandle handle)
    {
        // DISK_GEOMETRY_EX termina en datos variables; 256 bytes bastan para la parte fija.
        Span<byte> geometry = stackalloc byte[256];
        Ioctl(handle, PInvoke.IOCTL_DISK_GET_DRIVE_GEOMETRY_EX, [], geometry);
        return DeviceDescriptors.ParseGeometry(geometry);
    }

    private static unsafe int Ioctl(SafeFileHandle handle, uint code, ReadOnlySpan<byte> input, Span<byte> output)
    {
        uint returned;
        var added = false;
        try
        {
            handle.DangerousAddRef(ref added);
            fixed (byte* inPointer = input)
            fixed (byte* outPointer = output)
            {
                var ok = PInvoke.DeviceIoControl(
                    (HANDLE)handle.DangerousGetHandle(),
                    code,
                    inPointer,
                    (uint)input.Length,
                    outPointer,
                    (uint)output.Length,
                    &returned,
                    null);
                if (!ok)
                {
                    var error = new Win32Exception(Marshal.GetLastPInvokeError());
                    throw new IOException($"El dispositivo rechazó la operación 0x{code:X8}: {error.Message}", error);
                }
            }
        }
        finally
        {
            if (added)
            {
                handle.DangerousRelease();
            }
        }

        return (int)returned;
    }
}
