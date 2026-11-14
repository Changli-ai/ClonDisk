using Cloner.Core.Inventory;

namespace Cloner.Core.Tests.Inventory;

public class DiskInventoryBuilderTests
{
    private const string EfiVolume = @"\\?\Volume{11111111-1111-1111-1111-111111111111}\";
    private const string WindowsVolume = @"\\?\Volume{22222222-2222-2222-2222-222222222222}\";
    private const string DataVolume = @"\\?\Volume{33333333-3333-3333-3333-333333333333}\";

    /// <summary>Un NVMe GPT con Windows (disco 0) y un HDD MBR por USB (disco 1).</summary>
    private static StorageSnapshot TwoDisks() => new()
    {
        Disks =
        [
            new RawDisk
            {
                Number = 1, Model = "  WDC WD10EZEX  ", SerialNumber = " WD-123 ", Size = 1_000_204_886_016,
                BusType = 7, PartitionStyle = 1, LogicalSectorSize = 512, PhysicalSectorSize = 4096,
                HealthStatus = 0, Signature = 0x00C0FFEE,
                Path = @"\\?\usbstor#disk&ven_wdc#1#{53f56307-b6bf-11d0-94f2-00a0c91efb8b}", Location = "  ",
            },
            new RawDisk
            {
                Number = 0, FriendlyName = "NVMe Test", Size = 512_110_190_592,
                BusType = 17, PartitionStyle = 2, LogicalSectorSize = 512, PhysicalSectorSize = 512,
                IsBoot = true, IsSystem = true, HealthStatus = 0,
                Guid = "{AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE}",
            },
        ],
        PhysicalDisks =
        [
            new RawPhysicalDisk { DeviceId = "0", MediaType = 0, HealthStatus = 0 },
            new RawPhysicalDisk { DeviceId = "1", MediaType = 3, HealthStatus = 1 },
        ],
        Partitions =
        [
            new RawPartition
            {
                DiskNumber = 0, PartitionNumber = 3, Offset = 122_683_392, Size = 511_000_000_000,
                GptType = "{ebd0a0a2-b9e5-4433-87c0-68b6b72699c7}", DriveLetter = 'c', IsBoot = true,
                AccessPaths = [@"C:\", WindowsVolume],
            },
            new RawPartition
            {
                DiskNumber = 0, PartitionNumber = 1, Offset = 1_048_576, Size = 104_857_600,
                GptType = "{c12a7328-f81f-11d2-ba4b-00a0c93ec93b}", IsSystem = true, IsHidden = true,
                AccessPaths = [EfiVolume],
            },
            new RawPartition
            {
                DiskNumber = 0, PartitionNumber = 2, Offset = 105_906_176, Size = 16_777_216,
                GptType = "{e3c9e316-0b5c-4db8-817d-f92df00215ae}", IsHidden = true,
            },
            new RawPartition
            {
                DiskNumber = 1, PartitionNumber = 1, Offset = 1_048_576, Size = 500_000_000_000,
                MbrType = 0x07, DriveLetter = 'D', IsActive = true,
                AccessPaths = [@"D:\", DataVolume],
            },
        ],
        Volumes =
        [
            new RawVolume { Path = WindowsVolume, FileSystem = "NTFS", Label = "", Size = 511_000_000_000, SizeRemaining = 300_000_000_000 },
            new RawVolume { Path = DataVolume.ToUpperInvariant(), FileSystem = "NTFS", Label = "Datos", Size = 500_000_000_000, SizeRemaining = 100_000_000_000 },
        ],
    };

    [Fact]
    public void DevicePathAndLocationAreKeptWhenPresent()
    {
        var disks = DiskInventoryBuilder.Build(TwoDisks());

        Assert.Null(disks[0].DevicePath);
        Assert.StartsWith(@"\\?\usbstor#", disks[1].DevicePath, StringComparison.Ordinal);
        Assert.Null(disks[1].Location);
    }

    [Fact]
    public void DisksAreOrderedByNumberAndPartitionsByOffset()
    {
        var disks = DiskInventoryBuilder.Build(TwoDisks());

        Assert.Equal([0, 1], disks.Select(d => d.Number));
        Assert.Equal([1, 2, 3], disks[0].Partitions.Select(p => p.Number));
    }

    [Fact]
    public void GptSystemDiskIsInterpreted()
    {
        var disk = DiskInventoryBuilder.Build(TwoDisks())[0];

        Assert.Equal("NVMe Test", disk.Model);
        Assert.Null(disk.SerialNumber);
        Assert.Equal(BusKind.Nvme, disk.Bus);
        Assert.Equal(MediaKind.Ssd, disk.Media); // sin MediaType, un NVMe se considera SSD
        Assert.Equal(PartitionStyle.Gpt, disk.PartitionStyle);
        Assert.Equal("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", disk.Identifier);
        Assert.True(disk.IsSystemDisk);
        Assert.Equal(HealthState.Healthy, disk.Health);

        Assert.Equal(
            [PartitionKind.EfiSystem, PartitionKind.MicrosoftReserved, PartitionKind.BasicData],
            disk.Partitions.Select(p => p.Kind));

        var windows = disk.Partitions[2];
        Assert.Equal('C', windows.DriveLetter);
        Assert.True(windows.ContainsWindows);
        Assert.Equal("ebd0a0a2-b9e5-4433-87c0-68b6b72699c7", windows.TypeId);
        Assert.NotNull(windows.Volume);
        Assert.Equal("NTFS", windows.Volume.FileSystem);
        Assert.Null(windows.Volume.Label);
        Assert.Equal(211_000_000_000, windows.Volume.UsedBytes);

        var efi = disk.Partitions[0];
        Assert.True(efi.ContainsBootLoader);
        Assert.Null(efi.DriveLetter);
        Assert.Null(efi.Volume); // Windows no expone el volumen EFI sin privilegios
    }

    [Fact]
    public void MbrUsbDiskIsInterpreted()
    {
        var disk = DiskInventoryBuilder.Build(TwoDisks())[1];

        Assert.Equal("WDC WD10EZEX", disk.Model);
        Assert.Equal("WD-123", disk.SerialNumber);
        Assert.Equal(BusKind.Usb, disk.Bus);
        Assert.Equal(MediaKind.Hdd, disk.Media);
        Assert.Equal(PartitionStyle.Mbr, disk.PartitionStyle);
        Assert.Equal("00C0FFEE", disk.Identifier);
        Assert.Equal(4096, disk.PhysicalSectorSize);
        Assert.False(disk.IsSystemDisk);
        Assert.Equal(HealthState.Warning, disk.Health); // el peor entre disco y disco físico

        var data = Assert.Single(disk.Partitions);
        Assert.Equal(PartitionKind.BasicData, data.Kind);
        Assert.Equal("0x07", data.TypeId);
        Assert.True(data.IsActive);
        Assert.Equal("Datos", data.Volume?.Label); // la ruta del volumen se compara sin distinguir mayúsculas
        Assert.Equal(1_000_204_886_016 - 500_000_000_000, disk.UnallocatedBytes);
    }

    [Fact]
    public void EncryptionIsUnknownWithoutEncryptionData()
    {
        var disks = DiskInventoryBuilder.Build(TwoDisks());

        Assert.All(disks.SelectMany(d => d.Partitions), p => Assert.Equal(EncryptionState.Unknown, p.Encryption));
    }

    [Fact]
    public void EncryptionIsMappedPerVolume()
    {
        var snapshot = TwoDisks() with
        {
            Encryption = [new RawEncryption { VolumePath = WindowsVolume, ProtectionStatus = 1, ConversionStatus = 1 }],
        };

        var disks = DiskInventoryBuilder.Build(snapshot);

        Assert.Equal(EncryptionState.On, disks[0].Partitions[2].Encryption);
        Assert.Equal(EncryptionState.Off, disks[1].Partitions[0].Encryption);     // volumen sin entrada de cifrado
        Assert.Equal(EncryptionState.Unknown, disks[0].Partitions[1].Encryption); // partición sin volumen
    }

    [Theory]
    [InlineData(0u, 0u, EncryptionState.Off)]
    [InlineData(1u, 1u, EncryptionState.On)]
    [InlineData(0u, 1u, EncryptionState.Suspended)] // cifrado, protección desactivada
    [InlineData(0u, 2u, EncryptionState.Suspended)] // cifrado en curso
    public void EncryptionStateCombinesProtectionAndConversion(uint protection, uint conversion, EncryptionState expected)
    {
        var raw = new RawEncryption { VolumePath = WindowsVolume, ProtectionStatus = protection, ConversionStatus = conversion };

        Assert.Equal(expected, DiskInventoryBuilder.MapEncryption(raw));
    }

    [Theory]
    [InlineData(0u, EncryptionState.Off)]
    [InlineData(1u, EncryptionState.On)]
    [InlineData(2u, EncryptionState.Unknown)]
    public void EncryptionStateFallsBackToProtectionStatus(uint protection, EncryptionState expected)
    {
        var raw = new RawEncryption { VolumePath = WindowsVolume, ProtectionStatus = protection };

        Assert.Equal(expected, DiskInventoryBuilder.MapEncryption(raw));
    }

    [Fact]
    public void ReliabilityCountersAreCarriedOver()
    {
        var snapshot = TwoDisks() with
        {
            PhysicalDisks =
            [
                new RawPhysicalDisk
                {
                    DeviceId = "1", MediaType = 3,
                    Reliability = new RawReliability { Temperature = 41, ReadErrorsUncorrected = 7, PowerOnHours = 12_000 },
                },
            ],
        };

        var disks = DiskInventoryBuilder.Build(snapshot);

        Assert.Null(disks[0].Reliability);
        Assert.Equal(41, disks[1].Reliability?.TemperatureCelsius);
        Assert.Equal(7, disks[1].Reliability?.ReadErrorsUncorrected);
        Assert.Null(disks[1].Reliability?.WearPercent);
    }

    [Fact]
    public void ExtendedPartitionDoesNotCountAsAllocatedTwice()
    {
        var snapshot = new StorageSnapshot
        {
            Disks = [new RawDisk { Number = 0, Model = "MBR", Size = 1000, PartitionStyle = 1 }],
            Partitions =
            [
                new RawPartition { DiskNumber = 0, PartitionNumber = 1, Offset = 0, Size = 400, MbrType = 0x07 },
                new RawPartition { DiskNumber = 0, PartitionNumber = 2, Offset = 400, Size = 500, MbrType = 0x0F },
                new RawPartition { DiskNumber = 0, PartitionNumber = 3, Offset = 410, Size = 490, MbrType = 0x07 },
            ],
        };

        var disk = Assert.Single(DiskInventoryBuilder.Build(snapshot));

        Assert.Equal(PartitionKind.Extended, disk.Partitions[1].Kind);
        Assert.Equal(110, disk.UnallocatedBytes);
    }

    [Fact]
    public void UninitializedDiskHasNoIdentifierOrPartitions()
    {
        var snapshot = new StorageSnapshot
        {
            Disks = [new RawDisk { Number = 2, Size = 1000, PartitionStyle = 0, HealthStatus = ushort.MaxValue }],
        };

        var disk = Assert.Single(DiskInventoryBuilder.Build(snapshot));

        Assert.Equal(PartitionStyle.Raw, disk.PartitionStyle);
        Assert.Null(disk.Identifier);
        Assert.Empty(disk.Partitions);
        Assert.Equal(1000, disk.UnallocatedBytes);
        Assert.Equal(HealthState.Unknown, disk.Health);
        Assert.Equal("Disco desconocido", disk.Model);
    }
}
