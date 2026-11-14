using Cloner.Core.Cloning;
using Cloner.Core.Inventory;
using Cloner.Core.PartitionTables;
using static Cloner.Core.Tests.Cloning.DiskImages;

namespace Cloner.Core.Tests.Cloning;

public class ClonePlannerTests
{
    private static readonly Guid SourceGuid = new("11111111-2222-3333-4444-555555555555");
    private static readonly CloneEnvironment NoHost = new();

    private static (DiskInfo Disk, PartitionTableInfo Table) GptSource(long size = 8 * MiB, int number = 1)
    {
        var image = Gpt(size, SourceGuid, (MiB, 6 * MiB));
        var disk = TestDisks.Info(number, image, [TestDisks.Partition(1, MiB, 6 * MiB, 'E', "Datos")]);
        return (disk, TestDisks.Table(image));
    }

    private static DiskInfo Target(long size = 8 * MiB, int number = 2) =>
        TestDisks.Info(number, Garbage(size), [TestDisks.Partition(1, MiB, MiB, 'F', "Viejo")]);

    private static ClonePlanResult Plan(DiskInfo source, DiskInfo target, PartitionTableInfo? table, CloneEnvironment? environment = null, CloneOptions? options = null) =>
        ClonePlanner.Create(source, target, table, environment ?? NoHost, options ?? new CloneOptions());

    [Fact]
    public void EqualSizeGptPlanCopiesEverySourceByteOnceWithTheTableLast()
    {
        var (source, table) = GptSource();

        var result = Plan(source, Target(), table);

        Assert.Empty(result.Problems);
        var plan = Assert.IsType<ClonePlan>(result.Plan);
        Assert.False(plan.AdjustsGpt);

        // Primero se borran la tabla y el final del destino; la tabla se escribe al final (RS-05).
        Assert.Equal(CloneStepKind.Clear, plan.Steps[0].Kind);
        Assert.Equal(0, plan.Steps[0].Offset);
        Assert.Equal(CloneStepKind.Clear, plan.Steps[1].Kind);
        Assert.Equal(8 * MiB, plan.Steps[1].End);
        Assert.Equal(CloneStepKind.WritePartitionTable, plan.Steps[^1].Kind);
        Assert.Equal(0, plan.Steps[^1].Offset);

        // Los pasos que escriben datos cubren el origen entero, sin huecos ni solapes.
        var ranges = plan.Steps.Where(s => s.WritesData).OrderBy(s => s.Offset).ToList();
        Assert.Equal(0, ranges[0].Offset);
        for (var i = 1; i < ranges.Count; i++)
        {
            Assert.Equal(ranges[i - 1].End, ranges[i].Offset);
        }

        Assert.Equal(8 * MiB, ranges[^1].End);
        Assert.Equal(8 * MiB, plan.BytesToCopy);
        Assert.All(plan.Steps, s => Assert.True(s.Offset % Sector == 0 && s.Length % Sector == 0));
    }

    [Fact]
    public void LargerGptTargetGetsTheBackupTableAtItsEnd()
    {
        var (source, table) = GptSource();

        var plan = Plan(source, Target(12 * MiB), table).Plan!;

        Assert.True(plan.AdjustsGpt);
        Assert.Equal(4 * MiB, plan.ExtraTargetBytes);
        var backup = Assert.Single(plan.Steps, s => s.Kind == CloneStepKind.WriteBackupGpt);
        Assert.Equal(12 * MiB, backup.End);
        Assert.Equal(33 * Sector, backup.Length);

        // La posición antigua de la copia de respaldo se borra y no se copia.
        var oldBackupStart = (8 * MiB) - (33 * Sector);
        Assert.Contains(plan.Steps, s => s.Kind == CloneStepKind.Clear && s.Offset == oldBackupStart && s.End == 8 * MiB);
        Assert.DoesNotContain(plan.Steps, s => s.Kind == CloneStepKind.Copy && s.End > oldBackupStart);
        Assert.Equal(CloneStepKind.WritePartitionTable, plan.Steps[^1].Kind);
    }

    [Fact]
    public void LargerMbrTargetCopiesTheSourceAsItIs()
    {
        var image = Mbr(8 * MiB, 0xCAFE0001, (MiB, 6 * MiB));
        var source = TestDisks.Info(1, image);

        var plan = Plan(source, Target(12 * MiB), TestDisks.Table(image)).Plan!;

        Assert.False(plan.AdjustsGpt);
        Assert.DoesNotContain(plan.Steps, s => s.Kind == CloneStepKind.WriteBackupGpt);
        var copy = Assert.Single(plan.Steps, s => s.Kind == CloneStepKind.Copy);
        Assert.Equal((long)PartitionTable.HeadBytes, copy.Offset);
        Assert.Equal(8 * MiB, copy.End);
    }

