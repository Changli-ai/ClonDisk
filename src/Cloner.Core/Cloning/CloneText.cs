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

    public static string Mode(CloneMode mode) => mode switch
    {
        CloneMode.UsedBlocks => "copia inteligente (solo los bloques en uso)",
        CloneMode.SectorBySector => "sector a sector (copia íntegra)",
        _ => mode.ToString(),
    };

    /// <summary>«Se copiarán unos 350 GB, solo los bloques en uso» o «Se copiarán 954 GB, sector a sector».</summary>
    public static string CopyAmount(ClonePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var size = ByteSize.Format(plan.BytesToCopy, Culture);
        var estimated = plan.Steps.Any(s => s.UsedBlocksOnly || s.Kind == CloneStepKind.CopyFiles);
        var text = plan.Mode == CloneMode.UsedBlocks
            ? $"Se copiarán {(estimated ? "unos " : string.Empty)}{size}, solo los bloques en uso"
            : $"Se copiarán {size}, sector a sector";
        return plan.FileSteps.Count > 0 ? text + " (archivo a archivo en las particiones reducidas)" : text;
    }

    /// <summary>«GPT (se convierte desde MBR)» o el estilo del origen.</summary>
    public static string TableStyle(ClonePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var target = Style(plan.TargetStyle);
        return plan.Layout.Style is null ? target : $"{target} (se convierte desde {Style(plan.Source.PartitionStyle)})";
    }

    public static string Style(PartitionStyle style) => style switch
    {
        PartitionStyle.Gpt => "GPT",
        PartitionStyle.Mbr => "MBR",
        _ => "sin inicializar",
    };

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

    /// <summary>
    /// Cómo quedan las particiones en el destino, una línea por partición que cambia de posición
    /// o tamaño; vacío si la distribución es la del origen.
    /// </summary>
    public static IReadOnlyList<string> Layout(ClonePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.Layout.Changes)
        {
            return [];
        }

        var created = plan.Layout.Created.Select(c =>
            $"{(c.Kind == PartitionKind.EfiSystem ? "Partición EFI nueva (FAT32)" : "Partición reservada de Microsoft nueva")}: {ByteSize.Format(c.Size, Culture)}");
        var placements = plan.Layout.Placements.Select(placement =>
        {
            var partition = placement.Partition;
            var name = partition.DriveLetter is { } letter ? $"{letter}:" : partition.Volume?.Label ?? InventoryText.Kind(partition.Kind);
            var size = placement.Grows || placement.Shrinks
                ? $"{ByteSize.Format(partition.SizeBytes, Culture)} → {ByteSize.Format(placement.TargetSize, Culture)}"
                : ByteSize.Format(partition.SizeBytes, Culture);
            var position = placement.Moves
                ? $", se mueve de {ByteSize.Format(partition.OffsetBytes, Culture)} a {ByteSize.Format(placement.TargetOffset, Culture)} del inicio"
                : string.Empty;
            var files = placement.Shrinks ? ", se copia archivo a archivo" : string.Empty;
            return $"Partición {partition.Number} ({name}): {size}{position}{files}";
        });
        return created.Concat(placements).ToList();
    }

    public static string Sizing(Cloner.Core.Layout.SizeMode mode) => mode switch
    {
        Cloner.Core.Layout.SizeMode.ExtendMain => "ampliar la partición principal",
        Cloner.Core.Layout.SizeMode.Proportional => "repartir el espacio entre las particiones de datos",
        _ => "mantener los tamaños",
    };

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
