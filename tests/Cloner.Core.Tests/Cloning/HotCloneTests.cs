using Cloner.Core.Cloning;
using Cloner.Core.Devices;
using Cloner.Core.Inventory;
using Cloner.Core.PartitionTables;
using Microsoft.Extensions.Logging.Abstractions;
using static Cloner.Core.Tests.Cloning.DiskImages;

namespace Cloner.Core.Tests.Cloning;

/// <summary>Planificación del clonado en caliente (instantáneas) y de la copia inteligente.</summary>
public class HotClonePlannerTests
{
    private static readonly CloneEnvironment WithSnapshots = new() { SnapshotsAvailable = true };
    private static readonly CloneEnvironment WinPE = new() { SnapshotsAvailable = false };

    private static (DiskInfo Disk, PartitionTableInfo Table) SystemDisk(string fileSystem = "NTFS")
    {
        var image = Gpt(8 * MiB, Guid.NewGuid(), (MiB, 6 * MiB));
        var windows = TestDisks.Partition(1, MiB, 6 * MiB, 'C') with { ContainsWindows = true };
        windows = windows with { Volume = windows.Volume! with { FileSystem = fileSystem } };
        var disk = TestDisks.Info(0, image, [windows]) with { ContainsWindows = true, ContainsBootLoader = true };
        return (disk, TestDisks.Table(image));
    }

    private static DiskInfo Target() => TestDisks.Info(2, Garbage(8 * MiB));

