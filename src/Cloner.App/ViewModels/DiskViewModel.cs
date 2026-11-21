using System.Globalization;
using System.Windows.Media;
using Cloner.Core;
using Cloner.Core.Cloning;
using Cloner.Core.Inventory;

namespace Cloner.App.ViewModels;

/// <summary>Una fila "etiqueta: valor" de las propiedades del disco.</summary>
public sealed record PropertyRow(string Label, string Value);

/// <summary>Un tramo del mapa de particiones: una partición o un hueco sin asignar.</summary>
public sealed record SegmentViewModel(string Label, string SizeText, double Weight, Brush Fill, Brush Foreground, string ToolTip);

/// <summary>Una fila de la tabla de particiones.</summary>
public sealed record PartitionViewModel(
    int Number,
    string Kind,
    string Volume,
    string FileSystem,
    string Size,
    string Used,
    string Encryption,
    string Flags);

/// <summary>Un disco preparado para mostrarse en pantalla.</summary>
public sealed class DiskViewModel
{
    private const string None = "—";

    /// <summary>Los huecos menores no se dibujan: son relleno de alineación.</summary>
    private const long MinimumGapBytes = 16L * 1024 * 1024;

    /// <summary>Peso mínimo de un tramo, para que las particiones pequeñas sigan siendo visibles.</summary>
    private const double MinimumWeight = 0.1;

    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("es-ES");

    /// <param name="hostDiskNumber">Disco desde el que se ejecuta la aplicación, si se sabe.</param>
    public DiskViewModel(DiskInfo disk, int? hostDiskNumber = null)
    {
        ArgumentNullException.ThrowIfNull(disk);

        Number = disk.Number;
        Model = disk.Model;
        Heading = $"Disco {disk.Number}";
        Summary = $"{Size(disk.SizeBytes)} · {InventoryText.Media(disk.Media)} {InventoryText.Bus(disk.Bus)} · {InventoryText.Style(disk.PartitionStyle)}";
        IsSystemDisk = disk.IsSystemDisk;
        LockLabel = DiskProtection.LockLabel(disk, hostDiskNumber);
        LockReason = DiskProtection.WhyLocked(disk, hostDiskNumber);
        CanPrepare = DiskPreparationService.CannotPrepare(disk, hostDiskNumber) is null;
        Warnings = BuildWarnings(disk);
        Properties = BuildProperties(disk);
        Identity =
        [
            new("Número de serie", disk.SerialNumber ?? None),
            new("Identificador", disk.Identifier ?? None),
        ];
        Segments = BuildSegments(disk);
        Partitions = disk.Partitions.Select(BuildPartition).ToList();
        CanRepairBoot = BootRepairService.CannotRepair(disk) is null;
    }

    /// <summary>Puede tener un Windows clonado cuyo arranque se puede rehacer.</summary>
    public bool CanRepairBoot { get; }

    /// <summary>Se puede limpiar y formatear: no es un disco que este equipo necesite.</summary>
    public bool CanPrepare { get; }

    /// <summary>Texto del candado («Windows en uso»…) si este equipo necesita el disco; nulo si no.</summary>
    public string? LockLabel { get; }

    /// <summary>Por qué el disco está protegido contra escritura; nulo si no lo está.</summary>
    public string? LockReason { get; }

    public bool IsLocked => LockLabel is not null;

    public int Number { get; }
    public string Model { get; }
    public string Heading { get; }
    public string Summary { get; }
    public bool IsSystemDisk { get; }
    public IReadOnlyList<string> Warnings { get; }
    public bool HasWarnings => Warnings.Count > 0;
    /// <summary>Propiedades cortas, mostradas en dos columnas.</summary>
    public IReadOnlyList<PropertyRow> Properties { get; }

    /// <summary>Datos que identifican el disco; son largos y ocupan una fila completa cada uno.</summary>
    public IReadOnlyList<PropertyRow> Identity { get; }

    public IReadOnlyList<SegmentViewModel> Segments { get; }
    public IReadOnlyList<PartitionViewModel> Partitions { get; }
    public bool HasPartitions => Partitions.Count > 0;

    private static List<string> BuildWarnings(DiskInfo disk)
    {
        var warnings = new List<string>(InventoryText.Issues(DiskHealth.Assess(disk)));

        if (disk.Partitions.Any(p => p.Encryption is EncryptionState.On or EncryptionState.Suspended))
        {
            warnings.Add(InventoryText.EncryptedPartitionsWarning);
        }

        if (disk.IsOffline) warnings.Add("El disco está sin conexión en Windows.");
        if (disk.IsReadOnly) warnings.Add("El disco está marcado como de solo lectura.");
        return warnings;
    }

