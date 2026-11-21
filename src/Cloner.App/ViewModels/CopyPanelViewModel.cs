using System.Globalization;
using Cloner.Core;
using Cloner.Core.Cloning;
using Cloner.Core.Inventory;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cloner.App.ViewModels;

/// <summary>Lo que muestra una tarjeta de origen o destino cuando tiene algo elegido.</summary>
/// <param name="Glyph">Icono (Segoe MDL2 Assets).</param>
public sealed record CardContent(int DiskNumber, string Title, string Details, string Glyph)
{
    /// <summary>Es la tarjeta del destino (verde); si no, la del origen (azul).</summary>
    public bool IsTarget { get; init; }

    public string Role => IsTarget ? "DESTINO" : "ORIGEN";

    /// <summary>Datos cortos: tipo y conexión, tabla de particiones, número de particiones.</summary>
    public IReadOnlyList<string> Chips { get; init; } = [];

    /// <summary>Texto del candado si este equipo necesita el disco.</summary>
    public string? LockLabel { get; init; }

    public bool IsLocked => LockLabel is not null;

    /// <summary>Cuatro datos destacados: usado, libre, salud y temperatura.</summary>
    public IReadOnlyList<PropertyRow> Stats { get; init; } = [];

    /// <summary>Mapa de particiones.</summary>
    public IReadOnlyList<SegmentViewModel> Segments { get; init; } = [];

    /// <summary>Leyenda del mapa: una línea por partición.</summary>
    public IReadOnlyList<LegendItem> Legend { get; init; } = [];

    /// <summary>Línea al pie: qué se copiará o qué se borrará.</summary>
    public string Footer { get; init; } = string.Empty;
}

/// <summary>Una línea de la leyenda del mapa de particiones.</summary>
public sealed record LegendItem(string Text, System.Windows.Media.Brush Fill);

/// <summary>
/// Pantalla de copia con dos tarjetas, origen y destino, unidas por una flecha. Vacías invitan a
/// elegir; al elegir muestran el disco. «Siguiente» abre el asistente con ambos ya elegidos.
/// </summary>
public sealed partial class CopyPanelViewModel : ObservableObject
{
    private const string DiskGlyph = "";
    private const string WindowsGlyph = "";
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("es-ES");

    private readonly Func<IReadOnlyList<DiskInfo>> _disks;
    private readonly Func<string, IReadOnlyList<DiskChoice>, DiskChoice?> _pick;
    private readonly Func<int, int, Task> _start;

    /// <param name="systemMode">El origen es siempre el disco del sistema en uso y no se cambia.</param>
    public CopyPanelViewModel(
        string title,
        string subtitle,
        bool systemMode,
        Func<IReadOnlyList<DiskInfo>> disks,
        Func<string, IReadOnlyList<DiskChoice>, DiskChoice?> pick,
        Func<int, int, Task> start)
    {
        Title = title;
        Subtitle = subtitle;
        IsSystemMode = systemMode;
        _disks = disks;
        _pick = pick;
        _start = start;
    }

