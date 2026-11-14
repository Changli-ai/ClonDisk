using System.Globalization;

namespace Cloner.Core.Inventory;

/// <summary>Convierte los datos crudos de Windows en el modelo de discos y particiones.</summary>
internal static class DiskInventoryBuilder
{
    public static IReadOnlyList<DiskInfo> Build(StorageSnapshot snapshot)
    {
        var volumesByPath = new Dictionary<string, RawVolume>(StringComparer.OrdinalIgnoreCase);
        foreach (var volume in snapshot.Volumes)
        {
            volumesByPath[volume.Path] = volume;
        }

        Dictionary<string, RawEncryption>? encryptionByPath = null;
        if (snapshot.Encryption is not null)
        {
            encryptionByPath = new Dictionary<string, RawEncryption>(StringComparer.OrdinalIgnoreCase);
            foreach (var encryption in snapshot.Encryption)
            {
                encryptionByPath[encryption.VolumePath] = encryption;
            }
        }

        var disks = new List<DiskInfo>();
        foreach (var disk in snapshot.Disks.OrderBy(d => d.Number))
        {
            var physical = snapshot.PhysicalDisks.FirstOrDefault(
                p => p.DeviceId == disk.Number.ToString(CultureInfo.InvariantCulture));
            var bus = MapBus(disk.BusType);

            var partitions = snapshot.Partitions
                .Where(p => p.DiskNumber == disk.Number)
                .OrderBy(p => p.Offset)
                .Select(p => BuildPartition(p, disk.PartitionStyle, volumesByPath, encryptionByPath))
                .ToList();

            disks.Add(new DiskInfo
            {
                Number = checked((int)disk.Number),
                Model = FirstNonEmpty(disk.Model, disk.FriendlyName) ?? "Disco desconocido",
                SerialNumber = FirstNonEmpty(disk.SerialNumber),
                SizeBytes = checked((long)disk.Size),
                Media = MapMedia(physical?.MediaType ?? 0, bus),
                Bus = bus,
                PartitionStyle = MapPartitionStyle(disk.PartitionStyle),
                LogicalSectorSize = checked((int)disk.LogicalSectorSize),
                PhysicalSectorSize = checked((int)disk.PhysicalSectorSize),
                Identifier = BuildIdentifier(disk),
                DevicePath = FirstNonEmpty(disk.Path),
                Location = FirstNonEmpty(disk.Location),
                ContainsWindows = disk.IsBoot,
                ContainsBootLoader = disk.IsSystem,
                IsOffline = disk.IsOffline,
                IsReadOnly = disk.IsReadOnly,
                Health = WorstHealth(MapHealth(disk.HealthStatus), MapHealth(physical?.HealthStatus)),
                Reliability = BuildReliability(physical?.Reliability),
                Partitions = partitions,
            });
        }

        return disks;
    }

    private static PartitionInfo BuildPartition(
        RawPartition partition,
        ushort diskPartitionStyle,
        Dictionary<string, RawVolume> volumesByPath,
        Dictionary<string, RawEncryption>? encryptionByPath)
    {
        var isGpt = diskPartitionStyle == 2;

        RawVolume? volume = null;
        foreach (var path in partition.AccessPaths)
        {
            if (volumesByPath.TryGetValue(path, out volume))
            {
                break;
            }
        }

        // Sin datos de cifrado no se puede afirmar nada; con ellos, un volumen ausente no está cifrado.
        var encryption = EncryptionState.Unknown;
        if (encryptionByPath is not null && volume is not null)
        {
            encryption = encryptionByPath.TryGetValue(volume.Path, out var raw)
                ? MapEncryption(raw)
                : EncryptionState.Off;
        }

        return new PartitionInfo
        {
            Number = checked((int)partition.PartitionNumber),
            OffsetBytes = checked((long)partition.Offset),
            SizeBytes = checked((long)partition.Size),
            Kind = isGpt
                ? PartitionTypes.FromGptType(partition.GptType)
                : PartitionTypes.FromMbrType(partition.MbrType ?? 0),
            TypeId = isGpt
                ? partition.GptType?.Trim('{', '}').ToLowerInvariant()
                : partition.MbrType is { } mbr ? $"0x{mbr:X2}" : null,
            DriveLetter = char.IsLetter(partition.DriveLetter) ? char.ToUpperInvariant(partition.DriveLetter) : null,
            ContainsWindows = partition.IsBoot,
            ContainsBootLoader = partition.IsSystem,
            IsActive = partition.IsActive,
            IsHidden = partition.IsHidden,
            Volume = volume is null
                ? null
                : new VolumeInfo
                {
                    Path = volume.Path,
                    FileSystem = FirstNonEmpty(volume.FileSystem),
                    Label = FirstNonEmpty(volume.Label),
                    SizeBytes = checked((long)volume.Size),
                    FreeBytes = checked((long)volume.SizeRemaining),
                },
            Encryption = encryption,
        };
    }

