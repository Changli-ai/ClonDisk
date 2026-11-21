using System.Windows;
using System.Windows.Media;
using Cloner.App.Controls;

namespace Cloner.App;

/// <summary>Tipo de mensaje: decide el icono y su color.</summary>
public enum MessageKind
{
    Information,
    Success,
    Warning,
    Error,
    Question,
}

/// <summary>Mensaje o pregunta con la misma ventana redondeada que el resto de ClonDisk (en lugar del MessageBox de Windows).</summary>
public partial class MessageDialog : RoundedWindow
{
    private MessageDialog()
    {
        InitializeComponent();
    }

    /// <summary>Muestra un aviso con un solo botón.</summary>
    public static void Show(Window? owner, string title, string message, MessageKind kind) =>
        Create(owner, title, message, kind, "Aceptar", cancelText: null, dangerous: false).ShowDialog();

    /// <summary>Pregunta sí o no. <paramref name="dangerous"/> pinta en rojo el botón de aceptar (borrar datos, cancelar).</summary>
    public static bool Confirm(Window? owner, string title, string message, string confirmText, string cancelText = "Cancelar", MessageKind kind = MessageKind.Question, bool dangerous = false) =>
        Create(owner, title, message, kind, confirmText, cancelText, dangerous).ShowDialog() == true;

    private static MessageDialog Create(Window? owner, string title, string message, MessageKind kind, string confirmText, string? cancelText, bool dangerous)
    {
        var dialog = new MessageDialog { Title = $"{title} · ClonDisk" };
        if (owner is { IsLoaded: true })
        {
            dialog.Owner = owner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        dialog.Heading.Text = title;
        dialog.Message.Text = message;
        dialog.YesButton.Content = confirmText;
        if (dangerous)
        {
            dialog.YesButton.Style = (Style)Application.Current.FindResource("DangerButton");
        }

        if (cancelText is null)
        {
            dialog.NoButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            dialog.NoButton.Content = cancelText;

            // En las preguntas, Intro no confirma: hay que pulsar el botón a propósito.
            dialog.YesButton.IsDefault = false;
            dialog.NoButton.Focus();
        }

        var (glyph, color, back) = kind switch
        {
            MessageKind.Success => ("", "#16A365", "#E8F6EE"),
            MessageKind.Warning => ("", "#B07A00", "#FFF4DC"),
            MessageKind.Error => ("", "#C9372C", "#FDEDED"),
            MessageKind.Question => ("", "#1F6FEB", "#E7F0FE"),
            _ => ("", "#1F6FEB", "#E7F0FE"),
        };
        dialog.IconGlyph.Text = glyph;
        dialog.IconGlyph.Foreground = Brush(color);
        dialog.IconBox.Background = Brush(back);
        return dialog;
    }

    private static SolidColorBrush Brush(string color) => new((Color)ColorConverter.ConvertFromString(color));

    private void OnYesClick(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnNoClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
