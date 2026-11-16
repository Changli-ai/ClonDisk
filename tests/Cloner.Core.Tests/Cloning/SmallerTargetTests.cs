using Cloner.Core.Cloning;
using Cloner.Core.Inventory;
using Cloner.Core.Layout;
using Cloner.Core.PartitionTables;
using Microsoft.Extensions.Logging.Abstractions;
using static Cloner.Core.Tests.Cloning.DiskImages;

namespace Cloner.Core.Tests.Cloning;

/// <summary>Destino más pequeño que el origen (RF-14).</summary>
public class SmallerTargetTests
{
    private static readonly CloneEnvironment WithSnapshots = new() { SnapshotsAvailable = true };

    /// <summary>Origen de 48 MiB: datos NTFS de 39 MiB (la mitad en uso) y otra FAT32 de 6 MiB detrás.</summary>
    private static (DiskInfo Disk, PartitionTableInfo Table, byte[] Image) Source()
    {
        var image = Gpt(48 * MiB, Guid.NewGuid(), (MiB, 39 * MiB), (40 * MiB, 6 * MiB));
        var data = TestDisks.Partition(1, MiB, 39 * MiB, 'E', "Datos");
        var other = TestDisks.Partition(2, 40 * MiB, 6 * MiB, 'F', "Otra");
        other = other with { Volume = other.Volume! with { FileSystem = "FAT32" } };
        return (TestDisks.Info(1, image, [data, other]), TestDisks.Table(image), image);
    }

    private static DiskInfo Target(long size) => TestDisks.Info(2, Garbage(size));

    private static ClonePlanResult Plan(long targetSize, CloneOptions? options = null)
    {
        var (source, table, _) = Source();
        return ClonePlanner.Create(source, Target(targetSize), table, WithSnapshots, options ?? new CloneOptions());
    }

    [Fact]
    public void PartitionsThatFitAreClonedWithoutShrinking()
    {
        var image = Gpt(12 * MiB, Guid.NewGuid(), (MiB, 6 * MiB));
        var source = TestDisks.Info(1, image, [TestDisks.Partition(1, MiB, 6 * MiB, 'E')]);

        var result = ClonePlanner.Create(source, Target(8 * MiB), TestDisks.Table(image), WithSnapshots, new CloneOptions());

        var plan = Assert.IsType<ClonePlan>(result.Plan);
        Assert.True(plan.Layout.FitsSmallerTarget);
        Assert.DoesNotContain(plan.Layout.Placements, p => p.Shrinks);
        Assert.Empty(plan.FileSteps);
        Assert.All(plan.Steps, s => Assert.True(s.End <= 8 * MiB));
        var backup = Assert.Single(plan.Steps, s => s.Kind == CloneStepKind.WriteBackupGpt);
        Assert.Equal(8 * MiB, backup.End);
        Assert.Contains(result.Warnings, w => w.Contains("caben sin reducirlas", StringComparison.Ordinal));
    }

    [Fact]
    public void NtfsDataPartitionShrinksAndIsCopiedFileByFile()
    {
        var result = Plan(44 * MiB);

        var plan = Assert.IsType<ClonePlan>(result.Plan);
        var data = plan.Layout.Placements[0];
        var other = plan.Layout.Placements[1];
        Assert.Equal(MiB, data.TargetOffset);
        Assert.Equal(36 * MiB, data.TargetSize);
        Assert.True(data.Shrinks);
        Assert.Equal(37 * MiB, other.TargetOffset);
        Assert.Equal(6 * MiB, other.TargetSize);
        Assert.True(plan.NeedsFinishing);

        var files = Assert.Single(plan.FileSteps);
        Assert.Equal(MiB, files.Offset);
        Assert.Equal(36 * MiB, files.Length);
        Assert.True(files.FromSnapshot);
        Assert.Contains(plan.SnapshotPartitions, p => p.Number == 1);
        Assert.Contains(plan.Steps, s => s.Kind == CloneStepKind.Clear && s.Offset == MiB && s.Length == MiB);

        // Ningún paso por bloques escribe la partición reducida, y nada queda fuera del destino.
        Assert.DoesNotContain(plan.Steps, s => s.Kind == CloneStepKind.CopyPartition && s.Partition!.Number == 1);
        Assert.All(plan.Steps, s => Assert.True(s.End <= 44 * MiB));
        Assert.Contains(result.Warnings, w => w.Contains("archivos se copian uno a uno", StringComparison.Ordinal));
    }

    [Fact]
    public void ShrinkingNeverGoesBelowUsedSpacePlusMargin()
    {
        var partition = TestDisks.Partition(1, MiB, 39 * MiB, 'E');

        Assert.Equal(RoundUpMiB(partition.Volume!.UsedBytes + LayoutPlanner.ShrinkMargin), LayoutPlanner.MinimumSize(partition));
    }

