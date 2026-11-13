using System.Collections.ObjectModel;
using Cloner.Core.Inventory;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cloner.App.ViewModels;

/// <summary>Pantalla de inspección: lista de discos y detalle del seleccionado.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly IDiskInventory _inventory;

    public MainViewModel(IDiskInventory inventory)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        _inventory = inventory;
    }

    public ObservableCollection<DiskViewModel> Disks { get; } = [];

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

            Disks.Clear();
            foreach (var disk in disks)
            {
                Disks.Add(new DiskViewModel(disk));
            }

            SelectedDisk = Disks.FirstOrDefault(d => d.Number == selectedNumber) ?? Disks.FirstOrDefault();
            StatusMessage = Disks.Count == 0 ? "No se encontró ningún disco." : null;
        }
        catch (DiskInventoryException ex)
        {
            Disks.Clear();
            SelectedDisk = null;
            StatusMessage = ex.Message;
        }
    }
}
