using System.Globalization;
using Cloner.Core;
using Cloner.Core.Inventory;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cloner.App.ViewModels;

/// <summary>Lo que muestra una tarjeta de origen o destino cuando tiene algo elegido.</summary>
/// <param name="Glyph">Icono (Segoe MDL2 Assets).</param>
public sealed record CardContent(int DiskNumber, string Title, string Details, string Glyph);

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

    /// <summary>Se llama tras leer los discos: quita lo que ya no existe y, en modo sistema, fija el origen.</summary>
    public void Refresh()
    {
        var disks = _disks();
        if (IsSystemMode)
        {
            Source = disks.FirstOrDefault(d => d.ContainsWindows) is { } system ? SystemCard(system) : null;
        }
        else if (Source is { } source && disks.All(d => d.Number != source.DiskNumber))
        {
            Source = null;
        }

        if (Target is { } target && disks.All(d => d.Number != target.DiskNumber))
        {
            Target = null;
        }
    }

    [RelayCommand]
    private void PickSource()
    {
        if (IsSystemMode)
        {
            return;
        }

        var choice = _pick("Elige el disco de origen", Choices(exclude: Target?.DiskNumber));
        if (choice is not null)
        {
            Source = DiskCard(_disks().First(d => d.Number == choice.Number));
        }
    }

    [RelayCommand]
    private void PickTarget()
    {
        var choice = _pick("Elige el disco de destino", Choices(exclude: Source?.DiskNumber));
        if (choice is not null)
        {
            Target = DiskCard(_disks().First(d => d.Number == choice.Number));
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

    private List<DiskChoice> Choices(int? exclude) =>
        _disks().Where(d => d.Number != exclude).Select(DiskChoice.From).ToList();

    private static CardContent DiskCard(DiskInfo disk) => new(
        disk.Number,
        disk.Model,
        $"Disco {disk.Number} · {ByteSize.Format(disk.SizeBytes, Culture)} · {InventoryText.Media(disk.Media)} {InventoryText.Bus(disk.Bus)}",
        DiskGlyph);

    /// <summary>El origen del modo sistema: la versión de Windows y las particiones del disco donde está.</summary>
    private static CardContent SystemCard(DiskInfo disk)
    {
        var parts = disk.Partitions
            .Where(p => p.Kind != PartitionKind.Extended && p.Kind != PartitionKind.MicrosoftReserved)
            .Select(p => p.DriveLetter is { } letter ? $"{letter}:" : p.Kind == PartitionKind.EfiSystem ? "EFI" : InventoryText.Kind(p.Kind));
        return new CardContent(disk.Number, WindowsInfo.Name, $"{string.Join(", ", parts)} · Disco {disk.Number}", WindowsGlyph);
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
