using Cloner.Core.Cloning;
using Cloner.Core.Inventory;
using Cloner.Core.Layout;
using Microsoft.Extensions.Logging.Abstractions;
using static Cloner.Core.Tests.Cloning.DiskImages;

namespace Cloner.Core.Tests.Cloning;

/// <summary>Reparación del arranque de un clon que ya no arranca.</summary>
public class BootRepairTests
{
    // Particiones como en DiskImages.Gpt: la entrada i tiene el GUID (i + 1, 0, 0, …, 1).
    private static readonly Guid WindowsGuid = new(2, 0, 0, [0, 0, 0, 0, 0, 0, 0, 1]);

    private static (DiskInfo Disk, FakeDiskAccess Access) Clone(bool offline = true, bool efi = true, bool system = false)
    {
        var image = Gpt(48 * MiB, Guid.NewGuid(), (MiB, 8 * MiB), (9 * MiB, 30 * MiB));
        var efiPartition = TestDisks.Partition(1, MiB, 8 * MiB) with { Kind = efi ? PartitionKind.EfiSystem : PartitionKind.BasicData };
        var windows = TestDisks.Partition(2, 9 * MiB, 30 * MiB, 'D');
        var disk = TestDisks.Info(1, image, [efiPartition, windows]) with { IsOffline = offline, ContainsWindows = system };
        var access = new FakeDiskAccess();
        access.Disks[1] = new MemoryDisk(image);
        access.FakeFinisher.WindowsAt = 9 * MiB;
        return (disk, access);
    }

    private static BootRepairResult Repair(DiskInfo disk, FakeDiskAccess access, int? host = null) =>
        new BootRepairer(access, NullLogger.Instance).Repair(disk, host, progress: null);

    [Fact]
    public void OfflineCloneIsBroughtOnlineAndItsBootRebuiltForItsOwnWindows()
    {
        var (disk, access) = Clone();

        var result = Repair(disk, access);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal([false], access.OfflineCalls);
        var repair = Assert.Single(access.FakeFinisher.InPlaceRepairs);
        Assert.True(repair.Uefi);
        Assert.Equal(FakeFinisher.VolumeAt(9 * MiB), repair.Windows);
        Assert.Equal(FakeFinisher.VolumeAt(MiB), repair.System);
        Assert.Equal(MountedDevices.GptValue(WindowsGuid), repair.Identity);
        Assert.Contains("letra", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlineDiskIsNotTouchedBeforeRepairing()
    {
        var (disk, access) = Clone(offline: false);
        access.FakeFinisher.InPlaceTranslated = 0;

        var result = Repair(disk, access);

        Assert.True(result.Succeeded, result.Message);
        Assert.Empty(access.OfflineCalls);
        Assert.DoesNotContain("letra", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RunningSystemDiskIsRefused()
    {
        var (disk, access) = Clone(system: true);

        var result = Repair(disk, access);

        Assert.False(result.Succeeded);
        Assert.Contains("sistema en uso", result.Message, StringComparison.Ordinal);
        Assert.Empty(access.Opened);
    }

    [Fact]
    public void DiskTheAppRunsFromIsRefused()
    {
        var (disk, access) = Clone();

        Assert.False(Repair(disk, access, host: 1).Succeeded);
        Assert.Empty(access.Opened);
    }

    [Fact]
    public void GptWithoutEfiPartitionIsRefused()
    {
        var (disk, access) = Clone(efi: false);

        var result = Repair(disk, access);

        Assert.False(result.Succeeded);
        Assert.Contains("EFI", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DiskWithoutWindowsIsReported()
    {
        var (disk, access) = Clone();
        access.FakeFinisher.WindowsAt = null;

        var result = Repair(disk, access);

        Assert.False(result.Succeeded);
        Assert.Contains("Windows", result.Message, StringComparison.Ordinal);
        Assert.Empty(access.FakeFinisher.InPlaceRepairs);
    }
}