    [Fact]
    public void RunningSystemDiskIsClonedFromASnapshot()
    {
        var (source, table) = SystemDisk();

        var result = ClonePlanner.Create(source, Target(), table, WithSnapshots, new CloneOptions());

        var plan = Assert.IsType<ClonePlan>(result.Plan);
        Assert.True(plan.SourceIsSystemDisk);
        var step = Assert.Single(plan.Steps, s => s.Kind == CloneStepKind.CopyPartition);
        Assert.True(step.FromSnapshot);
        Assert.True(step.UsedBlocksOnly);
        Assert.Single(plan.SnapshotPartitions);
        Assert.Contains(result.Warnings, w => w.Contains("instantánea de C:", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, w => w.Contains("disco del sistema", StringComparison.Ordinal));
    }

    [Fact]
    public void SystemDiskCannotBeClonedWithoutSnapshots()
    {
        var (source, table) = SystemDisk();

        var result = ClonePlanner.Create(source, Target(), table, WinPE, new CloneOptions());

        Assert.Null(result.Plan);
        Assert.Contains(result.Problems, p => p.Contains("VSS", StringComparison.Ordinal));
    }

    [Fact]
    public void SystemDiskWithoutNtfsWindowsVolumeIsRejected()
    {
        var (source, table) = SystemDisk("FAT32");

        Assert.Null(ClonePlanner.Create(source, Target(), table, WithSnapshots, new CloneOptions()).Plan);
    }

    [Fact]
    public void SystemDiskStaysForbiddenAsTarget()
    {
        var (system, _) = SystemDisk();
        var image = Gpt(8 * MiB, Guid.NewGuid(), (MiB, 6 * MiB));
        var data = TestDisks.Info(1, image, [TestDisks.Partition(1, MiB, 6 * MiB, 'E')]);

        var result = ClonePlanner.Create(data, system, TestDisks.Table(image), WithSnapshots, new CloneOptions());

        Assert.Null(result.Plan);
        Assert.Contains(result.Problems, p => p.Contains("disco del sistema", StringComparison.Ordinal));
    }

    [Fact]
    public void SmartCopySkipsUnpartitionedSpaceAndEstimatesUsedBytes()
    {
        var (source, table) = SystemDisk();

        var plan = ClonePlanner.Create(source, Target(), table, WithSnapshots, new CloneOptions()).Plan!;

        Assert.Equal(CloneMode.UsedBlocks, plan.Mode);
        Assert.DoesNotContain(plan.Steps, s => s.Description.Contains("sin particionar", StringComparison.Ordinal));
        var step = plan.Steps.Single(s => s.Kind == CloneStepKind.CopyPartition);
        Assert.Equal(6 * MiB, step.EstimatedBytes);
        Assert.True(plan.BytesToCopy < 8 * MiB);
    }

    [Fact]
    public void SectorBySectorFromASnapshotCopiesWholePartitions()
    {
        var (source, table) = SystemDisk();

        var plan = ClonePlanner.Create(source, Target(), table, WithSnapshots, new CloneOptions { Mode = CloneMode.SectorBySector }).Plan!;

        var step = plan.Steps.Single(s => s.Kind == CloneStepKind.CopyPartition);
        Assert.True(step.FromSnapshot);
        Assert.False(step.UsedBlocksOnly);
        Assert.Contains(plan.Steps, s => s.Description.Contains("sin particionar", StringComparison.Ordinal));
    }

    [Fact]
    public void NonNtfsVolumesAreCopiedWholeFromTheDisk()
    {
        var image = Gpt(8 * MiB, Guid.NewGuid(), (MiB, 6 * MiB));
        var exfat = TestDisks.Partition(1, MiB, 6 * MiB, 'F');
        exfat = exfat with { Volume = exfat.Volume! with { FileSystem = "exFAT" } };
        var source = TestDisks.Info(1, image, [exfat]);

        var plan = ClonePlanner.Create(source, Target(), TestDisks.Table(image), WithSnapshots, new CloneOptions()).Plan!;

        var step = plan.Steps.Single(s => s.Kind == CloneStepKind.CopyPartition);
        Assert.False(step.FromSnapshot);
        Assert.False(step.UsedBlocksOnly);
        Assert.Equal(6 * MiB, step.ExpectedBytes);
    }

    [Fact]
    public void ReadOnlyDiskIsReadDirectlyButStillOnlyUsedBlocks()
    {
        var image = Gpt(8 * MiB, Guid.NewGuid(), (MiB, 6 * MiB));
        var source = TestDisks.Info(1, image, [TestDisks.Partition(1, MiB, 6 * MiB, 'E')]) with { IsReadOnly = true };

        var plan = ClonePlanner.Create(source, Target(), TestDisks.Table(image), WithSnapshots, new CloneOptions()).Plan!;

        var step = plan.Steps.Single(s => s.Kind == CloneStepKind.CopyPartition);
        Assert.False(step.FromSnapshot);
        Assert.True(step.UsedBlocksOnly);
    }

    [Fact]
    public void LogicalPartitionTablesAreKeptInSmartCopy()
    {
        var image = Mbr(8 * MiB, 0x1234ABCD, (MiB, 6 * MiB));
        var extended = new PartitionInfo { Number = 1, OffsetBytes = 2 * MiB, SizeBytes = 5 * MiB, Kind = PartitionKind.Extended };
        var logical = TestDisks.Partition(5, 3 * MiB, 3 * MiB, 'L');
        var source = TestDisks.Info(1, image, [extended, logical]);

        var plan = ClonePlanner.Create(source, Target(), TestDisks.Table(image), WithSnapshots, new CloneOptions()).Plan!;

        var ebrCopies = plan.Steps.Where(s => s.Kind == CloneStepKind.Copy).ToList();
        Assert.Contains(ebrCopies, s => s.Offset == 2 * MiB && s.End == 3 * MiB);
        Assert.Contains(ebrCopies, s => s.Offset == 6 * MiB && s.End == 7 * MiB);
        Assert.DoesNotContain(ebrCopies, s => s.Offset < 2 * MiB && s.Offset >= PartitionTable.HeadBytes);
    }
}

/// <summary>Ejecución del clonado en caliente y de la copia inteligente con discos en memoria.</summary>
public class HotCloneExecutorTests
{
    private static CloneResult Run(CloneExecutorTestsAccess setup) => setup.Run();

