using System.Windows.Media;
using Cloner.Core.Inventory;

namespace Cloner.App.ViewModels;

/// <summary>Colores del mapa de particiones.</summary>
internal static class PartitionPalette
{
    public static readonly Brush LightText = Frozen("#FFFFFF");
    public static readonly Brush DarkText = Frozen("#3B4656");
    public static readonly Brush Unallocated = Frozen("#DDE2EA");

    private static readonly Brush Windows = Frozen("#1F6FEB");
    private static readonly Brush Data = Frozen("#2E9C8F");
    private static readonly Brush Efi = Frozen("#7A5AF0");
    private static readonly Brush Reserved = Frozen("#8792A4");
    private static readonly Brush Recovery = Frozen("#C9781F");
    private static readonly Brush Other = Frozen("#5B6677");

    public static (Brush Fill, Brush Foreground) For(PartitionInfo partition)
    {
        var fill = partition.Kind switch
        {
            _ when partition.ContainsWindows => Windows,
            PartitionKind.EfiSystem => Efi,
            PartitionKind.MicrosoftReserved => Reserved,
            PartitionKind.BasicData => Data,
            PartitionKind.Recovery => Recovery,
            _ => Other,
        };

        return (fill, LightText);
    }

    private static SolidColorBrush Frozen(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }
}
