using System.Buffers.Binary;
using Cloner.Core.Cloning;
using Cloner.Core.Inventory;
using Cloner.Core.Layout;
using Cloner.Core.PartitionTables;
using Microsoft.Extensions.Logging.Abstractions;
using static Cloner.Core.Tests.Cloning.DiskImages;

namespace Cloner.Core.Tests.Cloning;

/// <summary>Conversión de la tabla del destino entre MBR y GPT (RF-17).</summary>
public class ConversionTests
{
    private static readonly CloneEnvironment WithSnapshots = new() { SnapshotsAvailable = true };

    private static PartitionInfo Windows(int number, long offset, long size) =>
        TestDisks.Partition(number, offset, size, 'C') with { ContainsWindows = true };

    private static PartitionInfo Efi(int number, long offset, long size)
    {
        var partition = TestDisks.Partition(number, offset, size) with { Kind = PartitionKind.EfiSystem, ContainsBootLoader = true };
        return partition with { Volume = partition.Volume! with { FileSystem = "FAT32" } };
    }

    private static PartitionInfo Msr(int number, long offset, long size) =>
        TestDisks.Partition(number, offset, size) with { Kind = PartitionKind.MicrosoftReserved, Volume = null };

    private static ClonePlan PlanOrThrow(DiskInfo source, PartitionTableInfo table, DiskInfo target, PartitionStyle style)
    {
        var result = ClonePlanner.Create(source, target, table, WithSnapshots, new CloneOptions { TargetStyle = style });
        return result.Plan ?? throw new InvalidOperationException(string.Join(" ", result.Problems));
    }

    [Fact]
    public void SameStyleIsNotAConversion()
    {
        var image = Gpt(8 * MiB, Guid.NewGuid(), (MiB, 6 * MiB));
        var source = TestDisks.Info(1, image, [TestDisks.Partition(1, MiB, 6 * MiB, 'E')]);

        var plan = PlanOrThrow(source, TestDisks.Table(image), TestDisks.Info(2, Garbage(8 * MiB)), PartitionStyle.Gpt);

        Assert.Null(plan.Layout.Style);
        Assert.Null(plan.NewIdentity);
    }

