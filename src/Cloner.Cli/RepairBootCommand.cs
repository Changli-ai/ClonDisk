using System.Globalization;
using Cloner.Core;
using Cloner.Core.Cloning;
using Cloner.Core.Inventory;

namespace Cloner.Cli;

/// <summary>Comando <c>repair-boot</c>: rehace el arranque de un clon de Windows.</summary>
internal static class RepairBootCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 2 || !int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out var number))
        {
            Console.Error.WriteLine("Uso: clondisk-cli repair-boot <disco>   (el número que muestra «list»)");
            return CloneCommand.Usage;
        }

        if (!Elevation.IsAdministrator())
        {
            Console.Error.WriteLine("Para reparar el arranque hay que ejecutar la consola como administrador.");
            return CloneCommand.Blocked;
        }

        var inventory = new DiskInventory();
        var disk = inventory.GetDisks().FirstOrDefault(d => d.Number == number);
        if (disk is null)
        {
            Console.Error.WriteLine($"No existe el disco {number}. Usa «list» para ver los discos.");
            return CloneCommand.Usage;
        }

        if (BootRepairService.CannotRepair(disk) is { } reason)
        {
            Console.Error.WriteLine($"No se puede reparar el arranque del disco {number}: {reason}");
            return CloneCommand.Blocked;
        }

        Console.WriteLine($"Se va a rehacer el arranque del disco {number}: {disk.Model} · {ByteSize.Format(disk.SizeBytes, CultureInfo.GetCultureInfo("es-ES"))}.");
        Console.WriteLine("Se pondrá en línea si está sin conexión, se corregirá la letra C: en su registro y se");
        Console.WriteLine("volverán a crear sus archivos de arranque con bcdboot. No se borran datos.");
        Console.Write($"Para continuar, escribe {CloneText.RepairConfirmationWord} y pulsa Intro: ");
        if (!string.Equals(Console.ReadLine()?.Trim(), CloneText.RepairConfirmationWord, StringComparison.Ordinal))
        {
            Console.WriteLine("Cancelado. No se ha cambiado nada.");
            return CloneCommand.Cancelled;
        }

        var progress = new Progress<string>(text => Console.WriteLine($"  {text}…"));
        var result = await new BootRepairService(inventory).RepairAsync(number, progress);
        Console.WriteLine();
        Console.WriteLine(result.Succeeded ? $"Completado: {result.Message}" : $"Ha fallado: {result.Message}");
        if (result.LogPath is not null)
        {
            Console.WriteLine($"Registro: {result.LogPath}");
        }

        return result.Succeeded ? CloneCommand.Succeeded : CloneCommand.Failed;
    }
}
