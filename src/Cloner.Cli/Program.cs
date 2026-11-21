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

    case "repair-boot":
        return await RepairBootCommand.RunAsync(args);

    case "prepare":
        return await PrepareCommand.RunAsync(args);

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
    output.WriteLine("      --extend                Si el destino es mayor, amplía la partición principal.");
    output.WriteLine("      --proportional          Si el destino es mayor, reparte el espacio entre las de datos.");
    output.WriteLine("      --align / --no-align    Alinea las particiones a 1 MiB siempre / nunca (por defecto,");
    output.WriteLine("                              solo si el destino es un SSD).");
    output.WriteLine("      --new-ids               Da identificadores nuevos al clon para usarlo junto al original.");
    output.WriteLine("      --gpt / --mbr           Convierte la tabla del clon a GPT (arranque UEFI) o a MBR (BIOS).");
    output.WriteLine("                              Si el destino es más pequeño, las particiones NTFS de datos se");
    output.WriteLine("                              reducen y se copian archivo a archivo, si sus datos caben.");
    output.WriteLine("      --no-verify             No relee el destino para compararlo al terminar.");
    output.WriteLine("      --virtual-only          Solo admite destinos virtuales (VHD/VHDX), para pruebas.");
    output.WriteLine("  repair-boot <disco>         Rehace el arranque de un clon de Windows que no arranca (por");
    output.WriteLine("                              ejemplo, 0xc000000e): lo pone en línea, corrige la letra C:");
    output.WriteLine("                              en su registro y ejecuta bcdboot. No borra datos.");
    output.WriteLine("  prepare <disco>             Limpia y formatea un disco: borra todas sus particiones (también");
    output.WriteLine("                              las de arranque que deja un formateo), lo inicializa en GPT y crea");
    output.WriteLine("                              una sola partición NTFS con letra. Como «clean» de diskpart.");
    output.WriteLine("      --virtual-only          Solo admite discos virtuales (VHD/VHDX), para pruebas.");
    output.WriteLine("  version                     Muestra la versión.");
    output.WriteLine("  help                        Muestra esta ayuda.");
    output.WriteLine();
    output.WriteLine("Los discos se indican por el número que muestra «list». Clonar requiere administrador.");
}
