using System.Buffers.Binary;
using System.Text;
using Cloner.Core.Cloning;
using Cloner.Core.Inventory;
using Cloner.Core.Layout;
using Cloner.Core.PartitionTables;
using Cloner.Core.Tests.Cloning;
using Microsoft.Extensions.Logging.Abstractions;
using static Cloner.Core.Tests.Cloning.DiskImages;

namespace Cloner.Core.Tests.Layout;

/// <summary>Disco GPT de 64 MiB con EFI, Windows (C:) y recuperación, como un disco de sistema típico.</summary>
internal static class SystemLikeDisk
{
    public static readonly Guid DiskGuid = new("11112222-3333-4444-5555-666677778888");

    public static byte[] Image(long size = 64 * MiB) => Gpt(size, DiskGuid, (MiB, 4 * MiB), (5 * MiB, 40 * MiB), (45 * MiB, 8 * MiB));

    public static DiskInfo Info(byte[] image, int number = 1)
    {
        var efi = TestDisks.Partition(1, MiB, 4 * MiB) with { Kind = PartitionKind.EfiSystem, DriveLetter = null };
        efi = efi with { Volume = efi.Volume! with { FileSystem = "FAT32" } };
        var windows = TestDisks.Partition(2, 5 * MiB, 40 * MiB, 'W') with { ContainsWindows = false };
        var recovery = TestDisks.Partition(3, 45 * MiB, 8 * MiB) with { Kind = PartitionKind.Recovery, DriveLetter = null };
        return TestDisks.Info(number, image, [efi, windows, recovery]);
    }
}

public class LayoutPlannerTests
{
    private static TargetLayout Plan(DiskInfo source, DiskInfo target, SizeMode mode, AlignmentMode align, out List<string> problems, out List<string> warnings)
    {
        problems = [];
        warnings = [];
        var image = SystemLikeDisk.Image();
        return LayoutPlanner.Plan(source, target, TestDisks.Table(image), mode, align, problems, warnings);
    }

    private static DiskInfo Target(long size, MediaKind media = MediaKind.Unknown) =>
        TestDisks.Info(2, new byte[size]) with { Media = media };

    [Fact]
    public void KeepingSizesOnAnHddChangesNothing()
    {
        var source = SystemLikeDisk.Info(SystemLikeDisk.Image());

        var layout = Plan(source, Target(128 * MiB), SizeMode.Keep, AlignmentMode.Auto, out var problems, out _);

        Assert.Empty(problems);
        Assert.False(layout.Changes);
    }

    [Fact]
    public void ExtendingTheMainPartitionPushesRecoveryToTheEnd()
    {
        var source = SystemLikeDisk.Info(SystemLikeDisk.Image());

        var layout = Plan(source, Target(128 * MiB), SizeMode.ExtendMain, AlignmentMode.Auto, out var problems, out _);

        Assert.Empty(problems);
        var efi = layout.Placements[0];
        var main = layout.Placements[1];
        var recovery = layout.Placements[2];
        Assert.False(efi.Moves || efi.Grows);
        Assert.Equal(5 * MiB, main.TargetOffset);
        // Libre en el destino: 128 MiB menos la GPT de respaldo, menos lo ocupado hasta el final de recuperación (53 MiB).
        Assert.Equal((40 + 74) * MiB, main.TargetSize);
        Assert.Equal(main.TargetEnd, recovery.TargetOffset);
        Assert.Equal(8 * MiB, recovery.TargetSize);
        var usableEnd = (128 * MiB) - (33 * Sector);
        Assert.InRange(recovery.TargetEnd, usableEnd - MiB, usableEnd);
        Assert.All(layout.Placements, p => Assert.Equal(0, p.TargetOffset % MiB));
    }

    [Fact]
    public void ProportionalGrowthFollowsTheSizeOfEachDataPartition()
    {
        var image = Gpt(64 * MiB, Guid.NewGuid(), (MiB, 10 * MiB), (11 * MiB, 30 * MiB));
        var source = TestDisks.Info(1, image, [TestDisks.Partition(1, MiB, 10 * MiB, 'D'), TestDisks.Partition(2, 11 * MiB, 30 * MiB, 'E')]);
        var problems = new List<string>();

        var layout = LayoutPlanner.Plan(source, Target(64 * MiB + 64 * MiB), TestDisks.Table(image), SizeMode.Proportional, AlignmentMode.Auto, problems, []);

        var small = layout.Placements[0].Growth;
        var large = layout.Placements[1].Growth;
        Assert.True(small > 0 && large > 0);
        Assert.InRange((double)large / small, 2.8, 3.2);
        Assert.Equal(layout.Placements[0].TargetEnd, layout.Placements[1].TargetOffset);
    }

