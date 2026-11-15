using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using Cloner.Core;
using Cloner.Core.Cloning;
using Cloner.Core.Inventory;
using Cloner.Core.Layout;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cloner.App.ViewModels;

/// <summary>Fases del asistente de clonado.</summary>
public enum CloneStage
{
    Select,
    Review,
    Running,
    Finished,
}

/// <summary>Tono con el que se presenta el resultado.</summary>
public enum ResultTone
{
    Success,
    Neutral,
    Error,
}

/// <summary>Un disco en las listas de origen y destino.</summary>
public sealed record DiskChoice(int Number, string Title, string Details)
{
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("es-ES");

    public static DiskChoice From(DiskInfo disk)
    {
        ArgumentNullException.ThrowIfNull(disk);
        var partitions = disk.Partitions.Count(p => p.Kind != PartitionKind.Extended);
        var details = $"{InventoryText.Media(disk.Media)} {InventoryText.Bus(disk.Bus)} · {InventoryText.Style(disk.PartitionStyle)} · "
            + (partitions == 1 ? "1 partición" : $"{partitions} particiones")
            + (disk.IsSystemDisk ? " · Disco del sistema" : string.Empty);
        return new DiskChoice(disk.Number, $"Disco {disk.Number} · {disk.Model} · {ByteSize.Format(disk.SizeBytes, Culture)}", details);
    }
}

/// <summary>Diálogos que el asistente pide a la ventana.</summary>
public interface ICloneDialogs
{
    /// <summary>Pregunta si cancelar el clonado en curso.</summary>
    bool ConfirmCancel();

    /// <summary>Pide dónde guardar una copia del registro; nulo si el usuario desiste.</summary>
    string? ChooseLogDestination(string suggestedName);

    void ShowError(string message);
}

/// <summary>Asistente de clonado: elegir discos, revisar el plan, copiar y ver el resultado.</summary>
public sealed partial class CloneViewModel : ObservableObject
{
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("es-ES");

    private readonly ICloneService _service;
    private readonly ICloneDialogs _dialogs;
    private CancellationTokenSource? _cancellation;

    public CloneViewModel(ICloneService service, IReadOnlyList<DiskInfo> disks, int? preferredSource, ICloneDialogs dialogs, int? preferredTarget = null)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(disks);
        ArgumentNullException.ThrowIfNull(dialogs);
        _service = service;
        _dialogs = dialogs;

