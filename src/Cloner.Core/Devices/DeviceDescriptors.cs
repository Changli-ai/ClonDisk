using System.Buffers.Binary;
using System.Text;

namespace Cloner.Core.Devices;

/// <summary>Interpreta las estructuras que devuelven los IOCTL de disco. Sin llamadas al sistema.</summary>
internal static class DeviceDescriptors
{
    /// <summary>STORAGE_DEVICE_NUMBER: DeviceType, DeviceNumber, PartitionNumber.</summary>
    public const int DeviceNumberSize = 12;

    /// <summary>DISK_GEOMETRY_EX sin los datos de detección que la siguen.</summary>
    public const int GeometrySize = 32;

    /// <summary>STORAGE_PROPERTY_QUERY con PropertyId = StorageDeviceProperty y QueryType = PropertyStandardQuery.</summary>
    public const int PropertyQuerySize = 12;

    public static int ParseDeviceNumber(ReadOnlySpan<byte> data)
    {
        Require(data, DeviceNumberSize);
        return checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data[4..]));
    }

    /// <summary>DISK_GEOMETRY ocupa 24 bytes (BytesPerSector en el 20) y DiskSize va en el 24.</summary>
    public static (int SectorSize, long DiskSize) ParseGeometry(ReadOnlySpan<byte> data)
    {
        Require(data, GeometrySize);
        var sectorSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data[20..]));
        var diskSize = BinaryPrimitives.ReadInt64LittleEndian(data[24..]);
        return (sectorSize, diskSize);
    }

    /// <summary>STORAGE_DEVICE_DESCRIPTOR: cadenas ASCII terminadas en cero, referidas por desplazamiento.</summary>
    public static (string? ProductId, string? SerialNumber, int BusType) ParseDeviceDescriptor(ReadOnlySpan<byte> data)
    {
        Require(data, 36);
        var size = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(data[4..]), (uint)data.Length);
        var descriptor = data[..Math.Max(size, 36)];

        var productId = ReadString(descriptor, BinaryPrimitives.ReadUInt32LittleEndian(descriptor[16..]));
        var serial = ReadString(descriptor, BinaryPrimitives.ReadUInt32LittleEndian(descriptor[24..]));
        var busType = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(descriptor[28..]));
        return (productId, serial, busType);
    }

    private static string? ReadString(ReadOnlySpan<byte> data, uint offset)
    {
        if (offset == 0 || offset >= data.Length)
        {
            return null;
        }

        var text = data[(int)offset..];
        var end = text.IndexOf((byte)0);
        if (end >= 0)
        {
            text = text[..end];
        }

        var value = Encoding.ASCII.GetString(text).Trim();
        return value.Length > 0 ? value : null;
    }

    private static void Require(ReadOnlySpan<byte> data, int length)
    {
        if (data.Length < length)
        {
            throw new ArgumentException($"Se esperaban al menos {length} bytes y hay {data.Length}.", nameof(data));
        }
    }
}
