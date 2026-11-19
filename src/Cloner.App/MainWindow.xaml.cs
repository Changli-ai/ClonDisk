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
        SidebarLogo.Source = AppIcon.Largest();
        SidebarVersion.Text = AppIcon.Version;
        viewModel.PickDisk = (title, choices) => DiskPickerWindow.Pick(this, title, choices);
        viewModel.ConfirmRepair = question => MessageBox.Show(
            this, question, "Reparar el arranque · ClonDisk", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
        viewModel.ShowRepairResult = (text, ok) => MessageBox.Show(
            this, text, "Reparar el arranque · ClonDisk", MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Error);
        viewModel.ShowCloneWizard = createViewModel =>
        {
            var wizard = new CloneWindow { Owner = this };
            wizard.ViewModel = createViewModel(wizard);
            wizard.ShowDialog();
        };
        // Si la pantalla de carga ya leyó los discos, no se vuelven a leer.
        Loaded += async (_, _) =>
        {
            if (!viewModel.HasLoaded)
            {
                await viewModel.RefreshCommand.ExecuteAsync(null);
            }
        };
    }

    internal static MainViewModel CreateViewModel()
    {
        var inventory = new DiskInventory();
        return new MainViewModel(inventory, new CloneService(inventory), new BootRepairService(inventory));
    }
}
