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

    case "plan":
        return await CloneCommand.RunAsync(args, execute: false);

    case "clone":
        return await CloneCommand.RunAsync(args, execute: true);

    case "version" or "--version":
        Console.WriteLine($"ClonDisk CLI {version}");
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
    output.WriteLine($"ClonDisk CLI {version}");
    output.WriteLine();
    output.WriteLine("Uso: clondisk-cli <comando>");
    output.WriteLine();
    output.WriteLine("  list                        Muestra los discos del equipo y sus particiones (solo lectura).");
    output.WriteLine("  plan <origen> <destino>     Muestra qué haría un clonado, sin escribir nada.");
    output.WriteLine("  clone <origen> <destino>    Clona el disco de origen sobre el de destino. Admite el disco");
    output.WriteLine("                              del sistema en uso: se copia desde una instantánea.");
    output.WriteLine("                              Borra todo el destino; pide confirmación antes de escribir.");
    output.WriteLine("      --sector                Copia sector a sector, también el espacio libre.");
    output.WriteLine("                              Sin esta opción solo se copian los bloques en uso.");
    output.WriteLine("      --no-verify             No relee el destino para compararlo al terminar.");
    output.WriteLine("      --virtual-only          Solo admite destinos virtuales (VHD/VHDX), para pruebas.");
    output.WriteLine("  version                     Muestra la versión.");
    output.WriteLine("  help                        Muestra esta ayuda.");
    output.WriteLine();
    output.WriteLine("Los discos se indican por el número que muestra «list». Clonar requiere administrador.");
}
