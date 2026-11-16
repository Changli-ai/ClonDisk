using System.Globalization;
using Cloner.Core.Inventory;
using Cloner.Core.PartitionTables;

namespace Cloner.Core.Layout;

/// <summary>Tipo de una partición en la tabla convertida.</summary>
/// <param name="GptAttributes">Atributos GPT (bit 0: necesaria para la plataforma; bit 63: sin letra).</param>
internal sealed record ConvertedType(Guid GptType, ulong GptAttributes, string GptName, byte MbrType);

/// <summary>Partición que no existe en el origen y se crea en el destino (la EFI al pasar a UEFI).</summary>
public sealed record CreatedPartition(PartitionKind Kind, long Offset, long Size)
{
    public long End => Offset + Size;
}

/// <summary>
/// Conversión de la tabla del destino entre MBR y GPT (RF-17): qué tipo recibe cada partición,
/// qué se descarta (la reservada de Microsoft al pasar a MBR) y qué se crea (la partición EFI
/// al pasar un Windows de BIOS a UEFI). No toca discos.
/// </summary>
internal static class StyleConversion
{
    /// <summary>Partición EFI que se crea al convertir a GPT un disco de Windows que arrancaba en BIOS.</summary>
    public const long EfiBytes = 100 * LayoutPlanner.MiB;

    /// <summary>Partición reservada de Microsoft que acompaña a la EFI.</summary>
    public const long MsrBytes = 16 * LayoutPlanner.MiB;

    /// <summary>Entradas de la matriz GPT que se genera: 128 de 128 bytes, como Windows.</summary>
    public const int GptEntryCount = 128;
    public const int GptEntrySize = 128;

    private const ulong RequiredAttribute = 0x1;
    private const ulong NoDriveLetterAttribute = 0x8000000000000000;

    public static readonly Guid LinuxFileSystem = new("0fc63daf-8483-4772-8e79-3d69d8477de4");
    public static readonly Guid LinuxSwap = new("0657fd6d-a4ab-43c4-84e5-0933c84b4f4f");

    /// <summary>Se pide una tabla de otro estilo que la del origen.</summary>
    public static bool Requested(PartitionTableInfo table, PartitionStyle? targetStyle) =>
        targetStyle is { } style && style != PartitionStyle.Raw && table.Style != PartitionStyle.Raw && style != table.Style;

    /// <summary>Particiones del origen que pasan al destino: al convertir a MBR se descarta la reservada de Microsoft, que no guarda datos.</summary>
    public static bool Keeps(PartitionInfo partition, PartitionStyle targetStyle) =>
        partition.Kind != PartitionKind.Extended
        && !(targetStyle == PartitionStyle.Mbr && partition.Kind == PartitionKind.MicrosoftReserved);

