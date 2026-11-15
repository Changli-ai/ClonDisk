using Cloner.Core.Cloning;
using Cloner.Core.Devices;
using Cloner.Core.Inventory;
using Cloner.Core.PartitionTables;
using Microsoft.Extensions.Logging.Abstractions;
using static Cloner.Core.Tests.Cloning.DiskImages;

namespace Cloner.Core.Tests.Cloning;

public class CloneExecutorTests
{
    private static readonly Guid SourceGuid = new("aaaaaaaa-1111-2222-3333-444444444444");
    private static readonly Guid TargetGuid = new("bbbbbbbb-1111-2222-3333-444444444444");

    /// <summary>Origen GPT de 8 MiB y destino GPT con otra tabla, ambos en memoria.</summary>
    private sealed class Setup
    {
        public Setup(
            long targetSize = 8 * MiB,
            bool mbrSource = false,
            bool verify = true,
            CloneMode mode = CloneMode.SectorBySector,
            bool snapshots = false,
            IReadOnlyList<ByteRange>? usedRanges = null,
            bool efi = false)
        {
            SourceImage = mbrSource
                ? Mbr(8 * MiB, 0x12345678, (MiB, 6 * MiB))
                : Gpt(8 * MiB, SourceGuid, (MiB, 6 * MiB));
            OriginalSource = (byte[])SourceImage.Clone();
            TargetImage = Gpt(targetSize, TargetGuid, (MiB, 2 * MiB));
            OriginalTarget = (byte[])TargetImage.Clone();

            var partition = TestDisks.Partition(1, MiB, 6 * MiB, 'E', "Datos");
            if (efi)
            {
                partition = partition with { Kind = PartitionKind.EfiSystem, Volume = partition.Volume! with { FileSystem = "FAT32" } };
            }

            var source = TestDisks.Info(1, SourceImage, [partition]);
            var target = TestDisks.Info(2, TargetImage, [TestDisks.Partition(1, MiB, 2 * MiB, 'F')]);
            var environment = new CloneEnvironment { SnapshotsAvailable = snapshots };
            var options = new CloneOptions { Verify = verify, Mode = mode };
            var result = ClonePlanner.Create(source, target, TestDisks.Table(SourceImage), environment, options);
            Plan = result.Plan ?? throw new InvalidOperationException(string.Join(" ", result.Problems));

            Source = new MemoryDisk(SourceImage);
            Target = new MemoryDisk(TargetImage);
            Access.Disks[1] = Source;
            Access.Disks[2] = Target;
            SourceVolume = source.Partitions[0].Volume!.Path;
            Access.Volumes[SourceVolume] = (1, MiB, 6 * MiB, usedRanges);
        }

        public byte[] SourceImage { get; }
        public byte[] OriginalSource { get; }
        public byte[] TargetImage { get; }
        public byte[] OriginalTarget { get; }
        public ClonePlan Plan { get; }
        public string SourceVolume { get; }
        public MemoryDisk Source { get; }
        public MemoryDisk Target { get; }
        public FakeDiskAccess Access { get; } = new();
        public ProgressLog Progress { get; } = new();

        public CloneResult Run(CancellationToken cancellation = default) =>
            new CloneExecutor(Access, NullLogger.Instance).Execute(Plan, Progress, cancellation);
    }

    [Fact]
    public void EqualSizeCloneIsIdenticalAndVerified()
    {
        var setup = new Setup();

        var result = setup.Run();

        Assert.Equal(CloneOutcome.Succeeded, result.Outcome);
        Assert.True(result.Verified);
        Assert.True(result.TargetComplete);
        Assert.Equal(8 * MiB, result.BytesWritten);
        Assert.NotNull(result.DataHash);
        Assert.Equal(setup.OriginalSource, setup.TargetImage);
        Assert.True(setup.Target.LayoutRefreshes > 0);
    }

    [Fact]
    public void SourceIsOpenedReadOnlyAndNeverWritten()
    {
        var setup = new Setup();

        setup.Run();

        Assert.Contains((1, false), setup.Access.Opened);
        Assert.DoesNotContain((1, true), setup.Access.Opened);
        Assert.Empty(setup.Source.Writes);
        Assert.Equal(setup.OriginalSource, setup.SourceImage);
    }

