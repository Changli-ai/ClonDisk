using System.Windows;
using Cloner.App.ViewModels;
using Cloner.Core.Cloning;
using Cloner.Core.Inventory;

namespace Cloner.App;

/// <summary>Ventana principal: inspección de discos y particiones.</summary>
public partial class MainWindow : Window
{
    public MainWindow()
        : this(CreateViewModel())
    {
    }

    public MainWindow(MainViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        InitializeComponent();
        DataContext = viewModel;
        viewModel.ShowCloneWizard = createViewModel =>
        {
            var wizard = new CloneWindow { Owner = this };
            wizard.ViewModel = createViewModel(wizard);
            wizard.ShowDialog();
        };
        Loaded += async (_, _) => await viewModel.RefreshCommand.ExecuteAsync(null);
    }

    private static MainViewModel CreateViewModel()
    {
        var inventory = new DiskInventory();
        return new MainViewModel(inventory, new CloneService(inventory));
    }
}
