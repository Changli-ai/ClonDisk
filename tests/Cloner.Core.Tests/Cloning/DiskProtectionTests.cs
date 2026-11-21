using Cloner.Core.Cloning;
using Cloner.Core.Inventory;
using Cloner.Core.PartitionTables;
using static Cloner.Core.Tests.Cloning.DiskImages;

namespace Cloner.Core.Tests.Cloning;

/// <summary>
/// Discos que este equipo necesita (candado), y el caso real del 20/11/2026: el Windows en uso
/// estaba en el disco 0, pero arrancaba con la partición EFI de otro disco que solo se había
/// formateado.
/// </summary>
public class DiskProtectionTests
{
    private static readonly CloneEnvironment WithSnapshots = new() { SnapshotsAvailable = true };

    private static DiskInfo Data(int number = 2) =>
        TestDisks.Info(number, Gpt(8 * MiB, Guid.NewGuid(), (MiB, 6 * MiB)), [TestDisks.Partition(1, MiB, 6 * MiB, 'E')]);

    /// <summary>Disco que solo tiene el arranque del equipo: una EFI en uso y una NTFS vacía.</summary>
    private static (DiskInfo Disk, PartitionTableInfo Table) BootLoaderOnly(int number = 1)
    {
        var image = Gpt(8 * MiB, Guid.NewGuid(), (MiB, 6 * MiB));
        var disk = TestDisks.Info(number, image, [TestDisks.Partition(1, MiB, 6 * MiB, 'D') with { ContainsBootLoader = true }])
            with { ContainsBootLoader = true };
        return (disk, TestDisks.Table(image));
    }

    /// <summary>Disco con el Windows en uso pero sin su arranque, que está en otro disco.</summary>
    private static (DiskInfo Disk, PartitionTableInfo Table) WindowsWithoutBootLoader()
    {
        var image = Gpt(8 * MiB, Guid.NewGuid(), (MiB, 6 * MiB));
        var disk = TestDisks.Info(0, image, [TestDisks.Partition(1, MiB, 6 * MiB, 'C') with { ContainsWindows = true }])
            with { ContainsWindows = true };
        return (disk, TestDisks.Table(image));
    }

    [Fact]
    public void WindowsDiskBootLoaderDiskAndAppDiskAreLocked()
    {
        var windows = Data(0) with { ContainsWindows = true, ContainsBootLoader = true };
        var bootLoader = Data(1) with { ContainsBootLoader = true };
        var app = Data(3);

        Assert.Equal("Windows en uso", DiskProtection.LockLabel(windows, hostDiskNumber: null));
        Assert.Equal("Arranque del equipo", DiskProtection.LockLabel(bootLoader, hostDiskNumber: null));
        Assert.Equal("Disco de ClonDisk", DiskProtection.LockLabel(app, hostDiskNumber: 3));
        Assert.Contains("dejaría de arrancar", DiskProtection.WhyLocked(bootLoader, null), StringComparison.Ordinal);
        Assert.NotNull(DiskProtection.WhyLocked(app, 3));
    }

    [Fact]
    public void OrdinaryDiskIsNotLocked()
    {
        Assert.Null(DiskProtection.LockLabel(Data(), hostDiskNumber: 0));
        Assert.Null(DiskProtection.WhyLocked(Data(), hostDiskNumber: 0));
    }

    [Fact]
    public void BootLoaderDiskIsRefusedAsTargetWithItsOwnReason()
    {
        var (source, table) = WindowsWithoutBootLoader();
        var (bootLoader, _) = BootLoaderOnly();

        var result = ClonePlanner.Create(source, bootLoader, table, WithSnapshots, new CloneOptions());

        Assert.Null(result.Plan);
        Assert.Contains(result.Problems, p => p.Contains("partición de arranque", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Problems, p => p.Contains("disco del sistema en uso", StringComparison.Ordinal));
    }

    [Fact]
    public void BootLoaderOnlyDiskCanBeTheSourceWithoutAWindowsSnapshot()
    {
        // Antes salía «No se puede hacer una instantánea del volumen de Windows del disco 1».
        var (source, table) = BootLoaderOnly();

        var result = ClonePlanner.Create(source, Data(), table, WithSnapshots, new CloneOptions());

        Assert.Empty(result.Problems);
        Assert.NotNull(result.Plan);
        Assert.False(result.Plan!.SourceIsSystemDisk);
        Assert.Contains(result.Warnings, w => w.Contains("partición de arranque", StringComparison.Ordinal));
    }

    [Fact]
    public void CloningWindowsWhoseBootLoaderIsElsewhereWarns()
    {
        var (source, table) = WindowsWithoutBootLoader();

        var result = ClonePlanner.Create(source, Data(), table, WithSnapshots, new CloneOptions());

        Assert.NotNull(result.Plan);
        Assert.Contains(result.Warnings, w => w.Contains("está en otro disco", StringComparison.Ordinal));
    }

    [Fact]
    public void PreparingRefusesLockedReadOnlyAndRealDisksInVirtualMode()
    {
        Assert.NotNull(DiskPreparer.Check(Data() with { ContainsWindows = true }, null, virtualOnly: false));
        Assert.NotNull(DiskPreparer.Check(Data() with { ContainsBootLoader = true }, null, virtualOnly: false));
        Assert.NotNull(DiskPreparer.Check(Data(), hostDiskNumber: 2, virtualOnly: false));
        Assert.NotNull(DiskPreparer.Check(Data() with { IsReadOnly = true }, null, virtualOnly: false));
        Assert.NotNull(DiskPreparer.Check(Data() with { Bus = BusKind.Usb }, null, virtualOnly: true));
        Assert.Null(DiskPreparer.Check(Data(), null, virtualOnly: true));
        Assert.Null(DiskPreparationService.CannotPrepare(Data() with { Bus = BusKind.Usb }, null));
    }
}
