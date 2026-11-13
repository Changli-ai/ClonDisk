using System.Reflection;
using System.Text;
using Cloner.Cli;
using Cloner.Core;
using Cloner.Core.Inventory;

Console.OutputEncoding = Encoding.UTF8;

var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
var command = args.Length > 0 ? args[0].ToLowerInvariant() : "help";

switch (command)
{
    case "list":
        try
        {
            DiskReport.Write(Console.Out, new DiskInventory().GetDisks(), Elevation.IsAdministrator());
            return 0;
        }
        catch (DiskInventoryException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }

    case "version" or "--version":
        Console.WriteLine($"Cloner CLI {version}");
        return 0;

    case "help" or "--help" or "-h":
        WriteHelp(Console.Out);
        return 0;

    default:
        Console.Error.WriteLine($"Comando desconocido: {args[0]}");
        WriteHelp(Console.Error);
        return 1;
}

void WriteHelp(TextWriter output)
{
    output.WriteLine($"Cloner CLI {version}");
    output.WriteLine();
    output.WriteLine("Uso: cloner <comando>");
    output.WriteLine();
    output.WriteLine("  list      Muestra los discos del equipo y sus particiones (solo lectura).");
    output.WriteLine("  version   Muestra la versión.");
    output.WriteLine("  help      Muestra esta ayuda.");
}