    [Fact]
    public void MisalignedPartitionsAreAlignedOnAnSsd()
    {
        var image = Mbr(64 * MiB, 0xABCD0001, (63 * Sector, 10 * MiB), (63 * Sector + 10 * MiB, 10 * MiB));
        var source = TestDisks.Info(1, image, [TestDisks.Partition(1, 63 * Sector, 10 * MiB, 'D'), TestDisks.Partition(2, (63 * Sector) + (10 * MiB), 10 * MiB, 'E')]);
        var problems = new List<string>();

        var layout = LayoutPlanner.Plan(source, Target(64 * MiB, MediaKind.Ssd), TestDisks.Table(image), SizeMode.Keep, AlignmentMode.Auto, problems, []);

        Assert.Empty(problems);
        Assert.True(layout.Aligned);
        Assert.Equal(MiB, layout.Placements[0].TargetOffset);
        Assert.Equal(11 * MiB, layout.Placements[1].TargetOffset);
        Assert.All(layout.Placements, p => Assert.Equal(p.Partition.SizeBytes, p.TargetSize));
    }

    [Fact]
    public void AlignmentIsSkippedWhenItWouldNotFit()
    {
        var image = Mbr(16 * MiB, 0xABCD0002, (63 * Sector, (16 * MiB) - (63 * Sector)));
        var source = TestDisks.Info(1, image, [TestDisks.Partition(1, 63 * Sector, (16 * MiB) - (63 * Sector), 'D')]);
        var warnings = new List<string>();

        var layout = LayoutPlanner.Plan(source, Target(16 * MiB, MediaKind.Ssd), TestDisks.Table(image), SizeMode.Keep, AlignmentMode.Auto, [], warnings);

        Assert.False(layout.Changes);
        Assert.Contains(warnings, w => w.Contains("no cabrían", StringComparison.Ordinal));
    }

    [Fact]
    public void ForcedAlignmentThatDoesNotFitIsAProblem()
    {
        var image = Mbr(16 * MiB, 0xABCD0003, (63 * Sector, (16 * MiB) - (63 * Sector)));
        var source = TestDisks.Info(1, image, [TestDisks.Partition(1, 63 * Sector, (16 * MiB) - (63 * Sector), 'D')]);
        var problems = new List<string>();

        LayoutPlanner.Plan(source, Target(16 * MiB), TestDisks.Table(image), SizeMode.Keep, AlignmentMode.Always, problems, []);

        Assert.NotEmpty(problems);
    }

    [Fact]
    public void LogicalPartitionsCannotBeMoved()
    {
        var image = Mbr(32 * MiB, 0xABCD0004, (MiB, 20 * MiB));
        var extended = new PartitionInfo { Number = 1, OffsetBytes = MiB, SizeBytes = 20 * MiB, Kind = PartitionKind.Extended };
        var source = TestDisks.Info(1, image, [extended, TestDisks.Partition(5, 2 * MiB, 10 * MiB, 'L')]);
        var problems = new List<string>();

        LayoutPlanner.Plan(source, Target(64 * MiB), TestDisks.Table(image), SizeMode.ExtendMain, AlignmentMode.Auto, problems, []);

        Assert.Contains(problems, p => p.Contains("lógicas", StringComparison.Ordinal));
    }

    [Fact]
    public void WithoutNtfsDataPartitionsNothingGrows()
    {
        var image = Gpt(32 * MiB, Guid.NewGuid(), (MiB, 10 * MiB));
        var exfat = TestDisks.Partition(1, MiB, 10 * MiB, 'X');
        var source = TestDisks.Info(1, image, [exfat with { Volume = exfat.Volume! with { FileSystem = "exFAT" } }]);
        var warnings = new List<string>();

        var layout = LayoutPlanner.Plan(source, Target(64 * MiB), TestDisks.Table(image), SizeMode.ExtendMain, AlignmentMode.Auto, [], warnings);

        Assert.False(layout.Changes);
        Assert.Contains(warnings, w => w.Contains("NTFS", StringComparison.Ordinal));
    }
}

