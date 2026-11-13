using Cloner.Core.Inventory;

namespace Cloner.Core.Tests.Inventory;

public class DiskHealthTests
{
    private static DiskInfo Disk(HealthState health = HealthState.Healthy, DiskReliability? reliability = null) => new()
    {
        Number = 0,
        Model = "Test",
        SizeBytes = 1000,
        Health = health,
        Reliability = reliability,
        Partitions = [],
    };

    [Fact]
    public void HealthyDiskWithoutCountersHasNoIssues()
    {
        Assert.Equal(HealthIssues.None, DiskHealth.Assess(Disk()));
    }

    [Fact]
    public void UnknownHealthIsNotAnIssue()
    {
        Assert.Equal(HealthIssues.None, DiskHealth.Assess(Disk(HealthState.Unknown)));
    }

    [Theory]
    [InlineData(HealthState.Warning, HealthIssues.ReportedWarning)]
    [InlineData(HealthState.Unhealthy, HealthIssues.ReportedUnhealthy)]
    public void ReportedStateBecomesAnIssue(HealthState health, HealthIssues expected)
    {
        Assert.Equal(expected, DiskHealth.Assess(Disk(health)));
    }

    [Fact]
    public void CountersBelowThresholdsAreNotIssues()
    {
        var reliability = new DiskReliability
        {
            TemperatureCelsius = DiskHealth.TemperatureThresholdCelsius - 1,
            WearPercent = DiskHealth.WearThresholdPercent - 1,
            ReadErrorsUncorrected = 0,
            WriteErrorsUncorrected = 0,
        };

        Assert.Equal(HealthIssues.None, DiskHealth.Assess(Disk(reliability: reliability)));
    }

    [Fact]
    public void EveryCounterOverItsThresholdIsReported()
    {
        var reliability = new DiskReliability
        {
            TemperatureCelsius = DiskHealth.TemperatureThresholdCelsius,
            WearPercent = DiskHealth.WearThresholdPercent,
            ReadErrorsUncorrected = 1,
            WriteErrorsUncorrected = 1,
        };

        var issues = DiskHealth.Assess(Disk(HealthState.Unhealthy, reliability));

        Assert.Equal(
            HealthIssues.ReportedUnhealthy | HealthIssues.UncorrectedReadErrors | HealthIssues.UncorrectedWriteErrors
                | HealthIssues.HighWear | HealthIssues.HighTemperature,
            issues);
        Assert.Equal(5, InventoryText.Issues(issues).Count);
    }
}
