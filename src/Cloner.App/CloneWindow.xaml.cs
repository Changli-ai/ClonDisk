using System.ComponentModel;
using System.Windows;
using Cloner.App.Controls;
using Cloner.App.ViewModels;
using Microsoft.Win32;

namespace Cloner.App;

/// <summary>Asistente de clonado. Mientras se copia, cerrar la ventana equivale a pedir la cancelación.</summary>
public partial class CloneWindow : RoundedWindow, ICloneDialogs
{
    public CloneWindow()
    {
        InitializeComponent();
    }

    public CloneViewModel? ViewModel
    {
        get => DataContext as CloneViewModel;
        set => DataContext = value;
    }

    public bool ConfirmCancel() => MessageDialog.Confirm(
        this,
        "Cancelar el clonado",
        "Si cancelas, el disco de destino quedará incompleto y sin tabla de particiones.",
        "Cancelar el clonado",
        "Seguir clonando",
        MessageKind.Warning,
        dangerous: true);

    public string? ChooseLogDestination(string suggestedName)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Exportar el registro",
            FileName = suggestedName,
            DefaultExt = ".log",
            Filter = "Registro (*.log)|*.log|Texto (*.txt)|*.txt",
        };
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    public void ShowError(string message) =>
        MessageDialog.Show(this, "No se pudo continuar", message, MessageKind.Error);

    protected override void OnClosing(CancelEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (ViewModel is { IsRunning: true } running)
        {
            // Mientras se cancela no se vuelve a preguntar: basta con esperar a que termine.
            e.Cancel = true;
            if (!running.IsCancelling)
            {
                running.CancelCommand.Execute(null);
            }
        }

        base.OnClosing(e);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