    [Fact]
    public void GptDataDiskBecomesMbrWithWindowsBootCode()
    {
        var image = Gpt(48 * MiB, Guid.NewGuid(), (MiB, 4 * MiB), (5 * MiB, 40 * MiB));
        var source = TestDisks.Info(1, image, [Msr(1, MiB, 4 * MiB), TestDisks.Partition(2, 5 * MiB, 40 * MiB, 'E', "Datos")]);
        var targetImage = Garbage(48 * MiB);
        var plan = PlanOrThrow(source, TestDisks.Table(image), TestDisks.Info(2, targetImage), PartitionStyle.Mbr);

        Assert.Equal(PartitionStyle.Mbr, plan.TargetStyle);
        Assert.NotNull(plan.NewIdentity?.MbrSignature);
        Assert.DoesNotContain(plan.Steps, s => s.Kind == CloneStepKind.WriteBackupGpt);
        var placement = Assert.Single(plan.Layout.Placements);
        Assert.Equal(2, placement.Partition.Number);
        Assert.Contains(plan.Warnings, w => w.Contains("MSR", StringComparison.Ordinal));

        var access = Access(image, targetImage, source);
        var result = new CloneExecutor(access, NullLogger.Instance).Execute(plan, null, CancellationToken.None);

        Assert.Equal(CloneOutcome.Succeeded, result.Outcome);
        var table = TestDisks.Table(targetImage);
        Assert.Equal(PartitionStyle.Mbr, table.Style);
        Assert.True(targetImage.AsSpan(0, 440).SequenceEqual(MbrBootCode.Create()));
        var entry = targetImage.AsSpan(446, 16);
        Assert.Equal(0x07, entry[4]);
        Assert.Equal(0x00, entry[0]);
        Assert.Equal((uint)(placement.TargetOffset / Sector), BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]));
        Assert.Equal(0, targetImage[446 + 16 + 4]);

        // Los datos de la partición llegan intactos a su sitio.
        Assert.True(targetImage.AsSpan((int)placement.TargetOffset, (int)(40 * MiB)).SequenceEqual(image.AsSpan((int)(5 * MiB), (int)(40 * MiB))));
        Assert.Empty(access.FakeFinisher.Repairs);
    }

    [Fact]
    public void MbrDataDiskBecomesGptWithValidTables()
    {
        var image = Mbr(48 * MiB, 0x12345678, (MiB, 40 * MiB));
        var source = TestDisks.Info(1, image, [TestDisks.Partition(1, MiB, 40 * MiB, 'E', "Datos")]);
        var targetImage = Garbage(48 * MiB);
        var plan = PlanOrThrow(source, TestDisks.Table(image), TestDisks.Info(2, targetImage), PartitionStyle.Gpt);

        Assert.Equal(PartitionStyle.Gpt, plan.TargetStyle);
        Assert.Contains(plan.Steps, s => s.Kind == CloneStepKind.WriteBackupGpt && s.End == 48 * MiB);

        var access = Access(image, targetImage, source);
        var result = new CloneExecutor(access, NullLogger.Instance).Execute(plan, null, CancellationToken.None);

        Assert.Equal(CloneOutcome.Succeeded, result.Outcome);
        var table = TestDisks.Table(targetImage);
        Assert.Equal(PartitionStyle.Gpt, table.Style);
        Assert.Null(table.Defect);
        var entry = targetImage.AsSpan(2 * Sector, 128);
        Assert.Equal(PartitionTypes.BasicData, new Guid(entry[..16]));
        Assert.Equal(MiB / Sector, BinaryPrimitives.ReadInt64LittleEndian(entry[32..]));
        Assert.Equal(((41 * MiB) / Sector) - 1, BinaryPrimitives.ReadInt64LittleEndian(entry[40..]));

        // La copia de respaldo del final coincide con la principal.
        var lastLba = (48 * MiB / Sector) - 1;
        Assert.True(targetImage.AsSpan((int)((lastLba - 32) * Sector), 128 * 128).SequenceEqual(targetImage.AsSpan(2 * Sector, 128 * 128)));
    }

    [Fact]
    public void BootCodeFitsBeforeTheSignatureAndEndsJumpingToTheBootSector()
    {
        var code = MbrBootCode.Create();

        Assert.Equal(440, code.Length);
        Assert.True(code.AsSpan(94, 5).SequenceEqual(new byte[] { 0xEA, 0x00, 0x7C, 0x00, 0x00 }));
        Assert.True(code.AsSpan(99).IndexOfAnyExcept((byte)0) < 0);

        // Cada salto relativo cae en la instrucción prevista.
        Assert.Equal(0x80, code[0x24]);                    // cmp byte [si], 80h (destino de loop)
        Assert.Equal(0x24, 0x2E + (sbyte)code[0x2D]);      // loop
        Assert.Equal(0x37, 0x29 + code[0x28]);             // je hacia la lectura
        Assert.Equal(0x89, code[0x37]);                    // mov bp, si
        Assert.Equal(0xCD, code[0x2E]);                    // int 18h
        Assert.Equal(0x2E, 0x51 + (sbyte)code[0x50]);      // jc tras int 13h
        Assert.Equal(0x2E, 0x5C + (sbyte)code[0x5B]);      // jne si falta la firma

        // El salto tras copiarse a 0600h cae en la búsqueda de la partición activa (mov si, 07BEh).
        Assert.Equal(0x1E, code[26]);
        Assert.Equal(0xBE, code[0x1E]);
        Assert.Equal(0x07BE, code[0x1F] | (code[0x20] << 8));
    }

    [Fact]
    public void MoreThanFourPartitionsCannotBecomeMbr()
    {
        var image = Gpt(8 * MiB, Guid.NewGuid());
        var partitions = Enumerable.Range(0, 5).Select(i => TestDisks.Partition(i + 1, (i + 1) * MiB, MiB)).ToList();
        var source = TestDisks.Info(1, image, partitions);

        var result = ClonePlanner.Create(source, TestDisks.Info(2, Garbage(8 * MiB)), TestDisks.Table(image), WithSnapshots, new CloneOptions { TargetStyle = PartitionStyle.Mbr });

        Assert.Null(result.Plan);
        Assert.Contains(result.Problems, p => p.Contains("como mucho 4", StringComparison.Ordinal));
    }

    [Fact]
    public void UnknownPartitionTypesAreNotConverted()
    {
        var image = Gpt(8 * MiB, Guid.NewGuid(), (MiB, 6 * MiB));
        var odd = TestDisks.Partition(1, MiB, 6 * MiB) with { Kind = PartitionKind.Other, TypeId = Guid.NewGuid().ToString() };
        var source = TestDisks.Info(1, image, [odd]);

        var result = ClonePlanner.Create(source, TestDisks.Info(2, Garbage(8 * MiB)), TestDisks.Table(image), WithSnapshots, new CloneOptions { TargetStyle = PartitionStyle.Mbr });

        Assert.Null(result.Plan);
        Assert.Contains(result.Problems, p => p.Contains("no se sabe convertir", StringComparison.Ordinal));
    }

    [Fact]
    public void BiosWindowsBecomesUefiWithANewEfiPartition()
    {
        var image = Mbr(160 * MiB, 0xCAFE0001, (MiB, 30 * MiB));
        var windows = Windows(1, MiB, 30 * MiB) with { IsActive = true };
        var source = TestDisks.Info(1, image, [windows]) with { ContainsWindows = true };
        var targetImage = Garbage(160 * MiB);
        var plan = PlanOrThrow(source, TestDisks.Table(image), TestDisks.Info(2, targetImage), PartitionStyle.Gpt);

        var efi = Assert.Single(plan.Layout.Created, c => c.Kind == PartitionKind.EfiSystem);
        Assert.Equal(MiB, efi.Offset);
        Assert.Equal(StyleConversion.EfiBytes, efi.Size);
        var windowsAt = Assert.Single(plan.Layout.Placements).TargetOffset;
        Assert.Equal(MiB + StyleConversion.EfiBytes + StyleConversion.MsrBytes, windowsAt);
        Assert.Contains(plan.Warnings, w => w.Contains("UEFI", StringComparison.Ordinal));

        var access = Access(image, targetImage, source);
        access.FakeFinisher.WindowsAt = windowsAt;
        var result = new CloneExecutor(access, NullLogger.Instance).Execute(plan, null, CancellationToken.None);

        Assert.Equal(CloneOutcome.Succeeded, result.Outcome);
        var finisher = access.FakeFinisher;
        Assert.Contains((FakeFinisher.VolumeAt(MiB), "EFI"), finisher.Formatted);
        Assert.Contains("FAT32", finisher.FileSystems);
        var repair = Assert.Single(finisher.Repairs);
        Assert.True(repair.Uefi);
        Assert.Equal(FakeFinisher.VolumeAt(MiB), repair.SystemVolume);
        Assert.Equal(FakeFinisher.VolumeAt(windowsAt), repair.WindowsVolume);

        // C: pasa de «firma + desplazamiento» a «DMIO:ID: + GUID de la partición nueva».
        var map = Assert.Single(repair.MountedDevicesMap);
        Assert.Equal(MountedDevices.MbrValue(0xCAFE0001, MiB), map.Old);
        Assert.Equal(MountedDevices.GptValue(plan.NewIdentity!.PartitionGuids[MiB]), map.New);

        // Tras el remate, la EFI ya figura como EFI en la tabla definitiva (en la provisional era de datos).
        Assert.Equal(1, finisher.QuietWaits);
        var first = targetImage.AsSpan(2 * Sector, 128);
        Assert.Equal(PartitionTypes.EfiSystem, new Guid(first[..16]));
        Assert.Null(TestDisks.Table(targetImage).Defect);
    }

    [Fact]
    public void UefiWindowsBecomesBiosWithWindowsActive()
    {
        var image = Gpt(48 * MiB, Guid.NewGuid(), (MiB, 8 * MiB), (9 * MiB, 4 * MiB), (13 * MiB, 30 * MiB));
        var source = TestDisks.Info(1, image, [Efi(1, MiB, 8 * MiB), Msr(2, 9 * MiB, 4 * MiB), Windows(3, 13 * MiB, 30 * MiB)]) with { ContainsWindows = true };
        var targetImage = Garbage(48 * MiB);
        var plan = PlanOrThrow(source, TestDisks.Table(image), TestDisks.Info(2, targetImage), PartitionStyle.Mbr);

        var windowsAt = plan.Layout.Placements.Single(p => p.Partition.ContainsWindows).TargetOffset;
        var access = Access(image, targetImage, source);
        access.FakeFinisher.WindowsAt = windowsAt;
        var result = new CloneExecutor(access, NullLogger.Instance).Execute(plan, null, CancellationToken.None);

        Assert.Equal(CloneOutcome.Succeeded, result.Outcome);
        var repair = Assert.Single(access.FakeFinisher.Repairs);
        Assert.False(repair.Uefi);
        Assert.Equal(repair.WindowsVolume, repair.SystemVolume);
        Assert.Equal(0, access.FakeFinisher.QuietWaits);

        // Dos entradas: la EFI (0xEF) y Windows (0x07, activa). La MSR no pasa.
        Assert.Equal(0xEF, targetImage[446 + 4]);
        Assert.Equal(0x00, targetImage[446]);
        Assert.Equal(0x07, targetImage[446 + 16 + 4]);
        Assert.Equal(0x80, targetImage[446 + 16]);
        Assert.Equal(0, targetImage[446 + 32 + 4]);
        var map = repair.MountedDevicesMap.Single(m => m.Old.AsSpan().SequenceEqual(MountedDevices.GptValue(new Guid(3, 0, 0, [0, 0, 0, 0, 0, 0, 0, 1]))));
        Assert.Equal(MountedDevices.MbrValue(plan.NewIdentity!.MbrSignature!.Value, windowsAt), map.New);
    }

    [Fact]
    public void ConversionThatDoesNotFitShrinksTheMainPartition()
    {
        // MBR de 160 MiB lleno: con la EFI y la MSR nuevas (116 MiB) Windows tiene que reducirse.
        var image = Mbr(4 * MiB, 0xCAFE0002);
        var windows = Windows(1, MiB, 158 * MiB);
        windows = windows with { Volume = windows.Volume! with { FreeBytes = 140 * MiB } };
        var source = TestDisks.Info(1, image, [windows]) with { ContainsWindows = true, SizeBytes = 160 * MiB };
        var target = TestDisks.Info(2, Garbage(4 * MiB)) with { SizeBytes = 160 * MiB };

        var plan = PlanOrThrow(source, TestDisks.Table(image), target, PartitionStyle.Gpt);

        var placement = Assert.Single(plan.Layout.Placements);
        Assert.True(placement.Shrinks);
        Assert.True(placement.TargetEnd <= (160 * MiB) - (33 * Sector));
        Assert.Single(plan.FileSteps);
    }

    private static FakeDiskAccess Access(byte[] sourceImage, byte[] targetImage, DiskInfo source)
    {
        var access = new FakeDiskAccess();
        access.Disks[1] = new MemoryDisk(sourceImage);
        access.Disks[2] = new MemoryDisk(targetImage);
        foreach (var partition in source.Partitions.Where(p => p.Volume is not null))
        {
            access.Volumes[partition.Volume!.Path] = (1, partition.OffsetBytes, partition.SizeBytes, null);
        }

        return access;
    }
}