public class TableBuilderTests
{
    [Fact]
    public void RebuiltGptKeepsEntriesAndMovesThem()
    {
        var image = SystemLikeDisk.Image();
        var source = SystemLikeDisk.Info(image);
        var table = TestDisks.Table(image);
        var layout = LayoutPlanner.Plan(source, TestDisks.Info(2, new byte[128 * MiB]), table, SizeMode.ExtendMain, AlignmentMode.Auto, [], []);
        var targetSectors = 128 * MiB / Sector;

        var built = TableBuilder.Build(image.AsSpan(0, PartitionTable.HeadBytes), table, layout, targetSectors, Sector, identity: null);

        var rebuilt = PartitionTable.Read(built.Head, Sector);
        Assert.Null(rebuilt.Defect);
        Assert.Equal(table.Identifier, rebuilt.Identifier);
        Assert.Equal(targetSectors - 1, rebuilt.Gpt!.AlternateLba);
        var entries = built.Head.AsSpan(2 * Sector);
        Assert.Equal(layout.Placements[2].TargetOffset / Sector, BinaryPrimitives.ReadInt64LittleEndian(entries[(2 * 128 + 32)..]));
        Assert.Equal((layout.Placements[1].TargetEnd / Sector) - 1, BinaryPrimitives.ReadInt64LittleEndian(entries[(128 + 40)..]));
        Assert.True(entries[..16].SequenceEqual(image.AsSpan(2 * Sector, 16)), "El tipo de partición se conserva.");

        var backup = GptHeader.TryParse(built.Backup.AsSpan(built.Backup!.Length - Sector));
        Assert.NotNull(backup);
        Assert.Equal(targetSectors - 1, backup.MyLba);
        Assert.True(backup.EntriesMatch(built.Backup.AsSpan(0, 32 * Sector)));
    }

    [Fact]
    public void NewIdentityReplacesDiskAndPartitionGuids()
    {
        var image = SystemLikeDisk.Image();
        var source = SystemLikeDisk.Info(image);
        var table = TestDisks.Table(image);
        var identity = TableIdentity.CreateNew(PartitionStyle.Gpt, source.Partitions);

        var built = TableBuilder.Build(image.AsSpan(0, PartitionTable.HeadBytes), table, TargetLayout.Same(source.Partitions), 64 * MiB / Sector, Sector, identity);

        var rebuilt = PartitionTable.Read(built.Head, Sector);
        Assert.Null(rebuilt.Defect);
        Assert.Equal(identity.DiskGuid!.Value.ToString("D"), rebuilt.Identifier);
        Assert.Equal(identity.PartitionGuids[5 * MiB], new Guid(built.Head.AsSpan((2 * Sector) + 128 + 16, 16)));
    }

    [Fact]
    public void RebuiltMbrMovesEntriesAndCanChangeTheSignature()
    {
        var image = Mbr(64 * MiB, 0x01020304, (63 * Sector, 10 * MiB));
        var source = TestDisks.Info(1, image, [TestDisks.Partition(1, 63 * Sector, 10 * MiB, 'D')]);
        var layout = new TargetLayout { Placements = [new PartitionPlacement(source.Partitions[0], MiB, 20 * MiB)] };

        var built = TableBuilder.Build(image.AsSpan(0, PartitionTable.HeadBytes), TestDisks.Table(image), layout, 64 * MiB / Sector, Sector, new TableIdentity { MbrSignature = 0xCAFEBABE });

        Assert.Null(built.Backup);
        Assert.Equal(2048u, BinaryPrimitives.ReadUInt32LittleEndian(built.Head.AsSpan(446 + 8)));
        Assert.Equal((uint)(20 * MiB / Sector), BinaryPrimitives.ReadUInt32LittleEndian(built.Head.AsSpan(446 + 12)));
        Assert.Equal("CAFEBABE", PartitionTable.Read(built.Head, Sector).Identifier);
        Assert.True(built.Head.AsSpan(63 * Sector, (int)(MiB - (63 * Sector))).IndexOfAnyExcept((byte)0) < 0, "Los datos de la partición movida no se quedan en la cabeza.");
    }

    [Fact]
    public void PartitionUnknownToWindowsStopsTheRebuild()
    {
        var image = SystemLikeDisk.Image();
        var partial = new TargetLayout { Placements = [new PartitionPlacement(SystemLikeDisk.Info(image).Partitions[0], MiB, 4 * MiB)] };

        Assert.Throws<CloneException>(() =>
            TableBuilder.Build(image.AsSpan(0, PartitionTable.HeadBytes), TestDisks.Table(image), partial, 64 * MiB / Sector, Sector, null));
    }

