using System.Diagnostics;
using System.Windows;

namespace Cloner.App;

/// <summary>Arranque: pantalla de carga mientras se leen los discos y después la ventana principal.</summary>
public partial class App : Application
{
    /// <summary>Tiempo mínimo de la pantalla de carga, para que no aparezca y desaparezca de golpe.</summary>
    private static readonly TimeSpan MinimumSplashTime = TimeSpan.FromSeconds(1.5);

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Mientras solo existe la pantalla de carga, cerrarla no debe terminar la aplicación.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var splash = new SplashWindow();
        splash.Show();
        var shown = Stopwatch.StartNew();

        splash.Status = "Leyendo los discos del equipo…";
        var viewModel = Cloner.App.MainWindow.CreateViewModel();
        await viewModel.RefreshCommand.ExecuteAsync(null);

        splash.Status = "Preparando la ventana…";
        var remaining = MinimumSplashTime - shown.Elapsed;
        if (remaining > TimeSpan.Zero)
        {
            await Task.Delay(remaining);
        }

        var window = new MainWindow(viewModel);
        MainWindow = window;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        window.Show();
        window.Activate();
        splash.Close();
    }
}