    [Fact]
    public void DataThatDoesNotFitIsRejectedWithTheSizeNeeded()
    {
        var result = Plan(32 * MiB);

        Assert.Null(result.Plan);
        var problem = Assert.Single(result.Problems);
        Assert.Contains("Los datos no caben", problem, StringComparison.Ordinal);
        Assert.Contains("al menos", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutShrinkablePartitionsTheMessageSaysSo()
    {
        var image = Gpt(48 * MiB, Guid.NewGuid(), (MiB, 46 * MiB));
        var fat = TestDisks.Partition(1, MiB, 46 * MiB, 'E');
        fat = fat with { Volume = fat.Volume! with { FileSystem = "FAT32" } };
        var source = TestDisks.Info(1, image, [fat]);

        var result = ClonePlanner.Create(source, Target(32 * MiB), TestDisks.Table(image), WithSnapshots, new CloneOptions());

        Assert.Null(result.Plan);
        Assert.Contains(result.Problems, p => p.Contains("no tiene particiones NTFS de datos", StringComparison.Ordinal));
    }

    [Fact]
    public void SectorBySectorCannotShrink()
    {
        var result = Plan(44 * MiB, new CloneOptions { Mode = CloneMode.SectorBySector });

        Assert.Null(result.Plan);
        Assert.Contains(result.Problems, p => p.Contains("sector a sector", StringComparison.Ordinal));
    }

    [Fact]
    public void SlightlySmallerTargetWorksSectorBySectorWhenPartitionsFit()
    {
        var (source, table, _) = Source();
        var target = Target(48 * MiB) with { SizeBytes = (48 * MiB) - 512 };

        var result = ClonePlanner.Create(source, target, table, WithSnapshots, new CloneOptions { Mode = CloneMode.SectorBySector });

        // Reducir no se puede sector a sector, pero las particiones caben (el final del origen está libre).
        Assert.NotNull(result.Plan);
    }

    [Fact]
    public void ExecutorFormatsCopiesFilesAndLabelsTheShrunkVolume()
    {
        var setup = new ShrinkSetup();

        var result = setup.Run();

        Assert.Equal(CloneOutcome.Succeeded, result.Outcome);
        Assert.True(result.TargetComplete);
        var finisher = setup.Access.FakeFinisher;
        var volume = FakeFinisher.VolumeAt(MiB);
        Assert.Equal([(volume, CloneExecutor.IncompleteLabel)], finisher.Formatted);
        var copy = Assert.Single(finisher.FileCopies);
        Assert.Contains("Instantanea", copy.Source, StringComparison.Ordinal);
        Assert.Equal(volume, copy.Target);
        Assert.Equal([(volume, "Datos")], finisher.Labels);

        // La partición que va detrás se copia por bloques a su nueva posición.
        Assert.True(setup.TargetImage.AsSpan((int)(37 * MiB), (int)(6 * MiB)).SequenceEqual(setup.SourceImage.AsSpan((int)(40 * MiB), (int)(6 * MiB))));

        // Del sistema de archivos del origen no queda nada al principio de la partición reducida.
        Assert.True(setup.TargetImage.AsSpan((int)MiB, (int)MiB).IndexOfAnyExcept((byte)0) < 0);

        // La instantánea sigue viva durante la copia por archivos y se completa al final.
        var snapshot = Assert.Single(setup.Access.Snapshots);
        Assert.True(snapshot.Completed);
        Assert.True(snapshot.Disposed);

        // Tabla definitiva con el identificador del origen y la partición reducida.
        var table = TestDisks.Table(setup.TargetImage);
        Assert.Equal(setup.SourceTable.Identifier, table.Identifier);
        Assert.Equal(1, finisher.QuietWaits);
    }

    [Fact]
    public void FileCopyFailureLeavesTheTargetWithoutTable()
    {
        var setup = new ShrinkSetup();
        setup.Access.FakeFinisher.FileCopyFailure = new IOException("Disco lleno");

        var result = setup.Run();

        Assert.Equal(CloneOutcome.Failed, result.Outcome);
        Assert.False(result.TargetComplete);
        Assert.Contains("sin tabla", result.Message, StringComparison.Ordinal);
        Assert.True(setup.TargetImage.AsSpan(0, (int)MiB).IndexOfAnyExcept((byte)0) < 0);
        Assert.Contains(setup.Access.Locks, l => l.Dismount && l.Volumes.Any(v => v.Path == FakeFinisher.VolumeAt(MiB)));
    }

    [Fact]
    public void FileErrorsAreReportedInTheResult()
    {
        var setup = new ShrinkSetup();
        setup.Access.FakeFinisher.FileCopyErrors = [@"\bloqueado.txt: acceso denegado"];

        var result = setup.Run();

        Assert.Equal(CloneOutcome.Succeeded, result.Outcome);
        Assert.Contains("no se pudieron copiar", result.Message, StringComparison.Ordinal);
    }

    private static long RoundUpMiB(long value) => (value + MiB - 1) / MiB * MiB;

    private sealed class ShrinkSetup
    {
        public ShrinkSetup()
        {
            var (source, table, image) = Source();
            SourceImage = image;
            SourceTable = table;
            TargetImage = Garbage(44 * MiB);
            var result = ClonePlanner.Create(source, TestDisks.Info(2, TargetImage), table, WithSnapshots, new CloneOptions());
            Plan = result.Plan ?? throw new InvalidOperationException(string.Join(" ", result.Problems));
            Access.Disks[1] = new MemoryDisk(SourceImage);
            Access.Disks[2] = new MemoryDisk(TargetImage);
            foreach (var partition in source.Partitions)
            {
                Access.Volumes[partition.Volume!.Path] = (1, partition.OffsetBytes, partition.SizeBytes, null);
            }
        }

        public byte[] SourceImage { get; }
        public PartitionTableInfo SourceTable { get; }
        public byte[] TargetImage { get; }
        public ClonePlan Plan { get; }
        public FakeDiskAccess Access { get; } = new();

        public CloneResult Run() => new CloneExecutor(Access, NullLogger.Instance).Execute(Plan, null, CancellationToken.None);
    }
}
