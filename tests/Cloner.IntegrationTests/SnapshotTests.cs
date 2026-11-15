using Cloner.Core;
using Cloner.Core.Devices;
using Cloner.Core.Snapshots;

namespace Cloner.IntegrationTests;

/// <summary>Instantáneas VSS reales sobre el volumen de un VHD de prueba. Requiere administrador.</summary>
[Collection(VirtualDiskTests.Name)]
public sealed class SnapshotTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cloner-vss", Guid.NewGuid().ToString("N"));
    private VirtualDisk? _disk;

    public SnapshotTests()
    {
        if (Elevation.IsAdministrator())
        {
            Directory.CreateDirectory(_directory);
        }
    }

    [AdminFact]
    public void SnapshotKeepsTheContentFromWhenItWasTaken()
    {
        _disk = VirtualDisk.Create(Path.Combine(_directory, "vss.vhd"), 64, label: "ORIGEN");
        var volume = _disk.Info().Partitions.Single(p => p.Volume?.Label == "ORIGEN").Volume!;
        var file = Path.Combine(volume.Path, "muestra.txt");
        File.WriteAllText(file, "antes");

        using var snapshot = VssSnapshotSet.Create([volume.Path]);
        File.WriteAllText(file, "después");

        var device = snapshot.DeviceFor(volume.Path);
        Assert.StartsWith(@"\\?\GLOBALROOT\Device\", device, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("antes", File.ReadAllText(device + @"\muestra.txt"));
        Assert.Equal("después", File.ReadAllText(file));

        using var raw = DiskDevice.OpenVolume(device, 512);
        Assert.Equal(volume.SizeBytes, raw.Length, tolerance: 1024 * 1024);
        var readable = raw.ReadableLength();
        Assert.InRange(readable, raw.Length - (64 * 1024), raw.Length);
        var used = raw.ReadNtfsUsage(readable);
        Assert.NotNull(used);
        Assert.NotEmpty(used);
        Assert.Equal(0, used[0].Offset);
        Assert.True(used[^1].End <= readable);
        Assert.True(used.Sum(r => r.Length) < raw.Length, "Un volumen casi vacío debe tener bloques libres.");

        snapshot.Complete();
        snapshot.Dispose();
        VirtualDisk.NoteSnapshotUse();
    }

    public void Dispose()
    {
        _disk?.Dispose();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
