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
        SourceInitialized += (_, _) => UpdateChrome();
        viewModel.PickDisk = (title, choices) => DiskPickerWindow.Pick(this, title, choices);
        viewModel.ConfirmRepair = question =>
            MessageDialog.Confirm(this, "Reparar el arranque", question, "Reparar el arranque");
        viewModel.ShowRepairResult = (text, ok) =>
            MessageDialog.Show(this, "Reparar el arranque", text, ok ? MessageKind.Success : MessageKind.Error);
        viewModel.ConfirmPrepare = question =>
            MessageDialog.Confirm(this, "Limpiar y formatear", question, "Borrar y formatear", kind: MessageKind.Warning, dangerous: true);
        viewModel.ShowPrepareResult = (text, ok) =>
            MessageDialog.Show(this, "Limpiar y formatear", text, ok ? MessageKind.Success : MessageKind.Error);
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

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        UpdateChrome();
    }

    /// <summary>
    /// Maximizada, Windows deja fuera de la pantalla el borde de cambiar el tamaño: se compensa para que
    /// no se corte el contenido. Además, el botón alterna entre maximizar y restaurar.
    /// </summary>
    private void UpdateChrome()
    {
        var maximized = WindowState == WindowState.Maximized;
        RootGrid.Margin = maximized ? SystemParameters.WindowResizeBorderThickness : new Thickness(0);
        MaximizeButton.Content = maximized ? "\uE923" : "\uE922";
        MaximizeButton.ToolTip = maximized ? "Restaurar" : "Maximizar";
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void OnMaximizeClick(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            SystemCommands.RestoreWindow(this);
        }
        else
        {
            SystemCommands.MaximizeWindow(this);
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);

    internal static MainViewModel CreateViewModel()
    {
        var inventory = new DiskInventory();
        return new MainViewModel(inventory, new CloneService(inventory), new BootRepairService(inventory), new DiskPreparationService(inventory));
    }
}
