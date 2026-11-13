namespace Cloner.Core.Inventory;

/// <summary>Motivos por los que un disco se considera en riesgo.</summary>
[Flags]
public enum HealthIssues
{
    None = 0,

    /// <summary>Windows declara el disco en estado de advertencia.</summary>
    ReportedWarning = 1,

    /// <summary>Windows declara el disco en mal estado.</summary>
    ReportedUnhealthy = 2,

    UncorrectedReadErrors = 4,
    UncorrectedWriteErrors = 8,

    /// <summary>La vida útil consumida del SSD alcanza el umbral.</summary>
    HighWear = 16,

    HighTemperature = 32,
}

/// <summary>Evaluación del estado de salud de un disco.</summary>
public static class DiskHealth
{
    public const int WearThresholdPercent = 90;
    public const int TemperatureThresholdCelsius = 65;

    public static HealthIssues Assess(DiskInfo disk)
    {
        ArgumentNullException.ThrowIfNull(disk);

        var issues = disk.Health switch
        {
            HealthState.Warning => HealthIssues.ReportedWarning,
            HealthState.Unhealthy => HealthIssues.ReportedUnhealthy,
            _ => HealthIssues.None,
        };

        if (disk.Reliability is { } reliability)
        {
            if (reliability.ReadErrorsUncorrected > 0) issues |= HealthIssues.UncorrectedReadErrors;
            if (reliability.WriteErrorsUncorrected > 0) issues |= HealthIssues.UncorrectedWriteErrors;
            if (reliability.WearPercent >= WearThresholdPercent) issues |= HealthIssues.HighWear;
            if (reliability.TemperatureCelsius >= TemperatureThresholdCelsius) issues |= HealthIssues.HighTemperature;
        }

        return issues;
    }
}