    [Fact]
    public void PartitionTableIsClearedFirstAndWrittenLast()
    {
        var setup = new Setup();

        setup.Run();

        var writes = setup.Target.Writes;
        Assert.Equal(0, writes[0].Offset);
        Assert.Equal(0, writes[^1].Offset);
        var headWrites = writes.Where(w => w.Offset < PartitionTable.HeadBytes).ToList();
        Assert.Equal(2, headWrites.Count);
    }

    [Fact]
    public void VolumesAreLockedAndOnlyTargetVolumesAreDismounted()
    {
        var setup = new Setup();

        setup.Run();

        Assert.Contains(setup.Access.Locks, l => !l.Dismount && l.Volumes.Single().Name == "E:");
        Assert.Contains(setup.Access.Locks, l => l.Dismount && l.Volumes.Single().Name == "F:");
    }

    [Fact]
    public void LargerTargetGetsValidGptAtBothEnds()
    {
        var setup = new Setup(targetSize: 12 * MiB);

        var result = setup.Run();

        Assert.Equal(CloneOutcome.Succeeded, result.Outcome);
        var target = setup.TargetImage;
        var targetSectors = 12 * MiB / Sector;

        var primary = PartitionTable.Read(target.AsSpan(0, PartitionTable.HeadBytes), Sector);
        Assert.Null(primary.Defect);
        Assert.Equal(SourceGuid.ToString("D"), primary.Identifier);
        Assert.Equal(targetSectors - 1, primary.Gpt!.AlternateLba);

        var backup = GptHeader.TryParse(target.AsSpan((int)((targetSectors - 1) * Sector), Sector));
        Assert.NotNull(backup);
        Assert.Equal(targetSectors - 1, backup.MyLba);
        Assert.True(backup.EntriesMatch(target.AsSpan((int)(backup.PartitionEntryLba * Sector))));

        // Los datos son idénticos y la copia de respaldo antigua no queda en mitad del disco.
        var oldBackupStart = (int)((8 * MiB) - (33 * Sector));
        Assert.True(target.AsSpan(PartitionTable.HeadBytes, oldBackupStart - PartitionTable.HeadBytes)
            .SequenceEqual(setup.OriginalSource.AsSpan(PartitionTable.HeadBytes, oldBackupStart - PartitionTable.HeadBytes)));
        Assert.True(target.AsSpan(oldBackupStart, 33 * Sector).IndexOfAnyExcept((byte)0) < 0);
    }

    [Fact]
    public void LargerMbrTargetStartsWithAnExactCopy()
    {
        var setup = new Setup(targetSize: 12 * MiB, mbrSource: true);

        var result = setup.Run();

        Assert.Equal(CloneOutcome.Succeeded, result.Outcome);
        Assert.True(setup.TargetImage.AsSpan(0, (int)(8 * MiB)).SequenceEqual(setup.OriginalSource));

        // La GPT de respaldo que tenía el destino al final se ha borrado.
        Assert.True(setup.TargetImage.AsSpan((int)(11 * MiB)).IndexOfAnyExcept((byte)0) < 0);
    }

    [Fact]
    public void CancelledCloneLeavesTheTargetWithoutPartitionTable()
    {
        var setup = new Setup();
        using var cancellation = new CancellationTokenSource();
        setup.Target.AfterWrite = offset =>
        {
            if (offset >= PartitionTable.HeadBytes + (2 * MiB))
            {
                cancellation.Cancel();
            }
        };

        var result = setup.Run(cancellation.Token);

        Assert.Equal(CloneOutcome.Cancelled, result.Outcome);
        Assert.True(result.TargetModified);
        Assert.False(result.TargetComplete);
        Assert.Contains("incompleto", result.Message, StringComparison.Ordinal);
        Assert.Equal(PartitionStyle.Raw, TestDisks.Table(setup.TargetImage).Style);
        Assert.True(setup.Target.LayoutRefreshes > 0);
    }

    [Fact]
    public void ReadErrorStopsTheCloneAndLeavesNoValidTable()
    {
        var setup = new Setup();
        setup.Source.ReadFault = offset => offset == 5 * MiB ? new IOException("Error de E/S en el sector 10240.") : null;

        var result = setup.Run();

        Assert.Equal(CloneOutcome.Failed, result.Outcome);
        Assert.Contains("sector 10240", result.Message, StringComparison.Ordinal);
        Assert.Contains("incompleto", result.Message, StringComparison.Ordinal);
        Assert.Equal(PartitionStyle.Raw, TestDisks.Table(setup.TargetImage).Style);
    }