    /// <summary>
    /// Comprueba que la conversión es posible y dice si hay que crear una partición EFI. Los
    /// motivos que la impiden van a <paramref name="problems"/>.
    /// </summary>
    public static bool Check(DiskInfo source, PartitionStyle targetStyle, List<string> problems, List<string> warnings)
    {
        ArgumentNullException.ThrowIfNull(source);
        var styleName = Name(targetStyle);
        if (source.Partitions.Any(p => p.Kind == PartitionKind.Extended))
        {
            problems.Add($"El origen tiene particiones lógicas (partición extendida MBR): no se puede convertir a {styleName}.");
            return false;
        }

        var kept = source.Partitions.Where(p => Keeps(p, targetStyle)).ToList();
        foreach (var partition in kept.Where(p => TypeFor(p) is null))
        {
            problems.Add($"La partición {partition.Number} es de un tipo que no se sabe convertir a {styleName} ({partition.TypeId ?? "desconocido"}).");
        }

        var hasWindows = source.ContainsWindows || source.Partitions.Any(p => p.ContainsWindows);
        var createEfi = false;
        if (targetStyle == PartitionStyle.Mbr)
        {
            if (kept.Count > 4)
            {
                problems.Add($"Una tabla MBR admite como mucho 4 particiones y el origen tiene {kept.Count.ToString(CultureInfo.InvariantCulture)} (sin contar la reservada de Microsoft).");
            }

            if (source.Partitions.Any(p => p.Kind == PartitionKind.MicrosoftReserved))
            {
                warnings.Add("La partición reservada de Microsoft (MSR) no se copia: en MBR no se usa.");
            }

            warnings.Add(hasWindows
                ? "La tabla del clon será MBR: Windows arrancará en modo BIOS (heredado o CSM). El equipo de destino debe tenerlo activado y no habrá arranque seguro."
                : "La tabla del clon será MBR; el contenido de las particiones no cambia.");
        }
        else
        {
            createEfi = hasWindows && !source.Partitions.Any(p => p.Kind == PartitionKind.EfiSystem);
            warnings.Add(hasWindows
                ? "La tabla del clon será GPT: Windows arrancará en modo UEFI. El equipo de destino debe arrancar en UEFI (sin CSM ni arranque heredado) y Windows debe ser de 64 bits."
                : "La tabla del clon será GPT; el contenido de las particiones no cambia.");
            if (createEfi)
            {
                warnings.Add("Se crean una partición EFI de 100 MB y una reservada de Microsoft de 16 MB al principio del clon, y su arranque se rehace con bcdboot.");
            }
        }

        if (hasWindows)
        {
            warnings.Add("El entorno de recuperación de Windows (WinRE) puede quedar desactivado en el clon; se vuelve a activar con «reagentc /enable».");
        }

        return createEfi;
    }

    /// <summary>Tipo en la tabla convertida, o nulo si no se sabe convertir.</summary>
    public static ConvertedType? TypeFor(PartitionInfo partition)
    {
        ArgumentNullException.ThrowIfNull(partition);
        switch (partition.Kind)
        {
            case PartitionKind.EfiSystem:
                return new ConvertedType(PartitionTypes.EfiSystem, 0, "Sistema EFI", 0xEF);
            case PartitionKind.MicrosoftReserved:
                return new ConvertedType(PartitionTypes.MicrosoftReserved, 0, "Reservada de Microsoft", 0);
            case PartitionKind.Recovery:
                return new ConvertedType(PartitionTypes.Recovery, RequiredAttribute | NoDriveLetterAttribute, "Recuperación", 0x27);
            case PartitionKind.BasicData:
                return new ConvertedType(PartitionTypes.BasicData, 0, "Datos", MbrDataType(partition.Volume?.FileSystem));
            case PartitionKind.Other:
                if (Guid.TryParse(partition.TypeId, out var gpt))
                {
                    if (gpt == LinuxFileSystem)
                    {
                        return new ConvertedType(LinuxFileSystem, 0, "Linux", 0x83);
                    }

                    if (gpt == LinuxSwap)
                    {
                        return new ConvertedType(LinuxSwap, 0, "Linux swap", 0x82);
                    }
                }

                return partition.TypeId?.ToUpperInvariant() switch
                {
                    "0X83" => new ConvertedType(LinuxFileSystem, 0, "Linux", 0x83),
                    "0X82" => new ConvertedType(LinuxSwap, 0, "Linux swap", 0x82),
                    _ => null,
                };
            default:
                return null;
        }
    }

    public static ConvertedType TypeFor(CreatedPartition partition) => partition.Kind == PartitionKind.EfiSystem
        ? new ConvertedType(PartitionTypes.EfiSystem, 0, "Sistema EFI", 0xEF)
        : new ConvertedType(PartitionTypes.MicrosoftReserved, 0, "Reservada de Microsoft", 0);

    /// <summary>Byte de tipo MBR de una partición de datos según su sistema de archivos.</summary>
    public static byte MbrDataType(string? fileSystem) => fileSystem?.ToUpperInvariant() switch
    {
        "FAT32" => 0x0C,
        "FAT" or "FAT16" => 0x06,
        _ => 0x07,
    };

    /// <summary>Sectores que ocupa la matriz de entradas GPT generada.</summary>
    public static int GptEntriesSectors(int sectorSize) => GptEntryCount * GptEntrySize / sectorSize;

    public static string Name(PartitionStyle style) => style == PartitionStyle.Gpt ? "GPT" : "MBR";
}
