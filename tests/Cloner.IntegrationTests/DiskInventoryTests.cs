using Cloner.Core.Inventory;

namespace Cloner.IntegrationTests;

/// <summary>Lectura del inventario real del equipo. Solo consulta; no requiere administrador.</summary>
public class DiskInventoryTests
{
    [Fact]
    public void RealInventoryContainsTheSystemDisk()
    {
        var disks = new DiskInventory().GetDisks();

        var system = Assert.Single(disks, d => d.ContainsWindows);
        Assert.True(system.SizeBytes > 0);
        Assert.True(system.LogicalSectorSize > 0);
        Assert.NotEqual(PartitionStyle.Raw, system.PartitionStyle);
        Assert.NotNull(system.Identifier);

        var windows = Assert.Single(system.Partitions, p => p.ContainsWindows);
        var systemDrive = char.ToUpperInvariant(Environment.SystemDirectory[0]);
        Assert.Equal(systemDrive, windows.DriveLetter);
        Assert.NotNull(windows.Volume);
        Assert.InRange(windows.Volume.UsedBytes, 1, windows.SizeBytes);
    }

    [Fact]
    public void PartitionsLieInsideTheirDiskAndDoNotOverlap()
    {
        foreach (var disk in new DiskInventory().GetDisks())
        {
            long previousEnd = 0;
            foreach (var partition in disk.Partitions.Where(p => p.Kind != PartitionKind.Extended))
            {
                Assert.True(partition.OffsetBytes >= previousEnd, $"Disco {disk.Number}: la partición {partition.Number} se solapa");
                previousEnd = partition.OffsetBytes + partition.SizeBytes;
            }

            Assert.True(previousEnd <= disk.SizeBytes, $"Disco {disk.Number}: las particiones exceden el disco");
        }
    }
}
