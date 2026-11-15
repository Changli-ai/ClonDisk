using Cloner.Core.Cloning;
using Cloner.Core.Devices;
using Cloner.Core.Inventory;
using Cloner.Core.PartitionTables;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cloner.IntegrationTests;

/// <summary>
/// E/S cruda real (CreateFile sin caché, DeviceIoControl) sin escribir en ningún disco: las
/// escrituras van a archivos temporales y las consultas a discos no piden acceso de lectura.
/// </summary>
public sealed class RawIoTests : IDisposable
{
    private const int Sector = 512;
    private const long MiB = 1024 * 1024;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cloner-raw-io", Guid.NewGuid().ToString("N"));

    public RawIoTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void SystemDiskReportsTheSameIdentityAsTheInventory()
    {
        var system = new DiskInventory().GetDisks().Single(d => d.ContainsWindows);

        var facts = DiskDevice.QueryFacts(system.Number);

        Assert.Equal(system.Number, facts.DeviceNumber);
        Assert.Equal(system.SizeBytes, facts.SizeBytes);
        Assert.Equal(system.LogicalSectorSize, facts.SectorSize);
        Assert.Null(DiskIdentity.From(system).FindMismatch(facts));
    }

    [Fact]
    public void AppRunsFromTheSystemDisk()
    {
        var disks = new DiskInventory().GetDisks();
        var system = disks.Single(d => d.ContainsWindows);

        // Solo tiene sentido si el repositorio está en la unidad del sistema.
        if (Path.GetPathRoot(AppContext.BaseDirectory) != Path.GetPathRoot(Environment.SystemDirectory))
        {
            return;
        }

        Assert.Equal(system.Number, HostDisk.FindDiskNumber(AppContext.BaseDirectory, disks));
    }

    [Fact]
    public void UnbufferedFileDeviceReadsWhatItWrote()
    {
        var path = CreateFile("dispositivo.img", 2 * MiB, seed: 7);
        var expected = File.ReadAllBytes(path);

        using (var device = DiskDevice.OpenFile(path, Sector, writable: true))
        using (var buffer = new AlignedBuffer((int)MiB))
        {
            Assert.Equal(2 * MiB, device.Length);
            new Random(8).NextBytes(buffer.Span);
            device.Write(MiB, buffer.Span);
            device.Flush();
            buffer.Span.CopyTo(expected.AsSpan((int)MiB));

            buffer.Span.Clear();
            device.Read(MiB, buffer.Span);
            Assert.True(buffer.Span.SequenceEqual(expected.AsSpan((int)MiB)));
        }

        Assert.Equal(expected, File.ReadAllBytes(path));
    }

    [Fact]
    public void ReadOnlyFileDeviceRefusesToWrite()
    {
        var path = CreateFile("solo-lectura.img", MiB, seed: 9);

        using var device = DiskDevice.OpenFile(path, Sector, writable: false);
        using var buffer = new AlignedBuffer(Sector);

        Assert.False(device.CanWrite);
        Assert.Throws<InvalidOperationException>(() => device.Write(0, buffer.Span));
    }

