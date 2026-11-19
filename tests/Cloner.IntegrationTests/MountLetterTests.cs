using Cloner.Core;
using Cloner.Core.Devices;
using Cloner.Core.Inventory;

namespace Cloner.IntegrationTests;

/// <summary>
/// Letras temporales para los volúmenes de un clon al rehacer su arranque, incluida la partición
/// EFI (que no siempre admite letra por la vía normal). Requiere administrador.
/// </summary>
[Collection(VirtualDiskTests.Name)]
public sealed class MountLetterTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cloner-vhd", Guid.NewGuid().ToString("N"));
    private VirtualDisk? _disk;

    [AdminFact]
    public void EfiAndDataVolumesGetATemporaryLetterThatIsRemovedAfterwards()
    {
        Directory.CreateDirectory(_directory);
        _disk = VirtualDisk.CreateWithEfi(Path.Combine(_directory, "efi.vhd"), 128);
        var disk = _disk.Info();
        var efi = disk.Partitions.Single(p => p.Kind == PartitionKind.EfiSystem).Volume!.Path;
        var data = disk.Partitions.Single(p => p.Kind == PartitionKind.BasicData).Volume!.Path;
        var finisher = new TargetFinisher(new DiskInventory());

        foreach (var volume in new[] { efi, data })
        {
            var (letter, release) = finisher.MountTemporarily(volume);
            try
            {
                Assert.True(Directory.Exists($@"{letter}:\"), $"La letra {letter}: no se ve.");
            }
            finally
            {
                release.Dispose();
            }

            Assert.False(Directory.Exists($@"{letter}:\"), $"La letra {letter}: sigue asignada.");
        }
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
