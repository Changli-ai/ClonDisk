using System.Globalization;

namespace Cloner.Core;

/// <summary>Formato legible de tamaños en bytes.</summary>
public static class ByteSize
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB", "EB"];

    /// <summary>
    /// Convierte un tamaño en bytes a texto con la convención de Windows:
    /// múltiplos de 1024 etiquetados como KB, MB, GB…, con dos decimales como máximo.
    /// </summary>
    /// <param name="bytes">Tamaño en bytes; no puede ser negativo.</param>
    /// <param name="provider">Cultura para el separador decimal; por defecto, la actual.</param>
    public static string Format(long bytes, IFormatProvider? provider = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);

        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        var number = value.ToString("0.##", provider ?? CultureInfo.CurrentCulture);
        return $"{number} {Units[unit]}";
    }
}
