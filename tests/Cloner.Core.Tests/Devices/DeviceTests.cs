using System.Buffers.Binary;
using System.Text;
using Cloner.Core.Cloning;
using Cloner.Core.Devices;
using Cloner.Core.Inventory;
using Cloner.Core.Tests.Cloning;

namespace Cloner.Core.Tests.Devices;

public class DeviceDescriptorsTests
{
    [Fact]
    public void DeviceNumberIsTheSecondField()
    {
        var data = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0), 7);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), 3);

        Assert.Equal(3, DeviceDescriptors.ParseDeviceNumber(data));
    }

    [Fact]
    public void GeometryGivesSectorSizeAndDiskSize()
    {
        var data = new byte[256];
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(20), 4096);
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(24), 1_000_204_886_016);

        Assert.Equal((4096, 1_000_204_886_016L), DeviceDescriptors.ParseGeometry(data));
    }

    [Fact]
    public void DescriptorStringsAreReadFromTheirOffsets()
    {
        var data = new byte[1024];
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), 200);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16), 100);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(24), 150);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(28), 17);
        Encoding.ASCII.GetBytes("INTEL SSDPEKNW010T8  \0").CopyTo(data, 100);
        Encoding.ASCII.GetBytes(" BTNH12345678\0").CopyTo(data, 150);

        var (product, serial, bus) = DeviceDescriptors.ParseDeviceDescriptor(data);

        Assert.Equal("INTEL SSDPEKNW010T8", product);
        Assert.Equal("BTNH12345678", serial);
        Assert.Equal(17, bus);
    }

    [Fact]
    public void MissingDescriptorStringsAreNull()
    {
        var data = new byte[64];
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), 64);

        var (product, serial, _) = DeviceDescriptors.ParseDeviceDescriptor(data);

        Assert.Null(product);
        Assert.Null(serial);
    }
}

public class HostDiskTests
{
    [Fact]
    public void VolumeIsMatchedIgnoringCaseAndTrailingBackslash()
    {
        var partition = TestDisks.Partition(1, DiskImages.MiB, DiskImages.MiB, 'D');
        var disks = new[]
        {
            TestDisks.Info(0, new byte[4 * DiskImages.MiB]),
            TestDisks.Info(3, new byte[4 * DiskImages.MiB], [partition]),
        };

        Assert.Equal(3, HostDisk.FindByVolume(partition.Volume!.Path.ToLowerInvariant().TrimEnd('\\'), disks));
        Assert.Null(HostDisk.FindByVolume(@"\\?\Volume{99999999-0000-0000-0000-000000000000}\", disks));
    }
}

public class DiskIdentityTests
{
    private static DiskIdentity Identity(string? serial = "S5GX NX0R-000.000") => new()
    {
        Number = 2,
        Model = "Disco",
        SerialNumber = serial,
        SizeBytes = 500_107_862_016,
        SectorSize = 512,
        Bus = BusKind.Usb,
    };

    private static DeviceFacts Facts(int number = 2, long size = 500_107_862_016, string? serial = "S5GXNX0R_000000", BusKind bus = BusKind.Usb) => new()
    {
        DeviceNumber = number,
        SizeBytes = size,
        SectorSize = 512,
        SerialNumber = serial,
        Bus = bus,
    };

    [Fact]
    public void MatchingDeviceIgnoresSerialFormatting() => Assert.Null(Identity().FindMismatch(Facts()));

    [Fact]
    public void SerialIsSkippedWhenTheDeviceDoesNotReportIt() => Assert.Null(Identity().FindMismatch(Facts(serial: null)));

    [Theory]
    [InlineData(3, 500_107_862_016, "S5GXNX0R000000", BusKind.Usb)]
    [InlineData(2, 500_107_862_017, "S5GXNX0R000000", BusKind.Usb)]
    [InlineData(2, 500_107_862_016, "S5GXNX0R999999", BusKind.Usb)]
    [InlineData(2, 500_107_862_016, "S5GXNX0R000000", BusKind.Sata)]
    public void DifferentDeviceIsRejected(int number, long size, string serial, BusKind bus) =>
        Assert.NotNull(Identity().FindMismatch(Facts(number, size, serial, bus)));
}