    [Fact]
    public void ChangesAfterTheSnapshotDoNotReachTheClone()
    {
        var setup = new CloneExecutorTestsAccess(snapshots: true);
        var original = setup.SourceImage.AsSpan((int)MiB, (int)(6 * MiB)).ToArray();
        setup.Access.AfterSnapshot = () => setup.SourceImage.AsSpan((int)MiB, (int)(6 * MiB)).Fill(0xAB);

        var result = Run(setup);

        Assert.Equal(CloneOutcome.Succeeded, result.Outcome);
        Assert.True(result.Verified);
        Assert.True(setup.TargetImage.AsSpan((int)MiB, (int)(6 * MiB)).SequenceEqual(original));
        var snapshot = Assert.Single(setup.Access.Snapshots);
        Assert.True(snapshot.Completed);
        Assert.True(snapshot.Disposed);
    }

    [Fact]
    public void SnapshottedVolumesAreNotLocked()
    {
        var setup = new CloneExecutorTestsAccess(snapshots: true);
        setup.Access.BusySourceVolumes.Add("E:");

        var result = Run(setup);

        Assert.Equal(CloneOutcome.Succeeded, result.Outcome);
        Assert.DoesNotContain(setup.Access.Locks, l => !l.Dismount && l.Volumes.Count > 0);
    }

    [Fact]
    public void BusyVolumeWithoutSnapshotStopsBeforeWriting()
    {
        var setup = new CloneExecutorTestsAccess(snapshots: false);
        setup.Access.BusySourceVolumes.Add("E:");

        var result = Run(setup);

        Assert.Equal(CloneOutcome.Failed, result.Outcome);
        Assert.Contains("en uso", result.Message, StringComparison.Ordinal);
        Assert.Empty(setup.Target.Writes);
    }

    [Fact]
    public void BusyEfiPartitionIsCopiedAnyway()
    {
        var setup = new CloneExecutorTestsAccess(snapshots: true, efi: true);
        setup.Access.BusySourceVolumes.Add("E:");

        var result = Run(setup);

        Assert.Equal(CloneOutcome.Succeeded, result.Outcome);
        Assert.Empty(setup.Access.Snapshots);
    }

    [Fact]
    public void SmartCopyWritesOnlyUsedBlocks()
    {
        ByteRange[] used = [new(0, MiB), new(4 * MiB, 2 * MiB)];
        var setup = new CloneExecutorTestsAccess(snapshots: true, mode: CloneMode.UsedBlocks, usedRanges: used);
        var source = setup.SourceImage.ToArray();
        var target = setup.TargetImage.ToArray();

        var result = Run(setup);

        Assert.Equal(CloneOutcome.Succeeded, result.Outcome);
        Assert.True(result.Verified);
        var clone = setup.TargetImage;
        Assert.True(clone.AsSpan((int)MiB, (int)MiB).SequenceEqual(source.AsSpan((int)MiB, (int)MiB)));
        Assert.True(clone.AsSpan((int)(2 * MiB), (int)(3 * MiB)).SequenceEqual(target.AsSpan((int)(2 * MiB), (int)(3 * MiB))));
        Assert.True(clone.AsSpan((int)(5 * MiB), (int)(2 * MiB)).SequenceEqual(source.AsSpan((int)(5 * MiB), (int)(2 * MiB))));
        Assert.Equal(PartitionTable.HeadBytes + (3 * MiB) + (33 * Sector), result.BytesWritten);
        Assert.Equal(source.AsSpan(0, Sector).ToArray(), clone.AsSpan(0, Sector).ToArray());
    }

    [Fact]
    public void EndOfPartitionBeyondTheSnapshotIsReadFromTheDisk()
    {
        // La instantánea no deja leer el último clúster (allí NTFS guarda la copia del sector de arranque).
        ByteRange[] used = [new(0, MiB)];
        var setup = new CloneExecutorTestsAccess(snapshots: true, mode: CloneMode.UsedBlocks, usedRanges: used);
        setup.Access.SnapshotReadableLength = (6 * MiB) - 4096;
        var source = setup.SourceImage.ToArray();

        var result = Run(setup);

        Assert.True(result.Outcome == CloneOutcome.Succeeded, result.Message);
        var tail = (int)(7 * MiB) - 4096;
        Assert.True(setup.TargetImage.AsSpan(tail, 4096).SequenceEqual(source.AsSpan(tail, 4096)));
        Assert.Equal(PartitionTable.HeadBytes + MiB + 4096 + (33 * Sector), result.BytesWritten);
    }