    [Fact]
    public void HiddenSectorsAreUpdatedOnlyInNtfsAndFat32BootSectors()
    {
        var ntfs = BootSectorImage("NTFS    ", 3);
        var fat32 = BootSectorImage("FAT32   ", 0x52);
        var other = BootSectorImage("EXFAT   ", 3);

        Assert.True(BootSector.SetHiddenSectors(ntfs, 2048));
        Assert.True(BootSector.SetHiddenSectors(fat32, 4096));
        Assert.False(BootSector.SetHiddenSectors(other, 2048));
        Assert.Equal(2048u, BinaryPrimitives.ReadUInt32LittleEndian(ntfs.AsSpan(0x1C)));
        Assert.Equal(4096u, BinaryPrimitives.ReadUInt32LittleEndian(fat32.AsSpan(0x1C)));
        Assert.Equal(63u, BinaryPrimitives.ReadUInt32LittleEndian(other.AsSpan(0x1C)));
    }

    internal static byte[] BootSectorImage(string id, int idOffset)
    {
        var sector = new byte[Sector];
        Encoding.ASCII.GetBytes(id).CopyTo(sector, idOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(sector.AsSpan(0x1C), 63);
        sector[510] = 0x55;
        sector[511] = 0xAA;
        return sector;
    }
}

public class MountedDevicesTests
{
    [Fact]
    public void GptValuesFollowTheNewPartitionGuids()
    {
        var image = SystemLikeDisk.Image();
        var source = SystemLikeDisk.Info(image);
        var identity = TableIdentity.CreateNew(PartitionStyle.Gpt, source.Partitions);

        var map = MountedDevices.Map(image.AsSpan(0, PartitionTable.HeadBytes), TestDisks.Table(image), TargetLayout.Same(source.Partitions), identity, Sector);

        Assert.Equal(3, map.Count);
        var oldGuid = new Guid(image.AsSpan((2 * Sector) + 128 + 16, 16));
        Assert.Contains(map, pair => pair.Old.AsSpan().SequenceEqual(MountedDevices.GptValue(oldGuid))
            && pair.New.AsSpan().SequenceEqual(MountedDevices.GptValue(identity.PartitionGuids[5 * MiB])));
    }

    [Fact]
    public void SameGptIdentityNeedsNoTranslation()
    {
        var image = SystemLikeDisk.Image();
        var source = SystemLikeDisk.Info(image);

        Assert.Empty(MountedDevices.Map(image.AsSpan(0, PartitionTable.HeadBytes), TestDisks.Table(image), TargetLayout.Same(source.Partitions), null, Sector));
    }

    [Fact]
    public void MbrValuesFollowTheNewOffsets()
    {
        var image = Mbr(64 * MiB, 0x0A0B0C0D, (63 * Sector, 10 * MiB));
        var source = TestDisks.Info(1, image, [TestDisks.Partition(1, 63 * Sector, 10 * MiB, 'C')]);
        var layout = new TargetLayout { Placements = [new PartitionPlacement(source.Partitions[0], MiB, 10 * MiB)] };

        var map = MountedDevices.Map(image.AsSpan(0, PartitionTable.HeadBytes), TestDisks.Table(image), layout, null, Sector);

        var pair = Assert.Single(map);
        Assert.Equal(MountedDevices.MbrValue(0x0A0B0C0D, 63 * Sector), pair.Old);
        Assert.Equal(MountedDevices.MbrValue(0x0A0B0C0D, MiB), pair.New);
    }

    [Fact]
    public void OnlyValuesOfTheClonedPartitionsAreTranslated()
    {
        var oldValue = MountedDevices.MbrValue(1, 100);
        var newValue = MountedDevices.MbrValue(1, 200);
        var other = MountedDevices.MbrValue(9, 100);
        var values = new Dictionary<string, byte[]> { [@"\DosDevices\C:"] = oldValue, [@"\DosDevices\D:"] = other };

        var changed = MountedDevices.Translate(values, [(oldValue, newValue)]);

        Assert.Equal(1, changed);
        Assert.Equal(newValue, values[@"\DosDevices\C:"]);
        Assert.Equal(other, values[@"\DosDevices\D:"]);
    }
}

public class LayoutExecutorTests
{
    private sealed class Setup
    {
        public Setup(CloneOptions options, long targetSize = 128 * MiB, MediaKind targetMedia = MediaKind.Unknown, bool mbrMisaligned = false)
        {
            if (mbrMisaligned)
            {
                SourceImage = Mbr(32 * MiB, 0x55667788, (63 * Sector, 10 * MiB));
                TableBuilderTests.BootSectorImage("NTFS    ", 3).CopyTo(SourceImage, 63 * Sector);
                Source = TestDisks.Info(1, SourceImage, [TestDisks.Partition(1, 63 * Sector, 10 * MiB, 'D')]);
            }
            else
            {
                SourceImage = SystemLikeDisk.Image();
                Source = SystemLikeDisk.Info(SourceImage);
            }

            OriginalSource = (byte[])SourceImage.Clone();
            TargetImage = Garbage(targetSize);
            var target = TestDisks.Info(2, TargetImage) with { Media = targetMedia };
            var result = ClonePlanner.Create(Source, target, TestDisks.Table(SourceImage), new CloneEnvironment(), options);
            Plan = result.Plan ?? throw new InvalidOperationException(string.Join(" ", result.Problems));

            Access.Disks[1] = new MemoryDisk(SourceImage);
            Access.Disks[2] = Target = new MemoryDisk(TargetImage);
        }

