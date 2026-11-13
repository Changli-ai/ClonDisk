using System.Windows;
using System.Windows.Controls;

namespace Cloner.App.Controls;

/// <summary>
/// Reparte el ancho disponible entre sus hijos en proporción a su <c>Weight</c>.
/// Se usa para dibujar el mapa de particiones de un disco.
/// </summary>
public sealed class ProportionalPanel : Panel
{
    public static readonly DependencyProperty WeightProperty = DependencyProperty.RegisterAttached(
        "Weight",
        typeof(double),
        typeof(ProportionalPanel),
        new FrameworkPropertyMetadata(
            1.0,
            FrameworkPropertyMetadataOptions.AffectsParentMeasure | FrameworkPropertyMetadataOptions.AffectsParentArrange));

    public static double GetWeight(UIElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (double)element.GetValue(WeightProperty);
    }

    public static void SetWeight(UIElement element, double value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(WeightProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var totalWeight = TotalWeight();
        var width = double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width;
        var height = 0.0;

        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(width * Share(child, totalWeight), availableSize.Height));
            height = Math.Max(height, child.DesiredSize.Height);
        }

        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var totalWeight = TotalWeight();
        var x = 0.0;

        foreach (UIElement child in InternalChildren)
        {
            var width = finalSize.Width * Share(child, totalWeight);
            child.Arrange(new Rect(x, 0, width, finalSize.Height));
            x += width;
        }

        return finalSize;
    }

    private double TotalWeight()
    {
        var total = 0.0;
        foreach (UIElement child in InternalChildren)
        {
            total += Math.Max(0, GetWeight(child));
        }

        return total;
    }

    private static double Share(UIElement child, double totalWeight) =>
        totalWeight > 0 ? Math.Max(0, GetWeight(child)) / totalWeight : 0;
}
