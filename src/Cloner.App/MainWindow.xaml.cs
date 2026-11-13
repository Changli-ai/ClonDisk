using System.Windows;
using Cloner.App.ViewModels;
using Cloner.Core.Inventory;

namespace Cloner.App;

/// <summary>Ventana principal: inspección de discos y particiones.</summary>
public partial class MainWindow : Window
{
    public MainWindow()
        : this(new MainViewModel(new DiskInventory()))
    {
    }

    public MainWindow(MainViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        InitializeComponent();
        DataContext = viewModel;
        Loaded += async (_, _) => await viewModel.RefreshCommand.ExecuteAsync(null);
    }
}
