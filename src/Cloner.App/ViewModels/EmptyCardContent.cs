using System.Windows.Media;

namespace Cloner.App.ViewModels;

/// <summary>Texto y color del botón «+» de una tarjeta vacía.</summary>
public sealed class EmptyCardContent
{
    public string Prompt { get; set; } = string.Empty;
    public Brush? Tile { get; set; }
}