    [Fact]
    public void ExecutorClonesBetweenRealUnbufferedFiles()
    {
        // Misma ruta de E/S que con discos (CreateFile sin caché, hilos, búferes alineados), sobre archivos.
        var sourcePath = Path.Combine(_directory, "origen.img");
        var targetPath = CreateFile("destino.img", 24 * MiB, seed: 11);
        File.WriteAllBytes(sourcePath, GptImage(16 * MiB));

        var source = InfoFor(1, sourcePath);
        var target = InfoFor(2, targetPath);
        var table = PartitionTable.Read(File.ReadAllBytes(sourcePath).AsSpan(0, PartitionTable.HeadBytes), Sector);
        var plan = ClonePlanner.Create(source, target, table, new CloneEnvironment(), new CloneOptions { Mode = CloneMode.SectorBySector }).Plan!;

        var access = new FileDiskAccess(new() { [1] = sourcePath, [2] = targetPath });
        var result = new CloneExecutor(access, NullLogger.Instance).Execute(plan, null, CancellationToken.None);

        Assert.Equal(CloneOutcome.Succeeded, result.Outcome);
        Assert.True(result.Verified);
        var cloned = File.ReadAllBytes(targetPath);
        var original = File.ReadAllBytes(sourcePath);
        var dataEnd = (int)((16 * MiB) - (33 * Sector));
        Assert.True(cloned.AsSpan(PartitionTable.HeadBytes, dataEnd - PartitionTable.HeadBytes)
            .SequenceEqual(original.AsSpan(PartitionTable.HeadBytes, dataEnd - PartitionTable.HeadBytes)));
        var clonedTable = PartitionTable.Read(cloned.AsSpan(0, PartitionTable.HeadBytes), Sector);
        Assert.Equal(table.Identifier, clonedTable.Identifier);
        Assert.Equal((24 * MiB / Sector) - 1, clonedTable.Gpt!.AlternateLba);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private string CreateFile(string name, long size, int seed)
    {
        var path = Path.Combine(_directory, name);
        var data = new byte[size];
        new Random(seed).NextBytes(data);
        File.WriteAllBytes(path, data);
        return path;
    }

    /// <summary>Imagen GPT válida con una partición de datos.</summary>
    private static byte[] GptImage(long size)
    {
        var image = new byte[size];
        new Random(5).NextBytes(image.AsSpan((int)MiB, (int)(size - (2 * MiB))));

        var lastLba = (size / Sector) - 1;
        var entries = new byte[128 * 128];
        new Guid("ebd0a0a2-b9e5-4433-87c0-68b6b72699c7").TryWriteBytes(entries);
        Guid.NewGuid().TryWriteBytes(entries.AsSpan(16));
        BitConverter.TryWriteBytes(entries.AsSpan(32), MiB / Sector);
        BitConverter.TryWriteBytes(entries.AsSpan(40), ((size - MiB) / Sector) - 1);

        var header = new GptHeader
        {
            SectorSize = Sector,
            Revision = 0x00010000,
            HeaderSize = GptHeader.MinimumHeaderSize,
            MyLba = 1,
            AlternateLba = lastLba,
            FirstUsableLba = 34,
            LastUsableLba = lastLba - 33,
            DiskGuid = Guid.NewGuid(),
            PartitionEntryLba = 2,
            EntryCount = 128,
            EntrySize = 128,
            EntriesCrc = System.IO.Hashing.Crc32.HashToUInt32(entries),
        };

        image[446 + 4] = 0xEE;
        image[510] = 0x55;
        image[511] = 0xAA;
        header.Encode(1, lastLba, lastLba - 33, 2).CopyTo(image, Sector);
        entries.CopyTo(image, 2 * Sector);
        entries.CopyTo(image, (lastLba - 32) * Sector);
        header.Encode(lastLba, 1, lastLba - 33, lastLba - 32).CopyTo(image, lastLba * Sector);
        return image;
    }

    private static DiskInfo InfoFor(int number, string path)
    {
        var table = PartitionTable.Read(File.ReadAllBytes(path).AsSpan(0, PartitionTable.HeadBytes), Sector);
        return new DiskInfo
        {
            Number = number,
            Model = "Archivo de prueba",
            SizeBytes = new FileInfo(path).Length,
            Bus = BusKind.Virtual,
            PartitionStyle = table.Style,
            LogicalSectorSize = Sector,
            PhysicalSectorSize = Sector,
            Identifier = table.Identifier,
            Partitions = [],
        };
    }

    private sealed class FileDiskAccess(Dictionary<int, string> paths) : IDiskAccess
    {
        public IBlockDevice Open(DiskIdentity disk, bool writable) => DiskDevice.OpenFile(paths[disk.Number], Sector, writable);

        public IDisposable LockVolumes(IReadOnlyList<VolumeRef> volumes, bool dismount) => new Nothing();

        public ISnapshotSet CreateSnapshots(IReadOnlyList<string> volumePaths, int sectorSize) =>
            throw new NotSupportedException("Las imágenes de archivo no tienen volúmenes.");

        public IVolumeSource OpenVolume(string volumePath, int sectorSize) =>
            throw new NotSupportedException("Las imágenes de archivo no tienen volúmenes.");

        public ITargetFinisher Finisher => throw new NotSupportedException("Las imágenes de archivo no se montan.");

        private sealed class Nothing : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}
