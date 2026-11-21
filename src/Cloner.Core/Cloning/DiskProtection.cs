using Cloner.Core.Devices;
using Cloner.Core.Inventory;

namespace Cloner.Core.Cloning;

/// <summary>
/// Discos que no se pueden borrar ni usar como destino porque este equipo los necesita para
/// funcionar: el del Windows en uso, el de su arranque (que puede estar en otro disco) y el disco
/// desde el que se ejecuta la aplicación. La interfaz los muestra con un candado.
/// </summary>
public static class DiskProtection
{
    /// <summary>Número del disco desde el que se ejecuta la aplicación, o nulo si no se sabe.</summary>
    public static int? HostDiskNumber(IReadOnlyList<DiskInfo> disks) =>
        HostDisk.FindDiskNumber(AppContext.BaseDirectory, disks);

    /// <summary>Etiqueta corta del candado («Windows en uso»…), o nulo si el disco no está protegido.</summary>
    public static string? LockLabel(DiskInfo disk, int? hostDiskNumber)
    {
        ArgumentNullException.ThrowIfNull(disk);
        if (disk.ContainsWindows)
        {
            return "Windows en uso";
        }

        if (disk.ContainsBootLoader)
        {
            return "Arranque del equipo";
        }

        return hostDiskNumber == disk.Number ? "Disco de ClonDisk" : null;
    }

    /// <summary>Por qué no se puede escribir en el disco, o nulo si se puede.</summary>
    public static string? WhyLocked(DiskInfo disk, int? hostDiskNumber)
    {
        ArgumentNullException.ThrowIfNull(disk);
        if (disk.ContainsWindows)
        {
            return "Tiene instalado el Windows que se está usando ahora.";
        }

        if (disk.ContainsBootLoader)
        {
            return "Tiene la partición de arranque (EFI) con la que arranca el Windows en uso: si se borrara, este equipo dejaría de arrancar.";
        }

        return hostDiskNumber == disk.Number ? "La aplicación se está ejecutando desde este disco." : null;
    }
}