    private static List<PropertyRow> BuildProperties(DiskInfo disk)
    {
        var reliability = disk.Reliability;
        return
        [
            new("Tipo", InventoryText.Media(disk.Media)),
            new("Conexión", InventoryText.Bus(disk.Bus)),
            new("Capacidad", Size(disk.SizeBytes)),
            new("Tabla de particiones", InventoryText.Style(disk.PartitionStyle)),
            new("Sector lógico / físico", $"{disk.LogicalSectorSize} B / {disk.PhysicalSectorSize} B"),
            new("Estado", InventoryText.Health(disk.Health)),
            new("Temperatura", reliability?.TemperatureCelsius is { } t ? $"{t} °C" : None),
            new("Vida útil consumida", reliability?.WearPercent is { } w ? $"{w} %" : None),
            new("Horas de encendido", reliability?.PowerOnHours is { } h ? h.ToString("N0", Culture) : None),
            new("Errores no corregidos", reliability is null
                ? None
                : $"{reliability.ReadErrorsUncorrected ?? 0} lectura / {reliability.WriteErrorsUncorrected ?? 0} escritura"),
        ];
    }

    private static List<SegmentViewModel> BuildSegments(DiskInfo disk)
    {
        var segments = new List<SegmentViewModel>();
        if (disk.SizeBytes <= 0)
        {
            return segments;
        }

        long position = 0;
        foreach (var partition in disk.Partitions.Where(p => p.Kind != PartitionKind.Extended))
        {
            AddGap(segments, disk, partition.OffsetBytes - position);

            var name = partition.DriveLetter is { } letter ? $"{letter}:" : ShortKind(partition.Kind);
            var (fill, foreground) = PartitionPalette.For(partition);
            segments.Add(new SegmentViewModel(
                name,
                Size(partition.SizeBytes),
                Weight(partition.SizeBytes, disk.SizeBytes),
                fill,
                foreground,
                $"Partición {partition.Number} · {InventoryText.Kind(partition.Kind)} · {Size(partition.SizeBytes)}"));

            position = partition.OffsetBytes + partition.SizeBytes;
        }

        AddGap(segments, disk, disk.SizeBytes - position);
        return segments;
    }

    private static void AddGap(List<SegmentViewModel> segments, DiskInfo disk, long gapBytes)
    {
        if (gapBytes < MinimumGapBytes)
        {
            return;
        }

        segments.Add(new SegmentViewModel(
            "Sin asignar",
            Size(gapBytes),
            Weight(gapBytes, disk.SizeBytes),
            PartitionPalette.Unallocated,
            PartitionPalette.DarkText,
            $"Espacio sin asignar · {Size(gapBytes)}"));
    }

    private static PartitionViewModel BuildPartition(PartitionInfo partition)
    {
        var volume = partition.Volume;
        return new PartitionViewModel(
            partition.Number,
            InventoryText.Kind(partition.Kind),
            VolumeName(partition),
            volume?.FileSystem ?? None,
            Size(partition.SizeBytes),
            volume is null ? None : Size(volume.UsedBytes),
            InventoryText.Encryption(partition.Encryption),
            InventoryText.Flags(partition));
    }

    /// <summary>Letra y etiqueta del volumen: "C:", "D: Datos", "Datos" o un guion.</summary>
    private static string VolumeName(PartitionInfo partition)
    {
        var letter = partition.DriveLetter is { } l ? $"{l}:" : null;
        var name = string.Join(' ', new[] { letter, partition.Volume?.Label }.Where(part => part is not null));
        return name.Length > 0 ? name : None;
    }

    /// <summary>Nombre abreviado para los tramos estrechos del mapa.</summary>
    private static string ShortKind(PartitionKind kind) => kind switch
    {
        PartitionKind.EfiSystem => "EFI",
        PartitionKind.MicrosoftReserved => "MSR",
        PartitionKind.Recovery => "Recup.",
        _ => InventoryText.Kind(kind),
    };

    private static double Weight(long bytes, long diskBytes) => Math.Max((double)bytes / diskBytes, MinimumWeight);

    private static string Size(long bytes) => ByteSize.Format(bytes, Culture);
}
