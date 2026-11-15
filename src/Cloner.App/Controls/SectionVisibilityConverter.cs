using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Cloner.App.Controls;

/// <summary>Visible si el valor (la sección actual) coincide con el parámetro; si no, colapsado.</summary>
public sealed class SectionVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
