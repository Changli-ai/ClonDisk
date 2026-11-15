using System.Windows;
using System.Windows.Input;
using Cloner.App.ViewModels;

namespace Cloner.App;

/// <summary>Lista de discos para elegir el origen o el destino.</summary>
public partial class DiskPickerWindow : Window
{
    public DiskPickerWindow()
    {
        InitializeComponent();
    }

    /// <summary>Muestra la lista y devuelve el disco elegido, o nulo si se cancela.</summary>
    public static DiskChoice? Pick(Window owner, string title, IReadOnlyList<DiskChoice> choices)
    {
        var window = new DiskPickerWindow { Owner = owner };
        window.Heading.Text = title;
        window.Choices.ItemsSource = choices;
        if (choices.Count > 0)
        {
            window.Choices.SelectedIndex = 0;
        }

        return window.ShowDialog() == true ? window.Choices.SelectedItem as DiskChoice : null;
    }

    private void OnChooseClick(object sender, RoutedEventArgs e)
    {
        if (Choices.SelectedItem is not null)
        {
            DialogResult = true;
        }
    }

    private void OnChoiceDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Choices.SelectedItem is not null)
        {
            DialogResult = true;
        }
    }
}
