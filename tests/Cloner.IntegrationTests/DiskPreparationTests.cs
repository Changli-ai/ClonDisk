using Cloner.Core.Cloning;
using Cloner.Core.Inventory;

namespace Cloner.IntegrationTests;

/// <summary>
/// «Limpiar y formatear» sobre un VHD con particiones de un Windows (EFI y NTFS): debe quedar con
/// una sola partición NTFS con letra, sin la EFI que un formateo normal deja. Requiere administrador.
/// </summary>
[Collection(VirtualDiskTests.Name)]
public sealed class DiskPreparationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cloner-vhd", Guid.NewGuid().ToString("N"));
    private VirtualDisk? _disk;

    [AdminFact]
    public async Task PreparedDiskKeepsOnlyOneNtfsPartitionWithALetter()
    {
        Directory.CreateDirectory(_directory);
        _disk = VirtualDisk.CreateWithEfi(Path.Combine(_directory, "limpiar.vhd"), 128);
        var before = _disk.Info();
        Assert.Contains(before.Partitions, p => p.Kind == PartitionKind.EfiSystem);

        var service = new DiskPreparationService(new DiskInventory(), _directory, virtualOnly: true);
        var result = await service.PrepareAsync(DiskIdentity.From(before), progress: null);

        Assert.True(result.Succeeded, result.Message);
        Assert.NotNull(result.DriveLetter);
        var after = _disk.Info();
        Assert.Equal(PartitionStyle.Gpt, after.PartitionStyle);
        Assert.DoesNotContain(after.Partitions, p => p.Kind == PartitionKind.EfiSystem);
        var data = Assert.Single(after.Partitions, p => p.Kind == PartitionKind.BasicData);
        Assert.Equal("NTFS", data.Volume?.FileSystem);
        Assert.Equal(DiskPreparationService.VolumeLabel, data.Volume?.Label);
        Assert.Equal(result.DriveLetter, data.DriveLetter);
        Assert.True(Directory.Exists($@"{result.DriveLetter}:\"));
    }

    [AdminFact]
    public async Task ChangedDiskIsNotTouched()
    {
        Directory.CreateDirectory(_directory);
        _disk = VirtualDisk.CreateWithEfi(Path.Combine(_directory, "otro.vhd"), 64);
        var before = _disk.Info();
        var stale = DiskIdentity.From(before) with { Identifier = Guid.NewGuid().ToString("B") };

        var result = await new DiskPreparationService(new DiskInventory(), _directory, virtualOnly: true).PrepareAsync(stale, progress: null);

        Assert.False(result.Succeeded);
        Assert.Contains(_disk.Info().Partitions, p => p.Kind == PartitionKind.EfiSystem);
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
