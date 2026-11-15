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
    private IReadOnlyList<DiskInfo> _disks = [];

    public MainViewModel(IDiskInventory inventory, ICloneService cloneService)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(cloneService);
        _inventory = inventory;
        _cloneService = cloneService;
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

    public ObservableCollection<DiskViewModel> Disks { get; } = [];

    /// <summary>Ya se intentó leer los discos al menos una vez (con o sin éxito).</summary>
    public bool HasLoaded { get; private set; }

    [ObservableProperty]
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
            Disks.Clear();
            foreach (var disk in disks)
            {
                Disks.Add(new DiskViewModel(disk));
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
        ShowCloneWizard?.Invoke(dialogs => new CloneViewModel(_cloneService, disks, source, dialogs, target));
        await RefreshAsync();
    }

    private bool CanClone => _disks.Count >= 2;

    /// <summary>Abre el asistente de clonado y, al cerrarlo, vuelve a leer los discos.</summary>
    [RelayCommand(CanExecute = nameof(CanClone))]
    private async Task CloneAsync()
    {
        var disks = _disks;
        var source = SelectedDisk?.Number;
        ShowCloneWizard?.Invoke(dialogs => new CloneViewModel(_cloneService, disks, source, dialogs));
        await RefreshAsync();
    }
}
