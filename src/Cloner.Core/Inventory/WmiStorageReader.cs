using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;

namespace Cloner.Core.Inventory;

/// <summary>
/// Lee discos, particiones y volúmenes con la Storage Management API de Windows (WMI).
/// Solo consulta: no abre ni modifica ningún disco.
/// </summary>
internal sealed class WmiStorageReader : IStorageReader
{
    private const string StorageNamespace = @"root\Microsoft\Windows\Storage";
    private const string EncryptionNamespace = @"root\CIMV2\Security\MicrosoftVolumeEncryption";

    public StorageSnapshot Read() => new()
    {
        Disks = Query(StorageNamespace, "MSFT_Disk", ReadDisk),
        PhysicalDisks = Query(StorageNamespace, "MSFT_PhysicalDisk", ReadPhysicalDisk),
        Partitions = Query(StorageNamespace, "MSFT_Partition", ReadPartition),
        Volumes = Query(StorageNamespace, "MSFT_Volume", ReadVolume),
        Encryption = TryReadEncryption(),
    };

    private static List<T> Query<T>(string wmiNamespace, string className, Func<ManagementObject, T> map)
    {
        using var searcher = new ManagementObjectSearcher(wmiNamespace, $"SELECT * FROM {className}");
        using var results = searcher.Get();

        var items = new List<T>();
        foreach (var result in results)
        {
            using var item = (ManagementObject)result;
            items.Add(map(item));
        }

        return items;
    }

    private static RawDisk ReadDisk(ManagementObject disk) => new()
    {
        Number = Number<uint>(disk, "Number") ?? 0,
        FriendlyName = disk["FriendlyName"] as string,
        Model = disk["Model"] as string,
        SerialNumber = disk["SerialNumber"] as string,
        Size = Number<ulong>(disk, "Size") ?? 0,
        BusType = Number<ushort>(disk, "BusType") ?? 0,
        PartitionStyle = Number<ushort>(disk, "PartitionStyle") ?? 0,
        LogicalSectorSize = Number<uint>(disk, "LogicalSectorSize") ?? 0,
        PhysicalSectorSize = Number<uint>(disk, "PhysicalSectorSize") ?? 0,
        IsBoot = Flag(disk, "IsBoot"),
        IsSystem = Flag(disk, "IsSystem"),
        IsOffline = Flag(disk, "IsOffline"),
        IsReadOnly = Flag(disk, "IsReadOnly"),
        HealthStatus = Number<ushort>(disk, "HealthStatus") ?? ushort.MaxValue,
        Signature = Number<uint>(disk, "Signature"),
        Guid = disk["Guid"] as string,
    };

    private static RawPhysicalDisk ReadPhysicalDisk(ManagementObject physicalDisk) => new()
    {
        DeviceId = physicalDisk["DeviceId"] as string ?? string.Empty,
        MediaType = Number<ushort>(physicalDisk, "MediaType") ?? 0,
        HealthStatus = Number<ushort>(physicalDisk, "HealthStatus") ?? ushort.MaxValue,
        Reliability = TryReadReliability(physicalDisk),
    };

    private static RawPartition ReadPartition(ManagementObject partition) => new()
    {
        DiskNumber = Number<uint>(partition, "DiskNumber") ?? 0,
        PartitionNumber = Number<uint>(partition, "PartitionNumber") ?? 0,
        Offset = Number<ulong>(partition, "Offset") ?? 0,
        Size = Number<ulong>(partition, "Size") ?? 0,
        GptType = partition["GptType"] as string,
        MbrType = Number<ushort>(partition, "MbrType"),
        DriveLetter = Number<char>(partition, "DriveLetter") ?? '\0',
        IsBoot = Flag(partition, "IsBoot"),
        IsSystem = Flag(partition, "IsSystem"),
        IsActive = Flag(partition, "IsActive"),
        IsHidden = Flag(partition, "IsHidden"),
        AccessPaths = partition["AccessPaths"] as string[] ?? [],
    };

    private static RawVolume ReadVolume(ManagementObject volume) => new()
    {
        Path = volume["Path"] as string ?? string.Empty,
        FileSystem = volume["FileSystem"] as string,
        Label = volume["FileSystemLabel"] as string,
        Size = Number<ulong>(volume, "Size") ?? 0,
        SizeRemaining = Number<ulong>(volume, "SizeRemaining") ?? 0,
    };

    /// <summary>Los contadores de fiabilidad exigen administrador; sin él se devuelve nulo.</summary>
    private static RawReliability? TryReadReliability(ManagementObject physicalDisk)
    {
        try
        {
            using var related = physicalDisk.GetRelated("MSFT_StorageReliabilityCounter");
            foreach (var result in related)
            {
                using var counter = (ManagementObject)result;
                return new RawReliability
                {
                    Temperature = (int?)Number<uint>(counter, "Temperature"),
                    ReadErrorsUncorrected = ToLong(Number<ulong>(counter, "ReadErrorsUncorrected")),
                    WriteErrorsUncorrected = ToLong(Number<ulong>(counter, "WriteErrorsUncorrected")),
                    Wear = (int?)Number<uint>(counter, "Wear"),
                    PowerOnHours = ToLong(Number<ulong>(counter, "PowerOnHours")),
                };
            }

            return null;
        }
        catch (Exception ex) when (IsAccessOrAvailabilityError(ex))
        {
            return null;
        }
    }

    /// <summary>El estado de BitLocker exige administrador; sin él se devuelve nulo.</summary>
    private static List<RawEncryption>? TryReadEncryption()
    {
        // Sin privilegios la consulta siempre se deniega, y tarda unos 5 segundos en hacerlo.
        if (!Elevation.IsAdministrator())
        {
            return null;
        }

        try
        {
            return Query(EncryptionNamespace, "Win32_EncryptableVolume", volume => new RawEncryption
            {
                VolumePath = volume["DeviceID"] as string ?? string.Empty,
                ProtectionStatus = Number<uint>(volume, "ProtectionStatus") ?? 2,
                ConversionStatus = Number<uint>(volume, "ConversionStatus"),
            });
        }
        catch (Exception ex) when (IsAccessOrAvailabilityError(ex))
        {
            return null;
        }
    }

    private static bool IsAccessOrAvailabilityError(Exception ex) =>
        ex is ManagementException or UnauthorizedAccessException or COMException;

    /// <summary>Lee una propiedad numérica; nulo si no tiene valor o la clase no la define.</summary>
    private static T? Number<T>(ManagementBaseObject source, string property)
        where T : struct
    {
        object? value;
        try
        {
            value = source[property];
        }
        catch (ManagementException)
        {
            return null;
        }

        return value is null ? null : (T)Convert.ChangeType(value, typeof(T), CultureInfo.InvariantCulture);
    }

    private static bool Flag(ManagementBaseObject source, string property) => source[property] is true;

    private static long? ToLong(ulong? value) => value is { } v ? (long)Math.Min(v, long.MaxValue) : null;
}