    public string Title { get; }
    public string Subtitle { get; }
    public bool IsSystemMode { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSource), nameof(CanClearSource))]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    public partial CardContent? Source { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTarget))]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    public partial CardContent? Target { get; set; }

    public bool HasSource => Source is not null;
    public bool HasTarget => Target is not null;
    public bool CanClearSource => HasSource && !IsSystemMode;

    /// <summary>Discos que se pueden elegir directamente desde la tarjeta vacía del origen.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<DiskChoice> SourceSuggestions { get; set; } = [];

    /// <summary>Discos para la tarjeta vacía del destino; los protegidos salen con candado y desactivados.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<DiskChoice> TargetSuggestions { get; set; } = [];

    /// <summary>Hay algún disco con candado: se muestra el aviso bajo las tarjetas.</summary>
    [ObservableProperty]
    public partial bool HasLockedDisks { get; set; }

    partial void OnSourceChanged(CardContent? value) => UpdateSuggestions();

    partial void OnTargetChanged(CardContent? value) => UpdateSuggestions();

    private void UpdateSuggestions()
    {
        SourceSuggestions = Choices(exclude: Target?.DiskNumber, asTarget: false);
        TargetSuggestions = Choices(exclude: Source?.DiskNumber, asTarget: true);
        HasLockedDisks = TargetSuggestions.Any(c => c.IsLocked) || SourceSuggestions.Any(c => c.IsLocked);
    }

    [RelayCommand]
    private void ChooseSource(DiskChoice? choice)
    {
        if (!IsSystemMode && choice is not null)
        {
            Source = DiskCard(_disks().First(d => d.Number == choice.Number), isTarget: false);
        }
    }

    [RelayCommand]
    private void ChooseTarget(DiskChoice? choice)
    {
        if (choice is { IsSelectable: true })
        {
            Target = DiskCard(_disks().First(d => d.Number == choice.Number), isTarget: true);
        }
    }

    /// <summary>Se llama tras leer los discos: quita lo que ya no existe y, en modo sistema, fija el origen.</summary>
    public void Refresh()
    {
        var disks = _disks();
        if (IsSystemMode)
        {
            Source = disks.FirstOrDefault(d => d.ContainsWindows) is { } system ? SystemCard(system) : null;
        }
        else if (Source is { } current && disks.FirstOrDefault(d => d.Number == current.DiskNumber) is { } sourceDisk)
        {
            Source = DiskCard(sourceDisk, isTarget: false);
        }
        else if (Source is { } source && disks.All(d => d.Number != source.DiskNumber))
        {
            Source = null;
        }

        if (Target is { } target && disks.All(d => d.Number != target.DiskNumber))
        {
            Target = null;
        }

        UpdateSuggestions();
    }

    [RelayCommand]
    private void PickSource()
    {
        if (IsSystemMode)
        {
            return;
        }

        var choice = _pick("Elige el disco de origen", Choices(exclude: Target?.DiskNumber, asTarget: false));
        if (choice is not null)
        {
            Source = DiskCard(_disks().First(d => d.Number == choice.Number), isTarget: false);
        }
    }

    [RelayCommand]
    private void PickTarget()
    {
        var choice = _pick("Elige el disco de destino", Choices(exclude: Source?.DiskNumber, asTarget: true));
        if (choice is not null)
        {
            Target = DiskCard(_disks().First(d => d.Number == choice.Number), isTarget: true);
        }
    }

    [RelayCommand]
    private void ClearSource()
    {
        if (!IsSystemMode)
        {
            Source = null;
        }
    }

    [RelayCommand]
    private void ClearTarget() => Target = null;

    private bool CanNext => Source is not null && Target is not null;

    [RelayCommand(CanExecute = nameof(CanNext))]
    private Task NextAsync() => _start(Source!.DiskNumber, Target!.DiskNumber);

    /// <summary>
    /// Discos para elegir. En la lista de destinos los que este equipo necesita (Windows en uso, su
    /// arranque y el disco de la aplicación) salen con candado y no se pueden elegir.
    /// </summary>
    private List<DiskChoice> Choices(int? exclude, bool asTarget)
    {
        var disks = _disks();
        var host = DiskProtection.HostDiskNumber(disks);
        return disks.Where(d => d.Number != exclude).Select(d => DiskChoice.From(d, host, asTarget)).ToList();
    }

    private CardContent DiskCard(DiskInfo disk, bool isTarget) =>
        Describe(new CardContent(disk.Number, disk.Model, $"Disco {disk.Number} · {ByteSize.Format(disk.SizeBytes, Culture)}", DiskGlyph), disk, isTarget);

    /// <summary>El origen del modo sistema: la versión de Windows y las particiones del disco donde está.</summary>
    private CardContent SystemCard(DiskInfo disk)
    {
        var parts = disk.Partitions
            .Where(p => p.Kind != PartitionKind.Extended && p.Kind != PartitionKind.MicrosoftReserved)
            .Select(p => p.DriveLetter is { } letter ? $"{letter}:" : p.Kind == PartitionKind.EfiSystem ? "EFI" : InventoryText.Kind(p.Kind));
        return Describe(new CardContent(disk.Number, WindowsInfo.Name, $"{string.Join(", ", parts)} · Disco {disk.Number}", WindowsGlyph), disk, isTarget: false);
    }

    /// <summary>Completa la tarjeta con los datos del disco: etiquetas, cifras, mapa y leyenda.</summary>
    private CardContent Describe(CardContent card, DiskInfo disk, bool isTarget)
    {
        var partitions = disk.Partitions.Where(p => p.Kind != PartitionKind.Extended).ToList();
        var volumes = partitions.Where(p => p.Volume is not null).Select(p => p.Volume!).ToList();
        var used = volumes.Sum(v => Math.Max(0, v.SizeBytes - v.FreeBytes));
        var free = volumes.Sum(v => v.FreeBytes);
        var view = new DiskViewModel(disk);
        return card with
        {
            IsTarget = isTarget,
            Chips =
            [
                $"{InventoryText.Media(disk.Media)} {InventoryText.Bus(disk.Bus)}",
                InventoryText.Style(disk.PartitionStyle),
                partitions.Count == 1 ? "1 partición" : $"{partitions.Count} particiones",
            ],
            LockLabel = DiskProtection.LockLabel(disk, DiskProtection.HostDiskNumber(_disks())),
            Stats =
            [
                new("Usado", volumes.Count == 0 ? "—" : ByteSize.Format(used, Culture)),
                new("Libre", volumes.Count == 0 ? "—" : ByteSize.Format(free, Culture)),
                new("Salud", InventoryText.Health(disk.Health)),
                new("Temperatura", disk.Reliability?.TemperatureCelsius is { } t ? $"{t} °C" : "—"),
            ],
            Segments = view.Segments,
            Legend = partitions.Take(6).Select(p => new LegendItem(LegendText(p), PartitionPalette.For(p).Fill)).ToList(),
            Footer = isTarget
                ? "Se borrará todo su contenido actual antes de copiar."
                : volumes.Count == 0 ? "Se copiará entero, sector a sector." : $"Hay {ByteSize.Format(used, Culture)} en uso: la copia inteligente copia solo eso.",
        };
    }

    private static string LegendText(PartitionInfo partition)
    {
        var name = partition.DriveLetter is { } letter
            ? $"{letter}: {(string.IsNullOrEmpty(partition.Volume?.Label) ? InventoryText.Kind(partition.Kind) : partition.Volume!.Label)}"
            : InventoryText.Kind(partition.Kind);
        return $"{name} · {ByteSize.Format(partition.SizeBytes, Culture)}";
    }
}

/// <summary>Nombre de la versión de Windows en uso, para mostrar.</summary>
internal static class WindowsInfo
{
    public static string Name { get; } = Read();

    private static string Read()
    {
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        var product = key?.GetValue("ProductName") as string ?? "Windows";

        // Windows 11 sigue declarando «Windows 10» en ProductName; se distingue por el número de compilación.
        if (int.TryParse(key?.GetValue("CurrentBuild") as string, NumberStyles.None, CultureInfo.InvariantCulture, out var build) && build >= 22000)
        {
            product = product.Replace("Windows 10", "Windows 11", StringComparison.Ordinal);
        }

        var bits = Environment.Is64BitOperatingSystem ? "64 bits" : "32 bits";
        return $"Microsoft {product} {bits}";
    }
}