    [Fact]
    public void WholePartitionFromASnapshotAlsoTakesTheUnreadableEndFromTheDisk()
    {
        var setup = new CloneExecutorTestsAccess(snapshots: true);
        setup.Access.SnapshotReadableLength = (6 * MiB) - 4096;
        var source = setup.SourceImage.ToArray();

        var result = Run(setup);

        Assert.True(result.Outcome == CloneOutcome.Succeeded, result.Message);
        Assert.True(setup.TargetImage.AsSpan((int)MiB, (int)(6 * MiB)).SequenceEqual(source.AsSpan((int)MiB, (int)(6 * MiB))));
    }

    [Fact]
    public void SnapshotFailureWritesNothing()
    {
        var setup = new CloneExecutorTestsAccess(snapshots: true);
        setup.Access.SnapshotFailure = "Windows no pudo crear las instantáneas (VSS).";

        var result = Run(setup);

        Assert.Equal(CloneOutcome.Failed, result.Outcome);
        Assert.Contains("VSS", result.Message, StringComparison.Ordinal);
        Assert.Empty(setup.Target.Writes);
    }

    [Fact]
    public void SnapshotsAreReleasedWhenTheCloneFails()
    {
        var setup = new CloneExecutorTestsAccess(snapshots: true);
        setup.Target.AfterWrite = offset =>
        {
            if (offset == 5 * MiB)
            {
                throw new IOException("Fallo de escritura.");
            }
        };

        var result = Run(setup);

        Assert.Equal(CloneOutcome.Failed, result.Outcome);
        var snapshot = Assert.Single(setup.Access.Snapshots);
        Assert.False(snapshot.Completed);
        Assert.True(snapshot.Disposed);
    }
}

/// <summary>Origen GPT de 8 MiB con una partición E: de 6 MiB y destino de 8 MiB, en memoria.</summary>
internal sealed class CloneExecutorTestsAccess
{
    public CloneExecutorTestsAccess(bool snapshots, CloneMode mode = CloneMode.SectorBySector, IReadOnlyList<ByteRange>? usedRanges = null, bool efi = false)
    {
        SourceImage = Gpt(8 * MiB, Guid.NewGuid(), (MiB, 6 * MiB));
        TargetImage = Gpt(8 * MiB, Guid.NewGuid(), (MiB, 2 * MiB));
        new Random(9).NextBytes(TargetImage.AsSpan((int)(2 * MiB), (int)(5 * MiB)));

        var partition = TestDisks.Partition(1, MiB, 6 * MiB, 'E', "Datos");
        if (efi)
        {
            partition = partition with { Kind = PartitionKind.EfiSystem, Volume = partition.Volume! with { FileSystem = "FAT32" } };
        }

        var source = TestDisks.Info(1, SourceImage, [partition]);
        var target = TestDisks.Info(2, TargetImage);
        var environment = new CloneEnvironment { SnapshotsAvailable = snapshots };
        var result = ClonePlanner.Create(source, target, TestDisks.Table(SourceImage), environment, new CloneOptions { Mode = mode });
        Plan = result.Plan ?? throw new InvalidOperationException(string.Join(" ", result.Problems));

        Source = new MemoryDisk(SourceImage);
        Target = new MemoryDisk(TargetImage);
        Access.Disks[1] = Source;
        Access.Disks[2] = Target;
        Access.Volumes[partition.Volume!.Path] = (1, MiB, 6 * MiB, usedRanges);
    }

    public byte[] SourceImage { get; }
    public byte[] TargetImage { get; }
    public ClonePlan Plan { get; }
    public MemoryDisk Source { get; }
    public MemoryDisk Target { get; }
    public FakeDiskAccess Access { get; } = new();

    public CloneResult Run() => new CloneExecutor(Access, NullLogger.Instance).Execute(Plan, null, CancellationToken.None);
}
