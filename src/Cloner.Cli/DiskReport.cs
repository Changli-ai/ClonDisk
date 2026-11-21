using System.Globalization;
using Cloner.Core;
using Cloner.Core.Cloning;
using Cloner.Core.Inventory;

namespace Cloner.Cli;

/// <summary>Informe en texto de los discos y sus particiones.</summary>
internal static class DiskReport
{
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("es-ES");

    private static readonly string[] Header =
        ["#", "Tipo", "Letra", "Sist. arch.", "Tamaño", "Usado", "Cifrado", "Marcas"];

    public static void Write(TextWriter output, IReadOnlyList<DiskInfo> disks, bool isAdministrator)
    {
        if (disks.Count == 0)
        {
            output.WriteLine("No se encontró ningún disco.");
            return;
        }

        foreach (var disk in disks)
        {
            WriteDisk(output, disk);
            output.WriteLine();
        }

        if (!isAdministrator)
        {
            output.WriteLine("Nota: el estado SMART y el cifrado solo se pueden leer desde una consola de administrador.");
        }
    }

    private static void WriteDisk(TextWriter output, DiskInfo disk)
    {
        var role = DiskProtection.LockLabel(disk, hostDiskNumber: null) is { } locked ? $"  [protegido: {locked}]" : string.Empty;
        output.WriteLine(
            $"Disco {disk.Number}: {disk.Model}  ·  {Size(disk.SizeBytes)}  ·  " +
            $"{InventoryText.Media(disk.Media)} {InventoryText.Bus(disk.Bus)}  ·  {InventoryText.Style(disk.PartitionStyle)}{role}");
        output.WriteLine($"  Serie: {disk.SerialNumber ?? "—"}   Identificador: {disk.Identifier ?? "—"}");
        output.WriteLine(
            $"  Sector: {disk.LogicalSectorSize} B lógico / {disk.PhysicalSectorSize} B físico   " +
            $"Estado: {InventoryText.Health(disk.Health)}{State(disk)}");

        if (disk.Reliability is { } reliability)
        {
            output.WriteLine(
                $"  SMART: temperatura {Value(reliability.TemperatureCelsius, " °C")}, " +
                $"desgaste {Value(reliability.WearPercent, " %")}, " +
                $"horas encendido {Value(reliability.PowerOnHours, string.Empty)}, " +
                $"errores no corregidos {Value(reliability.ReadErrorsUncorrected, string.Empty)} lectura / " +
                $"{Value(reliability.WriteErrorsUncorrected, string.Empty)} escritura");
        }

        foreach (var issue in InventoryText.Issues(DiskHealth.Assess(disk)))
        {
            output.WriteLine($"  ! {issue}");
        }

        if (disk.Partitions.Any(p => p.Encryption is EncryptionState.On or EncryptionState.Suspended))
        {
            output.WriteLine($"  ! {InventoryText.EncryptedPartitionsWarning}");
        }

        if (disk.Partitions.Count == 0)
        {
            output.WriteLine("  Sin particiones.");
        }
        else
        {
            WritePartitions(output, disk.Partitions);
        }

        if (disk.UnallocatedBytes > 0)
        {
            output.WriteLine($"  Sin asignar: {Size(disk.UnallocatedBytes)}");
        }
    }

    private static void WritePartitions(TextWriter output, IReadOnlyList<PartitionInfo> partitions)
    {
        var rows = new List<string[]> { Header };

        foreach (var partition in partitions)
        {
            rows.Add(
            [
                partition.Number.ToString(Culture),
                InventoryText.Kind(partition.Kind),
                partition.DriveLetter is { } letter ? $"{letter}:" : "—",
                partition.Volume?.FileSystem ?? "—",
                Size(partition.SizeBytes),
                partition.Volume is { } volume ? Size(volume.UsedBytes) : "—",
                InventoryText.Encryption(partition.Encryption),
                InventoryText.Flags(partition),
            ]);
        }

        var widths = new int[rows[0].Length];
        foreach (var row in rows)
        {
            for (var i = 0; i < row.Length; i++)
            {
                widths[i] = Math.Max(widths[i], row[i].Length);
            }
        }

        foreach (var row in rows)
        {
            var cells = row.Select((cell, i) => cell.PadRight(widths[i]));
            output.WriteLine($"  {string.Join("  ", cells).TrimEnd()}");
        }
    }

    private static string State(DiskInfo disk)
    {
        var states = new List<string>();
        if (disk.IsOffline) states.Add("sin conexión");
        if (disk.IsReadOnly) states.Add("solo lectura");
        return states.Count == 0 ? string.Empty : $" ({string.Join(", ", states)})";
    }

    private static string Size(long bytes) => ByteSize.Format(bytes, Culture);

    private static string Value<T>(T? value, string unit)
        where T : struct, IFormattable =>
        value is { } v ? v.ToString(null, Culture) + unit : "—";
}
