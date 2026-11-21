using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Cloner.App.Controls;

/// <summary>
/// Pasos de un clonado (Discos · Opciones · Revisar · Clonar) con el actual resaltado. Los
/// anteriores se marcan como hechos.
/// </summary>
public sealed class StepIndicator : StackPanel
{
    public static readonly DependencyProperty CurrentProperty = DependencyProperty.Register(
        nameof(Current), typeof(int), typeof(StepIndicator), new PropertyMetadata(1, (d, _) => ((StepIndicator)d).Build()));

    private static readonly string[] Steps = ["Discos", "Opciones", "Revisar", "Clonar"];

    public StepIndicator()
    {
        Orientation = Orientation.Horizontal;
        VerticalAlignment = VerticalAlignment.Center;
        Build();
    }

    /// <summary>Paso actual, de 1 a 4.</summary>
    public int Current
    {
        get => (int)GetValue(CurrentProperty);
        set => SetValue(CurrentProperty, value);
    }

    private void Build()
    {
        Children.Clear();
        var accent = (Brush)Application.Current.FindResource("Accent");
        var muted = (Brush)Application.Current.FindResource("TextTertiary");
        var track = (Brush)Application.Current.FindResource("StepTrack");
        var success = (Brush)Application.Current.FindResource("SuccessStrong");

        for (var i = 0; i < Steps.Length; i++)
        {
            var number = i + 1;
            var isCurrent = number == Current;
            var isDone = number < Current;
            if (i > 0)
            {
                Children.Add(new Border
                {
                    Width = 22,
                    Height = 2,
                    CornerRadius = new CornerRadius(1),
                    Background = isDone || isCurrent ? accent : track,
                    Margin = new Thickness(8, 0, 8, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                });
            }

            var label = new TextBlock
            {
                Text = isDone ? "" : number.ToString(System.Globalization.CultureInfo.InvariantCulture),
                FontSize = isDone ? 10 : 11.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = isCurrent || isDone ? Brushes.White : muted,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            if (isDone)
            {
                label.FontFamily = new FontFamily("Segoe MDL2 Assets");
            }

            var circle = new Border
            {
                Width = 22,
                Height = 22,
                CornerRadius = new CornerRadius(11),
                Background = isCurrent ? accent : isDone ? success : track,
                Child = label,
            };

            Children.Add(circle);
            Children.Add(new TextBlock
            {
                Text = Steps[i],
                Margin = new Thickness(7, 0, 0, 0),
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = isCurrent ? accent : muted,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }
    }
}
