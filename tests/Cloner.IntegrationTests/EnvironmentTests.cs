using Cloner.Core;

namespace Cloner.IntegrationTests;

/// <summary>Condiciones del entorno que necesitan las pruebas de integración.</summary>
public class EnvironmentTests
{
    [Fact]
    public void DiskpartIsAvailableToCreateVirtualDisks()
    {
        var diskpart = Path.Combine(Environment.SystemDirectory, "diskpart.exe");
        Assert.True(File.Exists(diskpart), $"No se encontró {diskpart}");
    }

    [AdminFact]
    public void ProcessRunsElevated()
    {
        Assert.True(Elevation.IsAdministrator());
    }
}
