using System.Globalization;
using Cloner.Core.Inventory;

namespace Cloner.Core.Cloning;

/// <summary>Textos en español del clonado que comparten la CLI y la interfaz.</summary>
public static class CloneText
{
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("es-ES");

    public const string ConfirmationWord = "BORRAR";

    public static string Phase(ClonePhase phase) => phase switch
    {
        ClonePhase.Preparing => "Preparando",
        ClonePhase.Copying => "Copiando",
        ClonePhase.Verifying => "Verificando",
        ClonePhase.Finishing => "Terminando",
        _ => phase.ToString(),
    };

    public static string Outcome(CloneOutcome outcome) => outcome switch
    {
        CloneOutcome.Succeeded => "Completado",
        CloneOutcome.Cancelled => "Cancelado",
        CloneOutcome.Failed => "Ha fallado",
        CloneOutcome.VerificationFailed => "Verificación fallida",
        _ => outcome.ToString(),
    };

    public static string Percent(double fraction) => fraction.ToString("P1", Culture);

    /// <summary>«312 MB/s», o un guion mientras no hay medida.</summary>
    public static string Speed(double bytesPerSecond) =>
        bytesPerSecond > 0 ? $"{ByteSize.Format((long)bytesPerSecond, Culture)}/s" : "—";

    /// <summary>«1:05:09», «3:12» o un guion si no se puede estimar.</summary>
    public static string Duration(TimeSpan? duration)
    {
        if (duration is not { } value)
        {
            return "—";
        }

        var seconds = (long)Math.Ceiling(value.TotalSeconds);
        var hours = seconds / 3600;
        var minutes = seconds / 60 % 60;
        var rest = seconds % 60;
        return hours > 0
            ? string.Create(Culture, $"{hours}:{minutes:00}:{rest:00}")
            : string.Create(Culture, $"{minutes}:{rest:00}");
    }

    /// <summary>Una partición tal como se presenta en la lista de lo que se va a borrar.</summary>
    public static string Partition(PartitionInfo partition)
    {
        ArgumentNullException.ThrowIfNull(partition);
        var name = partition.DriveLetter is { } letter
            ? partition.Volume?.Label is { } label ? $"{letter}: {label}" : $"{letter}:"
            : partition.Volume?.Label ?? InventoryText.Kind(partition.Kind);

        var details = new List<string> { $"Partición {partition.Number} ({name})" };
        if (partition.Volume is { } volume)
        {
            details.Add(volume.FileSystem ?? "sin formato");
            details.Add($"{ByteSize.Format(partition.SizeBytes, Culture)}, {ByteSize.Format(volume.UsedBytes, Culture)} usados");
        }
        else
        {
            details.Add(ByteSize.Format(partition.SizeBytes, Culture));
        }

        return string.Join(" · ", details);
    }
}
