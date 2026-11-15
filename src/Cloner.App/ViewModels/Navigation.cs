namespace Cloner.App.ViewModels;

/// <summary>Secciones de la barra lateral.</summary>
public enum AppSection
{
    DiskCopy,
    SystemCopy,
    PartitionCopy,
    Windows11,
    EmergencyDisk,
    NetworkClone,
    Disks,
}

/// <summary>Una entrada de la barra lateral.</summary>
/// <param name="Glyph">Carácter de la fuente de iconos de Windows (Segoe MDL2 Assets).</param>
/// <param name="ComingInStage">Etapa en la que llega la función, o nulo si ya está disponible.</param>
/// <param name="StartsGroup">Dibuja un separador antes de la entrada.</param>
public sealed record NavItem(AppSection Section, string Title, string Glyph, int? ComingInStage = null, bool StartsGroup = false)
{
    public bool IsAvailable => ComingInStage is null;

    public string Badge => IsAvailable ? string.Empty : "Pronto";

    public static IReadOnlyList<NavItem> All { get; } =
    [
        new(AppSection.DiskCopy, "Disco", ""),
        new(AppSection.SystemCopy, "Sistema", ""),
        new(AppSection.PartitionCopy, "Partición", "", ComingInStage: 6),
        new(AppSection.Windows11, "Migrar a Windows 11", "", ComingInStage: 11, StartsGroup: true),
        new(AppSection.EmergencyDisk, "Disco de emergencia", "", ComingInStage: 8),
        new(AppSection.NetworkClone, "Clonación por red", "", ComingInStage: 12),
        new(AppSection.Disks, "Discos del equipo", "", StartsGroup: true),
    ];
}
