using System.Globalization;
using Cloner.Core;
using Cloner.Core.Cloning;
using Cloner.Core.Inventory;

namespace Cloner.Cli;

/// <summary>Comandos <c>plan</c> y <c>clone</c>.</summary>
internal static class CloneCommand
{
    public const int Succeeded = 0;
    public const int Usage = 1;
    public const int Blocked = 3;
    public const int Cancelled = 4;
    public const int Failed = 5;
    public const int VerificationFailed = 6;

    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("es-ES");

    /// <param name="execute">Falso para <c>plan</c>: solo muestra el plan.</param>
    public static async Task<int> RunAsync(string[] args, bool execute)
    {
        if (!TryParse(args, out var source, out var target, out var options, out var error))
        {
            Console.Error.WriteLine(error);
            Console.Error.WriteLine("Uso: cloner " + (execute ? "clone" : "plan") + " <disco origen> <disco destino> [--no-verify] [--virtual-only]");
            return Usage;
        }

        var service = new CloneService(new DiskInventory());
        var result = service.CreatePlan(source, target, options);
        if (result.Plan is not { } plan)
        {
            Console.Error.WriteLine("No se puede clonar:");
            foreach (var problem in result.Problems)
            {
                Console.Error.WriteLine($"  - {problem}");
            }

            return Blocked;
        }

        WritePlan(Console.Out, plan);
        if (!execute)
        {
            Console.WriteLine();
            Console.WriteLine("No se ha escrito nada. Para clonar, usa el comando «clone» con los mismos discos.");
            return Succeeded;
        }

        // RS-03: confirmación explícita antes de escribir.
        Console.WriteLine();
        Console.Write($"Para borrar el disco {plan.Target.Number} y clonar, escribe {CloneText.ConfirmationWord} y pulsa Intro: ");
        if (!string.Equals(Console.ReadLine()?.Trim(), CloneText.ConfirmationWord, StringComparison.Ordinal))
        {
            Console.WriteLine("Cancelado. No se ha escrito nada.");
            return Cancelled;
        }

        return await ExecuteAsync(service, plan);
    }

    public static void WritePlan(TextWriter output, ClonePlan plan)
    {
        output.WriteLine("Plan de clonado sector a sector");
        output.WriteLine();
        output.WriteLine($"  Origen:  {plan.Source.Describe(Culture)}");
        output.WriteLine($"  Destino: {plan.Target.Describe(Culture)}");
        output.WriteLine();

        output.WriteLine($"  Se borrará TODO el contenido del disco {plan.Target.Number}:");
        if (plan.TargetPartitions.Count == 0)
        {
            output.WriteLine("    (no tiene particiones)");
        }

        foreach (var partition in plan.TargetPartitions)
        {
            output.WriteLine($"    - {CloneText.Partition(partition)}");
        }

        output.WriteLine();
        output.WriteLine($"  Se copiarán {ByteSize.Format(plan.BytesToCopy, Culture)}. Verificación al terminar: {(plan.Options.Verify ? "sí" : "no")}.");

        if (plan.Warnings.Count > 0)
        {
            output.WriteLine();
            output.WriteLine("  Avisos:");
            foreach (var warning in plan.Warnings)
            {
                output.WriteLine($"    - {warning}");
            }
        }

        output.WriteLine();
        output.WriteLine("  Pasos:");
        for (var i = 0; i < plan.Steps.Count; i++)
        {
            var step = plan.Steps[i];
            output.WriteLine($"    {i + 1}. {step.Description} ({ByteSize.Format(step.Length, Culture)})");
        }
    }

    private static async Task<int> ExecuteAsync(CloneService service, ClonePlan plan)
    {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            e.Cancel = true;
            if (!cancellation.IsCancellationRequested)
            {
                Console.WriteLine();
                Console.WriteLine("Cancelando…");
                cancellation.Cancel();
            }
        };

        Console.CancelKeyPress += onCancel;
        try
        {
            Console.WriteLine();
            Console.WriteLine("Clonando. Pulsa Ctrl+C para cancelar.");
            var progress = new ConsoleProgress();
            var result = await service.RunAsync(plan, progress, cancellation.Token);
            progress.Complete();

            Console.WriteLine();
            Console.WriteLine($"{CloneText.Outcome(result.Outcome)}: {result.Message}");
            Console.WriteLine($"Duración: {CloneText.Duration(result.Elapsed)}.");
            if (result.LogPath is not null)
            {
                Console.WriteLine($"Registro: {result.LogPath}");
            }

            return result.Outcome switch
            {
                CloneOutcome.Succeeded => Succeeded,
                CloneOutcome.Cancelled => Cancelled,
                CloneOutcome.VerificationFailed => VerificationFailed,
                _ => Failed,
            };
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }
    }

    private static bool TryParse(string[] args, out int source, out int target, out CloneOptions options, out string? error)
    {
        source = target = -1;
        options = new CloneOptions();
        error = null;

        var numbers = new List<int>();
        foreach (var arg in args.Skip(1))
        {
            switch (arg.ToLowerInvariant())
            {
                case "--no-verify":
                    options = options with { Verify = false };
                    break;
                case "--virtual-only":
                    options = options with { VirtualTargetOnly = true };
                    break;
                default:
                    if (!int.TryParse(arg, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
                    {
                        error = $"Argumento no válido: {arg}";
                        return false;
                    }

                    numbers.Add(number);
                    break;
            }
        }

        if (numbers.Count != 2)
        {
            error = "Indica el número del disco de origen y el del destino (los que muestra «list»).";
            return false;
        }

        (source, target) = (numbers[0], numbers[1]);
        return true;
    }

    /// <summary>Progreso en una sola línea que se reescribe.</summary>
    private sealed class ConsoleProgress : IProgress<CloneProgress>
    {
        private readonly Lock _lock = new();
        private int _lastLength;

        public void Report(CloneProgress value)
        {
            var line = $"  {CloneText.Phase(value.Phase),-11} {CloneText.Percent(value.Fraction),7} · {CloneText.Speed(value.BytesPerSecond)} · quedan {CloneText.Duration(value.Remaining)} · {value.CurrentItem}";
            var width = Console.IsOutputRedirected ? int.MaxValue : Math.Max(20, Console.WindowWidth - 1);
            if (line.Length > width)
            {
                line = line[..width];
            }

            lock (_lock)
            {
                Console.Write("\r" + line.PadRight(_lastLength));
                _lastLength = line.Length;
            }
        }

        public void Complete()
        {
            lock (_lock)
            {
                if (_lastLength > 0)
                {
                    Console.WriteLine();
                    _lastLength = 0;
                }
            }
        }
    }
}
