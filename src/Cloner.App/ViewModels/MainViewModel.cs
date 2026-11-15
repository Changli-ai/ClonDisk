using System.Collections.ObjectModel;
using Cloner.Core.Cloning;
using Cloner.Core.Inventory;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cloner.App.ViewModels;

/// <summary>Pantalla de inspección: lista de discos y detalle del seleccionado.</summary>
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
    }

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