    private static DiskReliability? BuildReliability(RawReliability? raw) => raw is null
        ? null
        : new DiskReliability
        {
            TemperatureCelsius = raw.Temperature,
            ReadErrorsUncorrected = raw.ReadErrorsUncorrected,
            WriteErrorsUncorrected = raw.WriteErrorsUncorrected,
            WearPercent = raw.Wear,
            PowerOnHours = raw.PowerOnHours,
        };

    private static string? BuildIdentifier(RawDisk disk) => disk.PartitionStyle switch
    {
        2 => disk.Guid?.Trim('{', '}').ToLowerInvariant(),
        1 when disk.Signature is { } signature => signature.ToString("X8", CultureInfo.InvariantCulture),
        _ => null,
    };

    internal static EncryptionState MapEncryption(RawEncryption raw)
    {
        if (raw.ConversionStatus is { } conversion)
        {
            if (conversion == 0)
            {
                return EncryptionState.Off;
            }

            return raw.ProtectionStatus == 1 ? EncryptionState.On : EncryptionState.Suspended;
        }

        return raw.ProtectionStatus switch
        {
            0 => EncryptionState.Off,
            1 => EncryptionState.On,
            _ => EncryptionState.Unknown,
        };
    }

    // Valores de MSFT_Disk.BusType, que son los de STORAGE_BUS_TYPE.
    internal static BusKind MapBus(ushort busType) => busType switch
    {
        0 => BusKind.Unknown,
        1 => BusKind.Scsi,
        3 => BusKind.Ata,
        7 => BusKind.Usb,
        8 => BusKind.Raid,
        10 => BusKind.Sas,
        11 => BusKind.Sata,
        12 => BusKind.Sd,
        13 => BusKind.Mmc,
        14 or 15 => BusKind.Virtual,
        17 => BusKind.Nvme,
        _ => BusKind.Other,
    };

    // Valores de MSFT_PhysicalDisk.MediaType: 3 = HDD, 4 = SSD, 5 = memoria de clase de almacenamiento.
    private static MediaKind MapMedia(ushort mediaType, BusKind bus) => mediaType switch
    {
        3 => MediaKind.Hdd,
        4 or 5 => MediaKind.Ssd,
        _ => bus == BusKind.Nvme ? MediaKind.Ssd : MediaKind.Unknown,
    };

    private static PartitionStyle MapPartitionStyle(ushort style) => style switch
    {
        1 => PartitionStyle.Mbr,
        2 => PartitionStyle.Gpt,
        _ => PartitionStyle.Raw,
    };

    private static HealthState MapHealth(ushort? status) => status switch
    {
        0 => HealthState.Healthy,
        1 => HealthState.Warning,
        2 => HealthState.Unhealthy,
        _ => HealthState.Unknown,
    };

    private static HealthState WorstHealth(HealthState a, HealthState b)
    {
        // El orden del enum es Unknown < Healthy < Warning < Unhealthy.
        return (HealthState)Math.Max((int)a, (int)b);
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            var trimmed = value?.Trim();
            if (!string.IsNullOrEmpty(trimmed))
            {
                return trimmed;
            }
        }

        return null;
    }
}
