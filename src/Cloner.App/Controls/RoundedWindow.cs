using System.Windows;
using System.Windows.Controls;

namespace Cloner.App.Controls;

/// <summary>
/// Ventana emergente con las esquinas redondeadas, sombra y una barra de título propia (Windows 10
/// no redondea las ventanas por sí mismo). Se arrastra por la barra y se cierra con su «×».
/// </summary>
public class RoundedWindow : Window
{
    public RoundedWindow()
    {
        Style = (Style)Application.Current.FindResource("RoundedWindowStyle");
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        if (GetTemplateChild("PART_Close") is Button close)
        {
            close.Click += (_, _) => Close();
        }
    }
}