        Disks = disks.Select(DiskChoice.From).ToList();
        Source = Disks.FirstOrDefault(d => d.Number == preferredSource)
            ?? Disks.FirstOrDefault(d => !disks.First(x => x.Number == d.Number).IsSystemDisk);
        Target = Disks.FirstOrDefault(d => d.Number == preferredTarget && d != Source)
            ?? Disks.FirstOrDefault(d => d != Source && !disks.First(x => x.Number == d.Number).IsSystemDisk);
        Verify = true;
        UseSmartCopy = true;
        Sizing = SizeMode.ExtendMain;
        Stage = CloneStage.Select;
    }

    public IReadOnlyList<DiskChoice> Disks { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReviewCommand))]
    public partial DiskChoice? Source { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReviewCommand))]
    public partial DiskChoice? Target { get; set; }

    [ObservableProperty]
    public partial bool Verify { get; set; }

    /// <summary>Copia inteligente (RF-12); si es falso, sector a sector.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UseSectorCopy))]
    public partial bool UseSmartCopy { get; set; }

    public bool UseSectorCopy
    {
        get => !UseSmartCopy;
        set => UseSmartCopy = !value;
    }

    /// <summary>Uso del espacio sobrante si el destino es mayor (RF-13).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KeepSizes), nameof(ExtendMain), nameof(SpreadSpace))]
    public partial SizeMode Sizing { get; set; }

    public bool KeepSizes
    {
        get => Sizing == SizeMode.Keep;
        set
        {
            if (value)
            {
                Sizing = SizeMode.Keep;
            }
        }
    }

    public bool ExtendMain
    {
        get => Sizing == SizeMode.ExtendMain;
        set
        {
            if (value)
            {
                Sizing = SizeMode.ExtendMain;
            }
        }
    }

    public bool SpreadSpace
    {
        get => Sizing == SizeMode.Proportional;
        set
        {
            if (value)
            {
                Sizing = SizeMode.Proportional;
            }
        }
    }

    /// <summary>El clon recibe identificadores nuevos (RF-18).</summary>
    [ObservableProperty]
    public partial bool NewIdentifiers { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSelecting), nameof(IsReviewing), nameof(IsRunning), nameof(IsFinished))]
    public partial CloneStage Stage { get; set; }

    public bool IsSelecting => Stage == CloneStage.Select;
    public bool IsReviewing => Stage == CloneStage.Review;
    public bool IsRunning => Stage == CloneStage.Running;
    public bool IsFinished => Stage == CloneStage.Finished;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReviewCommand))]
    public partial bool IsPlanning { get; set; }

    /// <summary>Motivos por los que no se puede clonar con la selección actual.</summary>
    public ObservableCollection<string> Problems { get; } = [];

    // --- Revisión (RS-03) ---

    public ClonePlan? Plan { get; private set; }
    public string SourceSummary { get; private set; } = string.Empty;
    public string TargetSummary { get; private set; } = string.Empty;
    public string EraseTitle { get; private set; } = string.Empty;
    public IReadOnlyList<string> ErasedItems { get; private set; } = [];
    public string CopySummary { get; private set; } = string.Empty;
    public IReadOnlyList<string> Warnings { get; private set; } = [];
    public bool HasWarnings => Warnings.Count > 0;

    /// <summary>Cómo quedan las particiones en el destino, si cambian.</summary>
    public IReadOnlyList<string> LayoutLines { get; private set; } = [];
    public bool HasLayout => LayoutLines.Count > 0;
    public string ConfirmText { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    public partial bool ConfirmErase { get; set; }

    // --- Progreso (RF-30) ---

    public string RunTitle { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string PhaseText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial double Percent { get; set; }

    [ObservableProperty]
    public partial string PercentText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SpeedText { get; set; } = "—";

    [ObservableProperty]
    public partial string RemainingText { get; set; } = "—";

    [ObservableProperty]
    public partial string CurrentItem { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string BytesText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    public partial bool IsCancelling { get; set; }

    // --- Resultado ---

    [ObservableProperty]
    public partial ResultTone Tone { get; set; }

    [ObservableProperty]
    public partial string ResultTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ResultMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial IReadOnlyList<PropertyRow> ResultDetails { get; set; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportLogCommand))]
    public partial string? LogPath { get; set; }

    private bool CanReview => Source is not null && Target is not null && !IsPlanning;

    [RelayCommand(CanExecute = nameof(CanReview))]
    private async Task ReviewAsync()
    {
        Problems.Clear();
        IsPlanning = true;
        try
        {
            var source = Source!.Number;
            var target = Target!.Number;
            var options = new CloneOptions
            {
                Verify = Verify,
                Mode = UseSmartCopy ? CloneMode.UsedBlocks : CloneMode.SectorBySector,
                Sizing = Sizing,
                NewIdentifiers = NewIdentifiers,
            };
            var result = await Task.Run(() => _service.CreatePlan(source, target, options));
            if (result.Plan is not { } plan)
            {
                foreach (var problem in result.Problems)
                {
                    Problems.Add(problem);
                }

                return;
            }

            ShowPlan(plan);
            Stage = CloneStage.Review;
        }
        finally
        {
            IsPlanning = false;
        }
    }

    [RelayCommand]
    private void Back()
    {
        ConfirmErase = false;
        Stage = CloneStage.Select;
    }

    private bool CanStart => ConfirmErase && Plan is not null;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        var plan = Plan!;
        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        IsCancelling = false;
        RunTitle = $"Clonando el disco {plan.Source.Number} en el disco {plan.Target.Number}";
        OnPropertyChanged(nameof(RunTitle));
        ApplyProgress(new CloneProgress { Phase = ClonePhase.Preparing, BytesTotal = 1, CurrentItem = "Comprobando los discos" });
        Stage = CloneStage.Running;

        try
        {
            var progress = new Progress<CloneProgress>(ApplyProgress);
            var result = await _service.RunAsync(plan, progress, cancellation.Token);
            ShowResult(result);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DiskInventoryException)
        {
            // El servicio informa de los fallos en el resultado; esto cubre lo que ocurra antes, como crear el registro.
            ShowResult(new CloneResult { Outcome = CloneOutcome.Failed, Message = $"No se pudo iniciar el clonado: {ex.Message}" });
        }
        finally
        {
            _cancellation = null;
            Stage = CloneStage.Finished;
        }
    }

    private bool CanCancel => !IsCancelling;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        if (_cancellation is null || !_dialogs.ConfirmCancel())
        {
            return;
        }

        IsCancelling = true;
        PhaseText = "Cancelando…";
        _cancellation.Cancel();
    }

    private bool CanExportLog => LogPath is not null;

    [RelayCommand(CanExecute = nameof(CanExportLog))]
    private void ExportLog()
    {
        var destination = _dialogs.ChooseLogDestination(Path.GetFileName(LogPath!));
        if (destination is null)
        {
            return;
        }

        try
        {
            File.Copy(LogPath!, destination, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _dialogs.ShowError($"No se pudo guardar el registro: {ex.Message}");
        }
    }

    private void ShowPlan(ClonePlan plan)
    {
        Plan = plan;
        SourceSummary = plan.Source.Describe(Culture);
        TargetSummary = plan.Target.Describe(Culture);
        EraseTitle = $"Se borrará todo el contenido del disco {plan.Target.Number}";
        ErasedItems = plan.TargetPartitions.Count > 0
            ? plan.TargetPartitions.Select(CloneText.Partition).ToList()
            : ["El disco no tiene particiones, pero todo lo que contenga se sobrescribirá."];
        CopySummary = $"{CloneText.CopyAmount(plan)}. "
            + (plan.Options.Verify ? "Al terminar se comprobará que el destino coincide." : "No se verificará al terminar.");
        Warnings = plan.Warnings;
        LayoutLines = CloneText.Layout(plan);
        ConfirmText = $"Entiendo que se borrará todo el contenido del disco {plan.Target.Number}.";
        ConfirmErase = false;

        foreach (var name in new[]
        {
            nameof(Plan), nameof(SourceSummary), nameof(TargetSummary), nameof(EraseTitle), nameof(ErasedItems),
            nameof(CopySummary), nameof(Warnings), nameof(HasWarnings), nameof(ConfirmText),
            nameof(LayoutLines), nameof(HasLayout),
        })
        {
            OnPropertyChanged(name);
        }

        StartCommand.NotifyCanExecuteChanged();
    }

    private void ApplyProgress(CloneProgress progress)
    {
        if (!IsCancelling)
        {
            PhaseText = CloneText.Phase(progress.Phase);
        }

        Percent = progress.Fraction * 100;
        PercentText = CloneText.Percent(progress.Fraction);
        SpeedText = CloneText.Speed(progress.BytesPerSecond);
        RemainingText = CloneText.Duration(progress.Remaining);
        CurrentItem = progress.CurrentItem ?? string.Empty;
        BytesText = progress.BytesTotal > 1
            ? $"{ByteSize.Format(progress.BytesDone, Culture)} de {ByteSize.Format(progress.BytesTotal, Culture)}"
            : "—";
    }

    private void ShowResult(CloneResult result)
    {
        Tone = result.Outcome switch
        {
            CloneOutcome.Succeeded => ResultTone.Success,
            CloneOutcome.Cancelled => ResultTone.Neutral,
            _ => ResultTone.Error,
        };
        ResultTitle = result.Outcome == CloneOutcome.Succeeded
            ? result.Verified ? "Clonado completado y verificado" : "Clonado completado"
            : CloneText.Outcome(result.Outcome);
        ResultMessage = result.Message;

        var details = new List<PropertyRow>
        {
            new("Duración", CloneText.Duration(result.Elapsed)),
            new("Datos copiados", ByteSize.Format(result.BytesWritten, Culture)),
        };
        if (result.DataHash is not null)
        {
            details.Add(new PropertyRow("Huella de lo copiado", result.DataHash));
        }

        if (result.LogPath is not null)
        {
            details.Add(new PropertyRow("Registro", result.LogPath));
        }

        ResultDetails = details;
        LogPath = result.LogPath;
    }
}
