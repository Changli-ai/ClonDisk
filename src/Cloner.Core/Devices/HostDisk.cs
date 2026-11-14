using Cloner.Core.Inventory;
using Windows.Win32;

namespace Cloner.Core.Devices;

/// <summary>Averigua en qué disco está una ruta, para no escribir nunca sobre el disco desde el que corre la app.</summary>
internal static class HostDisk
{
    /// <summary>Número del disco que contiene <paramref name="path"/>, o nulo si no se puede determinar.</summary>
    public static int? FindDiskNumber(string path, IReadOnlyList<DiskInfo> disks)
    {
        var volume = TryGetVolumeName(path);
        return volume is null ? null : FindByVolume(volume, disks);
    }

    /// <summary>Busca el disco que contiene el volumen <c>\\?\Volume{GUID}\</c> indicado.</summary>
    internal static int? FindByVolume(string volumeName, IReadOnlyList<DiskInfo> disks)
    {
        var wanted = Normalize(volumeName);
        foreach (var disk in disks)
        {
            if (disk.Partitions.Any(p => p.Volume is { } v && Normalize(v.Path) == wanted))
            {
                return disk.Number;
            }
        }

        return null;
    }

    private static string Normalize(string volumePath) => volumePath.TrimEnd('\\').ToUpperInvariant();

    private static unsafe string? TryGetVolumeName(string path)
    {
        const int capacity = 1024;
        char* mountPoint = stackalloc char[capacity];
        fixed (char* file = path)
        {
            if (!PInvoke.GetVolumePathName(file, mountPoint, capacity))
            {
                return null;
            }
        }

        char* volumeName = stackalloc char[capacity];
        if (!PInvoke.GetVolumeNameForVolumeMountPoint(mountPoint, volumeName, capacity))
        {
            return null;
        }

        return new string(volumeName);
    }
}
