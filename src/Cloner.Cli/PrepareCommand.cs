using System.Globalization;
using Cloner.Core;
using Cloner.Core.Cloning;
using Cloner.Core.Inventory;

namespace Cloner.Cli;

/// <summary>Comando <c>prepare</c>: limpia un disco y crea en él una sola partición NTFS con letra.</summary>
internal static class PrepareCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        var virtualOnly = args.Contains("--virtual-only", StringComparer.OrdinalIgnoreCase);
        var positional = args.Skip(1).Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();
        if (positional.Count != 1 || !int.TryParse(positional[0], NumberStyles.None, CultureInfo.InvariantCulture, out var number))
        {
            Console.Error.WriteLine("Uso: clondisk-cli prepare <disco> [--virtual-only]   (el número que muestra «list»)");
            return CloneCommand.Usage;
        }

        if (!Elevation.IsAdministrator())
        {
            Console.Error.WriteLine("Para limpiar un disco hay que ejecutar la consola como administrador.");
            return CloneCommand.Blocked;
        }

        var inventory = new DiskInventory();
        var disks = inventory.GetDisks();
        var disk = disks.FirstOrDefault(d => d.Number == number);
        if (disk is null)
        {
            Console.Error.WriteLine($"No existe el disco {number}. Usa «list» para ver los discos.");
            return CloneCommand.Usage;
        }

        if (DiskPreparationService.CannotPrepare(disk, DiskProtection.HostDiskNumber(disks)) is { } reason)
        {
            Console.Error.WriteLine($"No se puede limpiar el disco {number}: {reason}");
            return CloneCommand.Blocked;
        }

        var culture = CultureInfo.GetCultureInfo("es-ES");
        Console.WriteLine($"Se borrará TODO el contenido del disco {number}: {disk.Model} · {ByteSize.Format(disk.SizeBytes, culture)}.");
        foreach (var partition in disk.Partitions)
        {
            var name = partition.DriveLetter is { } letter ? $"{letter}:" : InventoryText.Kind(partition.Kind);
            Console.WriteLine($"  · Partición {partition.Number}: {name} · {ByteSize.Format(partition.SizeBytes, culture)}");
        }

        Console.WriteLine($"Después tendrá una sola partición NTFS «{DiskPreparationService.VolumeLabel}» con letra de unidad.");
        Console.Write($"Para continuar, escribe {CloneText.ConfirmationWord} y pulsa Intro: ");
        if (!string.Equals(Console.ReadLine()?.Trim(), CloneText.ConfirmationWord, StringComparison.Ordinal))
        {
            Console.WriteLine("Cancelado. No se ha cambiado nada.");
            return CloneCommand.Cancelled;
        }

        var progress = new Progress<string>(text => Console.WriteLine($"  {text}…"));
        var result = await new DiskPreparationService(inventory, virtualOnly: virtualOnly).PrepareAsync(DiskIdentity.From(disk), progress);
        Console.WriteLine();
        Console.WriteLine(result.Succeeded ? $"Completado: {result.Message}" : $"Ha fallado: {result.Message}");
        if (result.LogPath is not null)
        {
            Console.WriteLine($"Registro: {result.LogPath}");
        }

        return result.Succeeded ? CloneCommand.Succeeded : CloneCommand.Failed;
    }
}
