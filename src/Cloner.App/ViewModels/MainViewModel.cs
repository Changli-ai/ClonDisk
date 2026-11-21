using System.Collections.ObjectModel;
using Cloner.Core.Cloning;
using Cloner.Core.Inventory;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cloner.App.ViewModels;

/// <summary>Ventana principal: barra lateral con los modos, paneles de copia y vista de los discos.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly IDiskInventory _inventory;
    private readonly ICloneService _cloneService;
    private readonly IBootRepairService? _bootRepair;
    private readonly IDiskPreparationService? _preparation;
    private IReadOnlyList<DiskInfo> _disks = [];
    private int? _hostDisk;

    public MainViewModel(IDiskInventory inventory, ICloneService cloneService, IBootRepairService? bootRepair = null, IDiskPreparationService? preparation = null)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(cloneService);
        _inventory = inventory;
        _cloneService = cloneService;
        _bootRepair = bootRepair;
        _preparation = preparation;
        SelectedNav = NavItem.All[0];
        DiskCopy = new CopyPanelViewModel(
            "Clonar un disco",
            "Elige el disco de origen y el de destino: ClonDisk copia todo su contenido, particiones incluidas.",
            systemMode: false,
            () => _disks,
            (title, choices) => PickDisk?.Invoke(title, choices),
            StartWizardAsync);
        SystemCopy = new CopyPanelViewModel(
            "Migrar el sistema",
            "Pasa Windows, tus programas y tus archivos a otro disco, con Windows en marcha. Solo tienes que elegir el destino.",
            systemMode: true,
            () => _disks,
            (title, choices) => PickDisk?.Invoke(title, choices),
            StartWizardAsync);
    }

    public IReadOnlyList<NavItem> NavItems { get; } = NavItem.All;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Section))]
    public partial NavItem SelectedNav { get; set; }

    public AppSection Section => SelectedNav.Section;

    public CopyPanelViewModel DiskCopy { get; }
    public CopyPanelViewModel SystemCopy { get; }

    /// <summary>La ventana lo asigna: muestra un selector de discos y devuelve el elegido, o nulo.</summary>
    public Func<string, IReadOnlyList<DiskChoice>, DiskChoice?>? PickDisk { get; set; }

    /// <summary>
    /// La ventana lo asigna para abrir el asistente de clonado de forma modal. Recibe una
    /// función que crea el ViewModel a partir de los diálogos de la propia ventana del asistente.
    /// </summary>
    public Action<Func<ICloneDialogs, CloneViewModel>>? ShowCloneWizard { get; set; }

    /// <summary>La ventana lo asigna: pregunta (sí o no) antes de reparar el arranque de un disco.</summary>
    public Func<string, bool>? ConfirmRepair { get; set; }

    /// <summary>La ventana lo asigna: muestra el resultado de la reparación (texto y si fue bien).</summary>
    public Action<string, bool>? ShowRepairResult { get; set; }

    /// <summary>Lo que está haciendo la reparación del arranque en curso, o nulo.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RepairBootCommand), nameof(PrepareDiskCommand))]
    public partial string? RepairStatus { get; set; }

    /// <summary>La ventana lo asigna: pregunta (sí o no) antes de limpiar y formatear un disco.</summary>
    public Func<string, bool>? ConfirmPrepare { get; set; }

    /// <summary>La ventana lo asigna: muestra el resultado de la limpieza (texto y si fue bien).</summary>
    public Action<string, bool>? ShowPrepareResult { get; set; }

    /// <summary>Lo que está haciendo la limpieza del disco en curso, o nulo.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RepairBootCommand), nameof(PrepareDiskCommand))]
    public partial string? PrepareStatus { get; set; }

    private bool IsBusy => RepairStatus is not null || PrepareStatus is not null;

    private bool CanRepairBoot => _bootRepair is not null && !IsBusy && SelectedDisk is { CanRepairBoot: true };

    private bool CanPrepareDisk => _preparation is not null && !IsBusy && SelectedDisk is { CanPrepare: true };

    /// <summary>
    /// Deja el disco seleccionado como nuevo: borra todas sus particiones (también las de arranque
    /// que un formateo deja), crea una sola NTFS y le da letra. Como «clean» de diskpart.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanPrepareDisk))]
    private async Task PrepareDiskAsync()
    {
        var selected = SelectedDisk!;
        var disk = _disks.FirstOrDefault(d => d.Number == selected.Number);
        if (disk is null)
        {
            return;
        }

        var erased = disk.Partitions.Count == 0
            ? "• El disco no tiene particiones.\n"
            : string.Concat(selected.Partitions.Select(p => $"• Partición {p.Number}: {p.Kind} · {p.Volume} · {p.FileSystem} · {p.Size}\n"));
        var question =
            $"Se borrará TODO el contenido del disco {disk.Number} ({disk.Model}):\n\n"
            + erased
            + "\nClonDisk borrará todas sus particiones, también las de arranque (EFI) y recuperación que deja un formateo, "
            + $"y creará una sola partición NTFS «{DiskPreparationService.VolumeLabel}» con letra de unidad.\n\n"
            + "No se puede deshacer. ¿Limpiar y formatear el disco?";
        if (ConfirmPrepare?.Invoke(question) != true)
        {
            return;
        }

        PrepareStatus = "Empezando la limpieza";
        try
        {
            var progress = new Progress<string>(text => PrepareStatus = text + "…");
            var result = await _preparation!.PrepareAsync(DiskIdentity.From(disk), progress);
            var text = result.LogPath is null ? result.Message : $"{result.Message}\n\nRegistro: {result.LogPath}";
            ShowPrepareResult?.Invoke(text, result.Succeeded);
        }
        finally
        {
            PrepareStatus = null;
        }

        await RefreshAsync();
    }

    /// <summary>
    /// Rehace el arranque del disco seleccionado: para un clon que se puso en línea junto al
    /// original y ya no arranca (0xc000000e).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRepairBoot))]
    private async Task RepairBootAsync()
    {
        var disk = SelectedDisk!;
        var question =
            $"ClonDisk va a rehacer el arranque del disco {disk.Number} ({disk.Model}):\n\n"
            + "• lo pondrá en línea si está sin conexión,\n"
            + "• buscará su Windows y su partición de arranque,\n"
            + "• corregirá la letra C: en su registro y\n"
            + "• volverá a crear sus archivos de arranque con bcdboot.\n\n"
            + "No borra datos. Úsalo con el clon conectado como segundo disco, arrancando desde el original.\n\n"
            + "¿Reparar el arranque?";
        if (ConfirmRepair?.Invoke(question) != true)
        {
            return;
        }

        RepairStatus = "Empezando la reparación";
        try
        {
            var progress = new Progress<string>(text => RepairStatus = text + "…");
            var result = await _bootRepair!.RepairAsync(disk.Number, progress);
            var text = result.LogPath is null ? result.Message : $"{result.Message}\n\nRegistro: {result.LogPath}";
            ShowRepairResult?.Invoke(text, result.Succeeded);
        }
        finally
        {
            RepairStatus = null;
        }

        await RefreshAsync();
    }

    public ObservableCollection<DiskViewModel> Disks { get; } = [];

    /// <summary>Ya se intentó leer los discos al menos una vez (con o sin éxito).</summary>
    public bool HasLoaded { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RepairBootCommand), nameof(PrepareDiskCommand))]
    public partial DiskViewModel? SelectedDisk { get; set; }

    /// <summary>Mensaje de carga, de error o de lista vacía. Nulo cuando hay discos que mostrar.</summary>
    [ObservableProperty]
    public partial string? StatusMessage { get; set; }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        var selectedNumber = SelectedDisk?.Number;
        if (Disks.Count == 0)
        {
            StatusMessage = "Leyendo los discos del equipo…";
        }

        try
        {
            var disks = await Task.Run(_inventory.GetDisks);

            _disks = disks;
            _hostDisk = DiskProtection.HostDiskNumber(disks);
            Disks.Clear();
            foreach (var disk in disks)
            {
                Disks.Add(new DiskViewModel(disk, _hostDisk));
            }

            SelectedDisk = Disks.FirstOrDefault(d => d.Number == selectedNumber) ?? Disks.FirstOrDefault();
            StatusMessage = Disks.Count == 0 ? "No se encontró ningún disco." : null;
            CloneCommand.NotifyCanExecuteChanged();
            DiskCopy.Refresh();
            SystemCopy.Refresh();
        }
        catch (DiskInventoryException ex)
        {
            _disks = [];
            Disks.Clear();
            SelectedDisk = null;
            StatusMessage = ex.Message;
        }
        finally
        {
            HasLoaded = true;
        }
    }

    private async Task StartWizardAsync(int source, int target)
    {
        var disks = _disks;
        ShowCloneWizard?.Invoke(dialogs => new CloneViewModel(_cloneService, disks, source, dialogs, target, _hostDisk));
        await RefreshAsync();
    }

    private bool CanClone => _disks.Count >= 2;

    /// <summary>Abre el asistente de clonado y, al cerrarlo, vuelve a leer los discos.</summary>
    [RelayCommand(CanExecute = nameof(CanClone))]
    private async Task CloneAsync()
    {
        var disks = _disks;
        var source = SelectedDisk?.Number;
        ShowCloneWizard?.Invoke(dialogs => new CloneViewModel(_cloneService, disks, source, dialogs, hostDiskNumber: _hostDisk));
        await RefreshAsync();
    }
}
