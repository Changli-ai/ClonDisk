using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Cloner.App.Controls;

/// <summary>Colapsado si el valor es verdadero; visible si es falso.</summary>
public sealed class NotVisibleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