    [Fact]
    public void PlanListsEverythingThatWillBeErasedOnTheTarget()
    {
        var (source, table) = GptSource();
        var target = Target();

        var plan = Plan(source, target, table).Plan!;

        Assert.Same(target.Partitions, plan.TargetPartitions);
        Assert.Equal(target.Number, plan.Target.Number);
        Assert.Equal(source.Identifier, plan.Source.Identifier);
    }

    [Fact]
    public void SameDiskIsRejected()
    {
        var (source, table) = GptSource();

        var result = Plan(source, source, table);

        Assert.Null(result.Plan);
        Assert.Contains(result.Problems, p => p.Contains("mismo disco", StringComparison.Ordinal));
    }

    [Fact]
    public void SystemDiskCannotBeTheTarget()
    {
        var (source, table) = GptSource();
        var target = Target() with { ContainsWindows = true };

        var result = Plan(source, target, table);

        Assert.Null(result.Plan);
        Assert.Contains(result.Problems, p => p.Contains("disco del sistema", StringComparison.Ordinal));
    }

    [Fact]
    public void BootLoaderDiskCannotBeTheTarget()
    {
        var (source, table) = GptSource();

        Assert.Null(Plan(source, Target() with { ContainsBootLoader = true }, table).Plan);
    }

    [Fact]
    public void DiskRunningTheAppCannotBeTheTarget()
    {
        var (source, table) = GptSource();

        var result = Plan(source, Target(), table, new CloneEnvironment { HostDiskNumber = 2 });

        Assert.Null(result.Plan);
        Assert.Contains(result.Problems, p => p.Contains("ejecutando", StringComparison.Ordinal));
    }

    [Fact]
    public void RunningSystemDiskCannotBeTheSourceYet()
    {
        var (source, table) = GptSource();

        Assert.Null(Plan(source with { ContainsWindows = true }, Target(), table).Plan);
    }

    [Fact]
    public void SmallerTargetIsRejectedWithBothSizes()
    {
        var (source, table) = GptSource(12 * MiB);

        var result = Plan(source, Target(8 * MiB), table);

        Assert.Null(result.Plan);
        Assert.Contains(result.Problems, p => p.Contains("más pequeño", StringComparison.Ordinal) && p.Contains("12 MB", StringComparison.Ordinal));
    }

    [Fact]
    public void DifferentLogicalSectorSizesAreRejected()
    {
        var (source, table) = GptSource();

        var result = Plan(source, Target() with { LogicalSectorSize = 4096 }, table);

        Assert.Contains(result.Problems, p => p.Contains("sector", StringComparison.Ordinal));
    }

    [Fact]
    public void ReadOnlyOrOfflineTargetIsRejected()
    {
        var (source, table) = GptSource();

        Assert.Null(Plan(source, Target() with { IsReadOnly = true }, table).Plan);
        Assert.Null(Plan(source, Target() with { IsOffline = true }, table).Plan);
    }

    [Fact]
    public void VirtualOnlyModeRejectsPhysicalTargets()
    {
        var (source, table) = GptSource();
        var options = new CloneOptions { VirtualTargetOnly = true };

        Assert.NotNull(Plan(source, Target(), table, options: options).Plan);
        Assert.Null(Plan(source, Target() with { Bus = BusKind.Usb }, table, options: options).Plan);
    }

    [Fact]
    public void UnreadableOrInconsistentSourceTableIsRejected()
    {
        var (source, table) = GptSource();

        Assert.Null(Plan(source, Target(), null).Plan);
        Assert.Null(Plan(source, Target(), table with { Defect = "dañada" }).Plan);
        Assert.Null(Plan(source with { Identifier = "otro" }, Target(), table).Plan);
    }

    [Fact]
    public void DynamicDisksAreRejected()
    {
        var (source, table) = GptSource();
        var dynamic = source with { Partitions = [source.Partitions[0] with { Kind = PartitionKind.Dynamic }] };

        Assert.Null(Plan(dynamic, Target(), table).Plan);
    }

    [Fact]
    public void WarningsExplainTheConsequences()
    {
        var (source, table) = GptSource();
        var risky = source with
        {
            Health = HealthState.Warning,
            Partitions = [source.Partitions[0] with { Encryption = EncryptionState.On }],
        };

        var warnings = Plan(risky, Target(12 * MiB), table).Warnings;

        Assert.Contains(warnings, w => w.Contains("salud", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("BitLocker", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("mismo identificador", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("4 MB", StringComparison.Ordinal));
    }
}