    [Fact]
    public void WriteErrorStopsTheClone()
    {
        var setup = new Setup();
        setup.Target.AfterWrite = offset =>
        {
            if (offset == 5 * MiB)
            {
                throw new IOException("El dispositivo no está listo.");
            }
        };

        var result = setup.Run();

        Assert.Equal(CloneOutcome.Failed, result.Outcome);
        Assert.Contains("no está listo", result.Message, StringComparison.Ordinal);
        Assert.Equal(PartitionStyle.Raw, TestDisks.Table(setup.TargetImage).Style);
    }

    [Fact]
    public void VerificationDetectsADifferenceAndInvalidatesTheTarget()
    {
        var setup = new Setup();
        setup.Target.CorruptReads.Add(5 * MiB);

        var result = setup.Run();

        Assert.Equal(CloneOutcome.VerificationFailed, result.Outcome);
        Assert.False(result.Verified);
        Assert.False(result.TargetComplete);
        Assert.Contains("5.242.880", result.Message, StringComparison.Ordinal);
        Assert.Equal(PartitionStyle.Raw, TestDisks.Table(setup.TargetImage).Style);
    }

    [Fact]
    public void WithoutVerificationTheTargetIsNotReadBack()
    {
        var setup = new Setup(verify: false);
        setup.Target.CorruptReads.Add(5 * MiB);

        var result = setup.Run();

        Assert.Equal(CloneOutcome.Succeeded, result.Outcome);
        Assert.False(result.Verified);
    }

    [Fact]
    public void LockedTargetVolumeMeansNothingIsWritten()
    {
        var setup = new Setup();
        setup.Access.LockFailure = "El volumen F: está en uso.";

        var result = setup.Run();

        Assert.Equal(CloneOutcome.Failed, result.Outcome);
        Assert.False(result.TargetModified);
        Assert.Contains("No se ha escrito nada", result.Message, StringComparison.Ordinal);
        Assert.Empty(setup.Target.Writes);
        Assert.Equal(setup.OriginalTarget, setup.TargetImage);
    }

    [Fact]
    public void TargetWithAnotherTableThanPlannedIsNotTouched()
    {
        var setup = new Setup();
        var replacement = Gpt(8 * MiB, Guid.NewGuid(), (MiB, MiB));
        replacement.CopyTo(setup.TargetImage, 0);

        var result = setup.Run();

        Assert.Equal(CloneOutcome.Failed, result.Outcome);
        Assert.Contains("ya no es el que se eligió", result.Message, StringComparison.Ordinal);
        Assert.Empty(setup.Target.Writes);
    }

    [Fact]
    public void SourceTableChangedSincePlanningAbortsBeforeWriting()
    {
        var setup = new Setup();
        Gpt(8 * MiB, Guid.NewGuid(), (MiB, 6 * MiB)).AsSpan(0, PartitionTable.HeadBytes).CopyTo(setup.SourceImage);

        var result = setup.Run();

        Assert.Equal(CloneOutcome.Failed, result.Outcome);
        Assert.Contains("ha cambiado", result.Message, StringComparison.Ordinal);
        Assert.Empty(setup.Target.Writes);
    }

    [Fact]
    public void CancelBeforeStartingWritesNothing()
    {
        var setup = new Setup();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = setup.Run(cancellation.Token);

        Assert.Equal(CloneOutcome.Cancelled, result.Outcome);
        Assert.False(result.TargetModified);
        Assert.Empty(setup.Target.Writes);
    }

    [Fact]
    public void ProgressEndsWithEverythingDone()
    {
        var setup = new Setup();

        setup.Run();

        var last = setup.Progress.Reports[^1];
        Assert.Equal(ClonePhase.Finishing, last.Phase);
        Assert.Equal(last.BytesTotal, last.BytesDone);
        Assert.Equal(1.0, last.Fraction);
        Assert.Equal(ClonePhase.Preparing, setup.Progress.Reports[0].Phase);
    }
}
