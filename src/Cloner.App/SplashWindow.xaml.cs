using System.Reflection;
using System.Windows;
using System.Windows.Media.Imaging;

namespace Cloner.App;

/// <summary>Pantalla de carga que se muestra mientras se leen los discos al arrancar.</summary>
public partial class SplashWindow : Window
{
    public SplashWindow()
    {
        InitializeComponent();
        Logo.Source = LargestIconFrame();
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
        VersionText.Text = $"Versión {version}";
    }

    /// <summary>Texto de lo que se está haciendo.</summary>
    public string Status
    {
        get => StatusText.Text;
        set => StatusText.Text = value;
    }

    /// <summary>El icono tiene varios tamaños; para el logotipo se usa el mayor.</summary>
    private static BitmapFrame LargestIconFrame()
    {
        var decoder = BitmapDecoder.Create(
            new Uri("pack://application:,,,/ClonDisk;component/Assets/ClonDisk.ico"),
            BitmapCreateOptions.None,
            BitmapCacheOption.OnLoad);
        return decoder.Frames.OrderByDescending(frame => frame.PixelWidth).First();
    }
}