        public byte[] SourceImage { get; }
        public byte[] OriginalSource { get; }
        public byte[] TargetImage { get; }
        public DiskInfo Source { get; }
        public ClonePlan Plan { get; }
        public MemoryDisk Target { get; }
        public FakeDiskAccess Access { get; } = new();

        public CloneResult Run() => new CloneExecutor(Access, NullLogger.Instance).Execute(Plan, null, CancellationToken.None);
    }

    private static readonly CloneOptions Extend = new() { Mode = CloneMode.SectorBySector, Sizing = SizeMode.ExtendMain };

    [Fact]
    public void ExtendedCloneHasPartitionsAtTheirNewPlaceAndTheSourceIdentity()
    {
        var setup = new Setup(Extend);

        var result = setup.Run();

        Assert.True(result.Outcome == CloneOutcome.Succeeded, result.Message);
        Assert.True(result.Verified);
        foreach (var placement in setup.Plan.Layout.Placements)
        {
            var copied = setup.TargetImage.AsSpan((int)placement.TargetOffset, (int)placement.Partition.SizeBytes);
            Assert.True(copied.SequenceEqual(setup.OriginalSource.AsSpan((int)placement.Partition.OffsetBytes, (int)placement.Partition.SizeBytes)));
        }

        // Al final quedan los identificadores del origen y la tabla nueva.
        var table = PartitionTable.Read(setup.TargetImage.AsSpan(0, PartitionTable.HeadBytes), Sector);
        Assert.Null(table.Defect);
        Assert.Equal(SystemLikeDisk.DiskGuid.ToString("D"), table.Identifier);
        var backup = GptHeader.TryParse(setup.TargetImage.AsSpan(setup.TargetImage.Length - Sector, Sector));
        Assert.NotNull(backup);
        Assert.Equal(SystemLikeDisk.DiskGuid, backup.DiskGuid);
    }

    [Fact]
    public void GrownNtfsPartitionIsExtendedThroughTheFinisher()
    {
        var setup = new Setup(Extend);

        setup.Run();

        var main = setup.Plan.Layout.Placements.Single(p => p.Grows);
        var extended = Assert.Single(setup.Access.FakeFinisher.Extended);
        Assert.Equal(FakeFinisher.VolumeAt(main.TargetOffset), extended.Volume);
        Assert.Equal(main.TargetSize, extended.Bytes);
        Assert.Equal(1, setup.Access.FakeFinisher.QuietWaits);
        Assert.Empty(setup.Access.FakeFinisher.Repairs);
    }

    [Fact]
    public void NewIdentifiersNeedNoTemporaryTableAndRepairWindowsBoot()
    {
        var setup = new Setup(new CloneOptions { Mode = CloneMode.SectorBySector, NewIdentifiers = true });
        setup.Access.FakeFinisher.WindowsAt = 5 * MiB;

        var result = setup.Run();

        Assert.True(result.Outcome == CloneOutcome.Succeeded, result.Message);
        var table = PartitionTable.Read(setup.TargetImage.AsSpan(0, PartitionTable.HeadBytes), Sector);
        Assert.Equal(setup.Plan.NewIdentity!.DiskGuid!.Value.ToString("D"), table.Identifier);
        Assert.Equal(0, setup.Access.FakeFinisher.QuietWaits);
        var repair = Assert.Single(setup.Access.FakeFinisher.Repairs);
        Assert.True(repair.Uefi);
        Assert.Equal(FakeFinisher.VolumeAt(5 * MiB), repair.WindowsVolume);
        Assert.Equal(FakeFinisher.VolumeAt(MiB), repair.SystemVolume);
        Assert.Equal(3, repair.MountedDevicesMap.Count);
    }

