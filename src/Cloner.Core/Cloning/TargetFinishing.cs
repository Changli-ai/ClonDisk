namespace Cloner.Core.Cloning;

/// <summary>Datos para rehacer el arranque de un clon de Windows con otros identificadores o posiciones.</summary>
/// <param name="WindowsVolume">Volumen del clon con la carpeta Windows.</param>
/// <param name="SystemVolume">Volumen del clon con el cargador: la partición EFI (UEFI) o la activa (BIOS).</param>
/// <param name="Uefi">Arranque UEFI (GPT) o BIOS (MBR).</param>
/// <param name="MountedDevicesMap">Valores de MountedDevices que hay que traducir en el registro del clon.</param>
internal sealed record BootRepair(string WindowsVolume, string SystemVolume, bool Uefi, IReadOnlyList<(byte[] Old, byte[] New)> MountedDevicesMap);

/// <summary>
/// Trabajo sobre el clon ya montado por Windows, después de copiar: ampliar sistemas de archivos
/// (RF-13) y rehacer el arranque (RF-18). En pruebas se sustituye por un doble.
/// </summary>
internal interface ITargetFinisher
{
    /// <summary>
    /// Espera a que Windows monte los volúmenes del disco y devuelve su ruta
    /// (<c>\\?\Volume{GUID}\</c>) por desplazamiento de partición.
    /// </summary>
    /// <exception cref="CloneException">No aparecieron a tiempo.</exception>
    IReadOnlyDictionary<long, string> WaitForVolumes(int diskNumber, IReadOnlyCollection<long> partitionOffsets);

    /// <summary>Amplía el sistema de archivos del volumen para que ocupe toda su partición.</summary>
    void ExtendVolume(string volumePath, long partitionBytes, int sectorSize);

    /// <summary>Volumen que contiene una instalación de Windows, o nulo.</summary>
    string? FindWindowsVolume(IEnumerable<string> volumePaths);

    /// <summary>Rehace el BCD con bcdboot y traduce las letras del registro del clon.</summary>
    void RepairBoot(BootRepair repair);

    /// <summary>
    /// Espera antes de que desaparezcan los volúmenes del clon: quitarlos justo después de
    /// montarlos puede bloquear el equipo por una carrera de volsnap (incidente del 14/11/2026).
    /// </summary>
    void WaitBeforeRemovingVolumes();
}
