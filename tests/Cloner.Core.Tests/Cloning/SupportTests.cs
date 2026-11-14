using Cloner.Core.Cloning;
using Cloner.Core.Inventory;
using Cloner.Core.Logging;
using Microsoft.Extensions.Logging;
using static Cloner.Core.Tests.Cloning.DiskImages;

namespace Cloner.Core.Tests.Cloning;

public class PlanValidationTests
{
    private static (ClonePlan Plan, DiskInfo Source, DiskInfo Target) Planned()
    {
        var image = Gpt(8 * MiB, Guid.NewGuid(), (MiB, 6 * MiB));
        var source = TestDisks.Info(1, image, [TestDisks.Partition(1, MiB, 6 * MiB, 'E')], serial: "SERIE-1");
        var target = TestDisks.Info(2, Garbage(8 * MiB), [TestDisks.Partition(1, MiB, MiB, 'F')], serial: "SERIE-2");
        var plan = ClonePlanner.Create(source, target, TestDisks.Table(image), new CloneEnvironment(), new CloneOptions()).Plan!;
        return (plan, source, target);
    }

    [Fact]
    public void UnchangedDisksPass()
    {
        var (plan, source, target) = Planned();

        Assert.Null(PlanValidation.FindChange(plan, [source, target]));
    }

    [Fact]
    public void AnotherDiskWithTheTargetNumberIsDetected()
    {
        var (plan, source, target) = Planned();
        var other = target with { SerialNumber = "OTRO", DevicePath = @"\\?\otro" };

        var change = PlanValidation.FindChange(plan, [source, other]);

        Assert.NotNull(change);
        Assert.Contains("destino", change, StringComparison.Ordinal);
    }

    [Fact]
    public void DisconnectedDiskIsDetected()
    {
        var (plan, source, _) = Planned();

        Assert.Contains("ya no está conectado", PlanValidation.FindChange(plan, [source]), StringComparison.Ordinal);
    }

    [Fact]
    public void NewPartitionOnTheTargetIsDetected()
    {
        var (plan, source, target) = Planned();
        var changed = target with { Partitions = [.. target.Partitions, TestDisks.Partition(2, 3 * MiB, MiB, 'G')] };

        Assert.Contains("han cambiado", PlanValidation.FindChange(plan, [source, changed]), StringComparison.Ordinal);
    }
}

public class ProgressMeterTests
{
    [Fact]
    public void SpeedAndRemainingTimeComeFromRecentBytes()
    {
        var time = new ManualTime();
        var meter = new ProgressMeter(1000 * MiB, time);

        time.Advance(TimeSpan.FromSeconds(2));
        meter.Advance(200 * MiB);

        Assert.Equal(100 * MiB, meter.BytesPerSecond, precision: 3);
        Assert.Equal(TimeSpan.FromSeconds(8), meter.Remaining);
    }

    [Fact]
    public void NoEstimateDuringTheFirstSecond()
    {
        var time = new ManualTime();
        var meter = new ProgressMeter(1000, time);

        time.Advance(TimeSpan.FromMilliseconds(500));
        meter.Advance(10);

        Assert.Equal(0, meter.BytesPerSecond);
        Assert.Null(meter.Remaining);
    }

    [Fact]
    public void OldSamplesLeaveTheWindow()
    {
        var time = new ManualTime();
        var meter = new ProgressMeter(10_000 * MiB, time);

        // Diez segundos rápidos y luego cinco lentos: la velocidad refleja los lentos.
        for (var i = 0; i < 10; i++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            meter.Advance(500 * MiB);
        }

        for (var i = 0; i < 6; i++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            meter.Advance(50 * MiB);
        }

        Assert.InRange(meter.BytesPerSecond, 40 * MiB, 60 * MiB);
    }

    [Fact]
    public void ReportsAreThrottled()
    {
        var time = new ManualTime();
        var meter = new ProgressMeter(100, time);

        Assert.False(meter.ShouldReport());
        time.Advance(TimeSpan.FromMilliseconds(300));
        Assert.True(meter.ShouldReport());
        Assert.False(meter.ShouldReport());
    }
}

public sealed class FileLoggerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cloner-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void LinesAreWrittenWithLevelAndException()
    {
        string path;
        using (var logger = FileLogger.Create(_directory, "clonado"))
        {
            path = logger.FilePath;
            CloneLog.VolumesLocked(logger);
            CloneLog.CloneError(logger, new IOException("fallo de prueba"));
        }

        Assert.StartsWith("clonado-", Path.GetFileName(path), StringComparison.Ordinal);
        var lines = File.ReadAllLines(path);
        Assert.Contains(lines, l => l.Contains("INFO", StringComparison.Ordinal) && l.Contains("Volúmenes bloqueados", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("ERROR", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("fallo de prueba", StringComparison.Ordinal));
    }

    [Fact]
    public void TwoLogsInTheSameSecondGetDifferentFiles()
    {
        using var first = FileLogger.Create(_directory, "clonado");
        using var second = FileLogger.Create(_directory, "clonado");

        Assert.NotEqual(first.FilePath, second.FilePath);
        Assert.False(first.IsEnabled(LogLevel.None));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