    [Fact]
    public void NewIdentifiersOnADataDiskDoNotTouchBoot()
    {
        var setup = new Setup(new CloneOptions { Mode = CloneMode.SectorBySector, NewIdentifiers = true });

        setup.Run();

        Assert.Empty(setup.Access.FakeFinisher.Repairs);
        Assert.NotEmpty(setup.Access.FakeFinisher.WaitedOffsets);
    }

    [Fact]
    public void MovedPartitionGetsItsNewStartInTheBootSector()
    {
        var setup = new Setup(new CloneOptions { Mode = CloneMode.SectorBySector }, targetSize: 32 * MiB, targetMedia: MediaKind.Ssd, mbrMisaligned: true);

        var result = setup.Run();

        Assert.True(result.Outcome == CloneOutcome.Succeeded, result.Message);
        Assert.True(setup.Plan.Layout.Aligned);
        var bootSector = setup.TargetImage.AsSpan((int)MiB, Sector);
        Assert.Equal(2048u, BinaryPrimitives.ReadUInt32LittleEndian(bootSector[0x1C..]));
        Assert.Equal(2048u, BinaryPrimitives.ReadUInt32LittleEndian(setup.TargetImage.AsSpan(446 + 8)));
        Assert.Equal("55667788", PartitionTable.Read(setup.TargetImage.AsSpan(0, PartitionTable.HeadBytes), Sector).Identifier);
    }

    [Fact]
    public void PartitionInsideTheFirstMebibyteIsCopiedOnceFromItsSnapshot()
    {
        // Como en los VHD pequeños de diskpart: la partición empieza a los 64 KiB.
        const long start = 64 * 1024;
        var sourceImage = Gpt(16 * MiB, Guid.NewGuid(), (start, 12 * MiB));
        var partition = TestDisks.Partition(1, start, 12 * MiB, 'E');
        var source = TestDisks.Info(1, sourceImage, [partition]);
        var targetImage = Garbage(16 * MiB);
        var options = new CloneOptions { Mode = CloneMode.SectorBySector, NewIdentifiers = true };
        var plan = ClonePlanner.Create(source, TestDisks.Info(2, targetImage), TestDisks.Table(sourceImage), new CloneEnvironment { SnapshotsAvailable = true }, options).Plan!;

        var access = new FakeDiskAccess();
        access.Disks[1] = new MemoryDisk(sourceImage);
        access.Disks[2] = new MemoryDisk(targetImage);
        access.Volumes[partition.Volume!.Path] = (1, start, 12 * MiB, null);
        var frozen = sourceImage.AsSpan((int)start, (int)(12 * MiB)).ToArray();
        access.AfterSnapshot = () => sourceImage.AsSpan((int)start, (int)(12 * MiB)).Fill(0x5A);

        var result = new CloneExecutor(access, NullLogger.Instance).Execute(plan, null, CancellationToken.None);

        Assert.True(result.Outcome == CloneOutcome.Succeeded, result.Message);
        Assert.True(result.Verified);
        Assert.True(targetImage.AsSpan((int)start, (int)(12 * MiB)).SequenceEqual(frozen));
        Assert.Equal(plan.NewIdentity!.DiskGuid!.Value.ToString("D"), PartitionTable.Read(targetImage.AsSpan(0, PartitionTable.HeadBytes), Sector).Identifier);
        Assert.DoesNotContain(plan.Steps, s => s.Kind == CloneStepKind.CopyPartition && s.Offset < PartitionTable.HeadBytes);
    }

    [Fact]
    public void PlanDescribesGrowthAndAlignment()
    {
        var setup = new Setup(Extend);

        Assert.True(setup.Plan.RebuildsTable);
        Assert.True(setup.Plan.NeedsFinishing);
        Assert.Contains(setup.Plan.Warnings, w => w.Contains("se amplía al terminar", StringComparison.Ordinal));
        Assert.DoesNotContain(setup.Plan.Warnings, w => w.Contains("quedan sin asignar", StringComparison.Ordinal));
        Assert.Equal(CloneStepKind.WritePartitionTable, setup.Plan.Steps[^1].Kind);
        Assert.Contains(setup.Plan.Steps, s => s.Kind == CloneStepKind.CopyPartition && s.Moves);
    }
}
