using System.Windows;
using System.Windows.Input;
using Cloner.App.Controls;
using Cloner.App.ViewModels;

namespace Cloner.App;

/// <summary>Lista de discos para elegir el origen o el destino.</summary>
public partial class DiskPickerWindow : RoundedWindow
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
        // Se preselecciona el primero que se puede elegir: nunca un disco con candado de la lista de destinos.
        window.Choices.SelectedItem = choices.FirstOrDefault(c => c.IsSelectable);

        return window.ShowDialog() == true && window.Choices.SelectedItem is DiskChoice { IsSelectable: true } choice ? choice : null;
    }

    private void OnChooseClick(object sender, RoutedEventArgs e)
    {
        if (Choices.SelectedItem is DiskChoice { IsSelectable: true })
        {
            DialogResult = true;
        }
    }

    private void OnChoiceDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Choices.SelectedItem is DiskChoice { IsSelectable: true })
        {
            DialogResult = true;
        }
    }
}
