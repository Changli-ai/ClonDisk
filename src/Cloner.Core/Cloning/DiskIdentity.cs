using Cloner.Core.Devices;
using Cloner.Core.Inventory;

namespace Cloner.Core.Cloning;

/// <summary>
/// Lo que identifica a un disco más allá de su número, que puede cambiar al conectar o
/// desconectar dispositivos (RS-04). Se compara justo antes de escribir.
/// </summary>
public sealed record DiskIdentity
{
    public required int Number { get; init; }
    public required string Model { get; init; }
    public string? SerialNumber { get; init; }
    public required long SizeBytes { get; init; }
    public required int SectorSize { get; init; }
    public BusKind Bus { get; init; }
    public PartitionStyle PartitionStyle { get; init; }

    /// <summary>GUID (GPT) o firma (MBR) del disco; nulo si no está inicializado.</summary>
    public string? Identifier { get; init; }

    public string? DevicePath { get; init; }
    public string? Location { get; init; }

    public static DiskIdentity From(DiskInfo disk)
    {
        ArgumentNullException.ThrowIfNull(disk);
        return new DiskIdentity
        {
            Number = disk.Number,
            Model = disk.Model,
            SerialNumber = disk.SerialNumber,
            SizeBytes = disk.SizeBytes,
            SectorSize = disk.LogicalSectorSize,
            Bus = disk.Bus,
            PartitionStyle = disk.PartitionStyle,
            Identifier = disk.Identifier,
            DevicePath = disk.DevicePath,
            Location = disk.Location,
        };
    }

    /// <summary>Diferencia con el mismo disco leído de nuevo del inventario; nulo si coincide.</summary>
    internal string? FindMismatch(DiskInfo current)
    {
        if (current.Number != Number)
        {
            return "el número de disco ha cambiado";
        }

        if (!SameText(DevicePath, current.DevicePath) || !SameText(Location, current.Location))
        {
            return "ahora es otro dispositivo";
        }

        if (!SameSerial(SerialNumber, current.SerialNumber, requireBoth: false) || current.SizeBytes != SizeBytes)
        {
            return "el número de serie o la capacidad no coinciden";
        }

        if (current.LogicalSectorSize != SectorSize)
        {
            return "el tamaño de sector no coincide";
        }

        if (current.PartitionStyle != PartitionStyle || !SameText(Identifier, current.Identifier))
        {
            return "su tabla de particiones ha cambiado";
        }

        return null;
    }

    /// <summary>Diferencia con lo que declara el dispositivo abierto; nulo si coincide.</summary>
    internal string? FindMismatch(DeviceFacts facts)
    {
        if (facts.DeviceNumber != Number)
        {
            return $"el dispositivo abierto es el disco {facts.DeviceNumber}";
        }

        if (facts.SizeBytes != SizeBytes)
        {
            return "la capacidad no coincide";
        }

        if (facts.SectorSize != SectorSize)
        {
            return "el tamaño de sector no coincide";
        }

        if (!SameSerial(SerialNumber, facts.SerialNumber, requireBoth: false))
        {
            return "el número de serie no coincide";
        }

        if (Bus != BusKind.Unknown && facts.Bus != BusKind.Unknown && Bus != facts.Bus)
        {
            return "el tipo de conexión no coincide";
        }

        return null;
    }

    /// <summary>Descripción corta para mensajes y registro: «Disco 1 · Modelo · 500 GB».</summary>
    public string Describe(IFormatProvider? provider = null) =>
        $"Disco {Number} · {Model} · {ByteSize.Format(SizeBytes, provider)}";

    private static bool SameText(string? expected, string? actual) =>
        expected is null || actual is null || string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Compara números de serie sin espacios, guiones, puntos ni guiones bajos: cada vía de
    /// Windows los presenta con distinto formato. Si falta uno de los dos, no se puede comparar.
    /// </summary>
    private static bool SameSerial(string? expected, string? actual, bool requireBoth)
    {
        if (expected is null || actual is null)
        {
            return !requireBoth;
        }

        return string.Equals(NormalizeSerial(expected), NormalizeSerial(actual), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeSerial(string serial) =>
        string.Concat(serial.Where(c => !char.IsWhiteSpace(c) && c is not ('-' or '_' or '.')));
}
