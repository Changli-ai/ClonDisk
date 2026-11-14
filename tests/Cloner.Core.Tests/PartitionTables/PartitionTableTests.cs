using System.Buffers.Binary;
using Cloner.Core.Inventory;
using Cloner.Core.PartitionTables;
using Cloner.Core.Tests.Cloning;
using static Cloner.Core.Tests.Cloning.DiskImages;

namespace Cloner.Core.Tests.PartitionTables;

public class PartitionTableTests
{
    private static readonly Guid DiskGuid = new("0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0");

    private static byte[] Head(byte[] image) => image[..PartitionTable.HeadBytes];

    [Fact]
    public void GptDiskIsReadWithItsGuid()
    {
        var image = Gpt(8 * MiB, DiskGuid, (MiB, 6 * MiB));

        var table = PartitionTable.Read(Head(image), Sector);

        Assert.Equal(PartitionStyle.Gpt, table.Style);
        Assert.Equal("0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0", table.Identifier);
        Assert.Null(table.Defect);
        Assert.NotNull(table.Gpt);
        Assert.Equal((8 * MiB / Sector) - 1, table.Gpt.AlternateLba);
        Assert.Equal(32, table.Gpt.EntriesSectors);
        Assert.Equal(34 * Sector, PartitionTable.TableBytes(table, Sector));
    }

    [Fact]
    public void MbrDiskIsReadWithItsSignature()
    {
        var table = PartitionTable.Read(Head(Mbr(8 * MiB, 0x00C0FFEE, (MiB, 6 * MiB))), Sector);

        Assert.Equal(PartitionStyle.Mbr, table.Style);
        Assert.Equal("00C0FFEE", table.Identifier);
        Assert.Null(table.Gpt);
        Assert.Equal(Sector, PartitionTable.TableBytes(table, Sector));
    }

    [Fact]
    public void DiskWithoutBootSignatureIsRaw()
    {
        var table = PartitionTable.Read(Head(Garbage(4 * MiB)), Sector);

        Assert.Equal(PartitionStyle.Raw, table.Style);
        Assert.Null(table.Identifier);
    }

    [Fact]
    public void DamagedEntriesAreReportedAsDefect()
    {
        var image = Gpt(8 * MiB, DiskGuid, (MiB, 6 * MiB));
        image[2 * Sector + 40] ^= 0xFF;

        var table = PartitionTable.Read(Head(image), Sector);

        Assert.Equal(PartitionStyle.Gpt, table.Style);
        Assert.NotNull(table.Defect);
    }

    [Fact]
    public void DamagedHeaderIsReportedAsDefect()
    {
        var image = Gpt(8 * MiB, DiskGuid, (MiB, 6 * MiB));
        image[Sector + 60] ^= 0xFF;

        var table = PartitionTable.Read(Head(image), Sector);

        Assert.Equal(PartitionStyle.Gpt, table.Style);
        Assert.Null(table.Gpt);
        Assert.NotNull(table.Defect);
    }

    [Fact]
    public void HeaderSurvivesEncodeAndParse()
    {
        var gpt = TestDisks.Table(Gpt(8 * MiB, DiskGuid, (MiB, MiB))).Gpt!;

        var parsed = GptHeader.TryParse(gpt.Encode(1, 999, 900, 2));

        Assert.NotNull(parsed);
        Assert.Equal(gpt with { AlternateLba = 999, LastUsableLba = 900 }, parsed);
    }

    [Fact]
    public void AdjustedPrimaryGptPointsToTheEndOfTheLargerDisk()
    {
        var image = Gpt(8 * MiB, DiskGuid, (MiB, 6 * MiB));
        var original = TestDisks.Table(image);
        var head = Head(image);
        var targetSectors = 12 * MiB / Sector;

        PartitionTable.AdjustPrimaryGpt(head, original.Gpt!, targetSectors);

        var adjusted = PartitionTable.Read(head, Sector);
        Assert.Null(adjusted.Defect);
        Assert.Equal(original.Identifier, adjusted.Identifier);
        Assert.Equal(targetSectors - 1, adjusted.Gpt!.AlternateLba);
        Assert.Equal(targetSectors - 34, adjusted.Gpt.LastUsableLba);
        Assert.Equal(original.Gpt!.FirstUsableLba, adjusted.Gpt.FirstUsableLba);
        Assert.Equal(original.Gpt.EntriesCrc, adjusted.Gpt.EntriesCrc);
        Assert.Equal((uint)(targetSectors - 1), BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(446 + 12)));
    }

    [Fact]
    public void BackupGptIsBuiltForTheLastSectorsOfTheTarget()
    {
        var image = Gpt(8 * MiB, DiskGuid, (MiB, 6 * MiB));
        var gpt = TestDisks.Table(image).Gpt!;
        var targetSectors = 12 * MiB / Sector;

        var backup = PartitionTable.BuildBackupGpt(Head(image), gpt, targetSectors);

        Assert.Equal(33 * Sector, backup.Length);
        var header = GptHeader.TryParse(backup.AsSpan(32 * Sector, Sector));
        Assert.NotNull(header);
        Assert.Equal(targetSectors - 1, header.MyLba);
        Assert.Equal(1, header.AlternateLba);
        Assert.Equal(targetSectors - 33, header.PartitionEntryLba);
        Assert.Equal(targetSectors - 34, header.LastUsableLba);
        Assert.True(header.EntriesMatch(backup.AsSpan(0, 32 * Sector)));
    }
}
