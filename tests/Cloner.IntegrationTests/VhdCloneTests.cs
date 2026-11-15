using System.IO.Hashing;
using Cloner.Core;
using Cloner.Core.Cloning;
using Cloner.Core.Devices;
using Cloner.Core.Inventory;
using Cloner.Core.Layout;
using Cloner.Core.PartitionTables;

namespace Cloner.IntegrationTests;

/// <summary>
/// Clonado real entre discos virtuales VHD (etapa 2). Solo escribe en los VHD que crea, y el
/// servicio se usa en modo «solo destinos virtuales». Requiere una consola de administrador.
/// </summary>
[Collection(VirtualDiskTests.Name)]
public sealed class VhdCloneTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cloner-vhd", Guid.NewGuid().ToString("N"));
    private readonly List<VirtualDisk> _disks = [];

    public VhdCloneTests()
    {
        if (Elevation.IsAdministrator())
        {
            Directory.CreateDirectory(_directory);
        }
    }

    [AdminFact]
    public void SystemDiskHeadMatchesTheInventory()
    {
        // Solo lectura: abre el disco del sistema sin permiso de escritura y lee su tabla.
        var system = new DiskInventory().GetDisks().Single(d => d.ContainsWindows);

        using var device = DiskDevice.OpenDisk(system.Number, writable: false);
        using var buffer = new AlignedBuffer(PartitionTable.HeadBytes);
        device.Read(0, buffer.Span);
        var table = PartitionTable.Read(buffer.Span, device.SectorSize);

        Assert.False(device.CanWrite);
        Assert.Equal(system.PartitionStyle, table.Style);
        Assert.Equal(system.Identifier, table.Identifier);
        Assert.Null(table.Defect);
    }

    [AdminFact]
    public async Task EqualSizeVhdCloneIsIdenticalByHash()
    {
        var source = NewDisk("origen.vhd", 64, label: "ORIGEN");
        var target = NewDisk("destino.vhd", 64);
        WriteSampleFile(source);

        // Montado en solo lectura, Windows no puede escribir en el origen (por ejemplo, al
        // desmontar su NTFS), así que lo que queda en su archivo es exactamente lo que se copió.
        source.Detach();
        source.Attach(readOnly: true);

        var (plan, result) = await Clone(source, target, CloneMode.SectorBySector);

        Assert.True(result.Outcome == CloneOutcome.Succeeded, result.Message);
        Assert.True(result.Verified, result.Message);
        Assert.True(File.Exists(result.LogPath));

        source.Detach();
        target.Detach();
        var original = source.ReadData();
        var clone = target.ReadData();

        // 1. Lo escrito cubre el disco entero y su huella es la del archivo del origen.
        var written = plan.Steps.Where(s => s.WritesData).OrderBy(s => s.Offset).ToList();
        Assert.Equal(original.Length, written.Sum(s => s.Length));
        Assert.Equal(original.Length, written[^1].End);
        var expected = new XxHash128();
        foreach (var step in plan.Steps.Where(s => s.WritesData))
        {
            expected.Append(original.AsSpan((int)step.Offset, (int)step.Length));
        }

        Assert.Equal(Convert.ToHexStringLower(expected.GetCurrentHash()), result.DataHash);

        // 2. La verificación releyó el destino y coincidía con lo escrito (Verified). Después,
        //    Windows monta el clon y escribe metadatos NTFS; las tablas GPT no las toca.
        const int sector = 512;
        Assert.True(original.AsSpan(0, 34 * sector).SequenceEqual(clone.AsSpan(0, 34 * sector)), DescribeDifferences(original, clone));
        Assert.True(original.AsSpan(^(33 * sector)..).SequenceEqual(clone.AsSpan(^(33 * sector)..)), DescribeDifferences(original, clone));
    }

    /// <summary>Bloques de 4 KiB distintos, para saber qué cambió si la comparación falla.</summary>
    private static string DescribeDifferences(byte[] expected, byte[] actual)
    {
        const int block = 4096;
        var offsets = new List<long>();
        for (var offset = 0; offset < Math.Min(expected.Length, actual.Length); offset += block)
        {
            var length = Math.Min(block, expected.Length - offset);
            if (!expected.AsSpan(offset, length).SequenceEqual(actual.AsSpan(offset, length)))
            {
                offsets.Add(offset);
            }
        }

        return $"{offsets.Count} bloques de 4 KiB distintos; los primeros en los bytes {string.Join(", ", offsets.Take(12))}.";
    }

    [AdminFact]
    public async Task LargerVhdCloneIsReadableAndKeepsFiles()
    {
        var source = NewDisk("origen.vhd", 64, label: "ORIGEN");
        var target = NewDisk("destino.vhd", 96);
        var expected = WriteSampleFile(source);
        var sourceInfo = source.Info();

        // Windows informa algunos VHD como SSD; sin esto, la alineación automática (RF-16) movería la partición.
        var (_, result) = await Clone(source, target, new CloneOptions { Alignment = AlignmentMode.Never });
        Assert.True(result.Outcome == CloneOutcome.Succeeded, result.Message);
        Assert.True(result.Verified, result.Message);

        // Con el origen desmontado, Windows reconoce el clon por sí solo.
        source.Detach();
        target.Detach();
        target.Attach();
        var clone = target.Info();
        Assert.Equal(PartitionStyle.Gpt, clone.PartitionStyle);
        Assert.Equal(sourceInfo.Identifier, clone.Identifier);
        Assert.Equal(sourceInfo.Partitions.Select(p => (p.OffsetBytes, p.SizeBytes)), clone.Partitions.Select(p => (p.OffsetBytes, p.SizeBytes)));
        Assert.True(clone.UnallocatedBytes >= 32L * 1024 * 1024);

        var volume = clone.Partitions.Single(p => p.Volume?.Label == "ORIGEN").Volume!;
        Assert.Equal(expected, File.ReadAllBytes(Path.Combine(volume.Path, "muestra.bin")));
    }

    [AdminFact]
    public async Task VolumeInUseIsClonedFromASnapshot()
    {
        var source = NewDisk("origen.vhd", 64, label: "ORIGEN");
        var target = NewDisk("destino.vhd", 64);
        var expected = WriteSampleFile(source);
        var volume = source.Info().Partitions.Single(p => p.Volume?.Label == "ORIGEN").Volume!;

        // Un archivo abierto en exclusiva impide bloquear el volumen: solo se puede copiar en caliente.
        CloneResult result;
        ClonePlan plan;
        using (var busy = new FileStream(Path.Combine(volume.Path, "abierto.bin"), FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            busy.Write(new byte[4096]);
            busy.Flush(flushToDisk: true);
            (plan, result) = await Clone(source, target);
        }

        Assert.True(Assert.Single(plan.Steps, s => s.Kind == CloneStepKind.CopyPartition && s.Partition!.Volume?.Label == "ORIGEN").FromSnapshot);
        Assert.True(result.Outcome == CloneOutcome.Succeeded, result.Message);
        Assert.True(result.Verified, result.Message);
        Assert.True(result.BytesWritten < 48L * 1024 * 1024, $"Con copia inteligente no debería copiarse todo: {result.BytesWritten} bytes.");

        source.Detach();
        target.Detach();
        target.Attach();
        var clone = target.Info().Partitions.Single(p => p.Volume?.Label == "ORIGEN").Volume!;
        Assert.Equal(expected, File.ReadAllBytes(Path.Combine(clone.Path, "muestra.bin")));
    }

    [AdminFact]
    public async Task ExtendedCloneFillsTheLargerDiskAndKeepsFiles()
    {
        var source = NewDisk("origen.vhd", 64, label: "ORIGEN");
        var target = NewDisk("destino.vhd", 128);
        var expected = WriteSampleFile(source);
        var sourceInfo = source.Info();

        var (plan, result) = await Clone(source, target, new CloneOptions { Sizing = SizeMode.ExtendMain });

        Assert.True(result.Outcome == CloneOutcome.Succeeded, result.Message);
        Assert.True(result.Verified, result.Message);
        Assert.Contains(plan.Layout.Placements, p => p.Grows);

        source.Detach();
        target.Detach();
        target.Attach();
        var clone = target.Info();

        // Conserva el identificador del origen y la partición ocupa casi todo el disco nuevo.
        Assert.Equal(sourceInfo.Identifier, clone.Identifier);
        var partition = clone.Partitions.Single(p => p.Volume?.Label == "ORIGEN");
        Assert.True(partition.SizeBytes > 120L * 1024 * 1024, $"La partición mide {partition.SizeBytes} bytes.");
        Assert.True(partition.Volume!.SizeBytes > 120L * 1024 * 1024, $"El volumen NTFS mide {partition.Volume.SizeBytes} bytes.");
        Assert.Equal(expected, File.ReadAllBytes(Path.Combine(partition.Volume.Path, "muestra.bin")));
    }

    [AdminFact]
    public async Task CloneWithNewIdentifiersStaysOnlineNextToTheOriginal()
    {
        var source = NewDisk("origen.vhd", 64, label: "ORIGEN");
        var target = NewDisk("destino.vhd", 64);
        var expected = WriteSampleFile(source);
        var sourceInfo = source.Info();

        var (_, result) = await Clone(source, target, new CloneOptions { NewIdentifiers = true });

        Assert.True(result.Outcome == CloneOutcome.Succeeded, result.Message);
        var clone = target.Info();
        Assert.NotEqual(sourceInfo.Identifier, clone.Identifier);
        Assert.False(clone.IsOffline, "Con identificadores nuevos, Windows no debe dejar el clon sin conexión.");
        var volume = clone.Partitions.Single(p => p.Volume?.Label == "ORIGEN").Volume!;
        Assert.Equal(expected, File.ReadAllBytes(Path.Combine(volume.Path, "muestra.bin")));
    }

    [AdminFact]
    public void PhysicalDisksAreRejectedInVirtualOnlyMode()
    {
        var source = NewDisk("origen.vhd", 64, label: "ORIGEN");
        var system = new DiskInventory().GetDisks().Single(d => d.ContainsWindows);
        var service = new CloneService(new DiskInventory(), _directory);

        var result = service.CreatePlan(source.Number, system.Number, new CloneOptions { VirtualTargetOnly = true });

        Assert.Null(result.Plan);
        Assert.Contains(result.Problems, p => p.Contains("sistema", StringComparison.Ordinal));
    }

    public void Dispose()
    {
        foreach (var disk in _disks)
        {
            disk.Dispose();
        }

        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private VirtualDisk NewDisk(string name, int sizeMiB, string? label = null)
    {
        var disk = VirtualDisk.Create(Path.Combine(_directory, name), sizeMiB, label);
        _disks.Add(disk);
        return disk;
    }

    private static byte[] WriteSampleFile(VirtualDisk disk)
    {
        var volume = disk.Info().Partitions.Single(p => p.Volume?.Label == "ORIGEN").Volume!;
        var data = new byte[8 * 1024 * 1024];
        new Random(42).NextBytes(data);
        File.WriteAllBytes(Path.Combine(volume.Path, "muestra.bin"), data);
        return data;
    }

    private Task<(ClonePlan Plan, CloneResult Result)> Clone(VirtualDisk source, VirtualDisk target, CloneMode mode = CloneMode.UsedBlocks) =>
        Clone(source, target, new CloneOptions { Mode = mode });

    private async Task<(ClonePlan Plan, CloneResult Result)> Clone(VirtualDisk source, VirtualDisk target, CloneOptions options)
    {
        var service = new CloneService(new DiskInventory(), _directory);
        var planned = service.CreatePlan(source.Number, target.Number, options with { VirtualTargetOnly = true });
        Assert.True(planned.Plan is not null, string.Join(" ", planned.Problems));

        // Doble comprobación antes de escribir: el destino es el VHD de esta prueba.
        var plan = planned.Plan!;
        Assert.Equal(BusKind.Virtual, plan.Target.Bus);
        if (plan.Target.Location is not null)
        {
            Assert.Equal(target.FilePath, plan.Target.Location, ignoreCase: true);
        }

        try
        {
            return (plan, await service.RunAsync(plan, null, CancellationToken.None));
        }
        finally
        {
            // El clonado en caliente usa instantáneas: hay que esperar antes de quitar los discos.
            VirtualDisk.NoteSnapshotUse();
        }
    }
}
