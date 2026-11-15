using System.Buffers.Binary;
using Cloner.Core.Devices;

namespace Cloner.Core.Tests.Devices;

public class NtfsUsageTests
{
    private const int Cluster = 4096;

    /// <summary>VOLUME_BITMAP_BUFFER con los clústeres indicados en uso.</summary>
    private static byte[] Bitmap(long clusters, params long[] used)
    {
        var data = new byte[NtfsUsage.BitmapHeaderSize + ((clusters + 7) / 8)];
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(8), clusters);
        foreach (var cluster in used)
        {
            data[NtfsUsage.BitmapHeaderSize + (cluster / 8)] |= (byte)(1 << (int)(cluster % 8));
        }

        return data;
    }

    [Fact]
    public void VolumeDataGivesClusterCountAndSize()
    {
        var data = new byte[NtfsUsage.VolumeDataSize];
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(16), 244_190_645);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(44), 4096);

        Assert.Equal((244_190_645L, 4096), NtfsUsage.ParseVolumeData(data));
    }

    [Fact]
    public void UsedClustersBecomeByteRanges()
    {
        var bitmap = Bitmap(64, 0, 1, 2, 40);

        var ranges = NtfsUsage.UsedRanges(bitmap, 64, Cluster, 64 * Cluster, mergeGap: 0);

        Assert.Equal([new ByteRange(0, 3 * Cluster), new ByteRange(40 * Cluster, Cluster)], ranges);
    }

    [Fact]
    public void SmallGapsAreMerged()
    {
        var bitmap = Bitmap(64, 0, 10);

        var ranges = NtfsUsage.UsedRanges(bitmap, 64, Cluster, 64 * Cluster, mergeGap: 16 * Cluster);

        Assert.Equal([new ByteRange(0, 11 * Cluster)], ranges);
    }

    [Fact]
    public void WholeBytesOfUsedClustersAreHandled()
    {
        var used = Enumerable.Range(8, 24).Select(c => (long)c).ToArray();

        var ranges = NtfsUsage.UsedRanges(Bitmap(64, used), 64, Cluster, 64 * Cluster, mergeGap: 0);

        Assert.Equal([new ByteRange(8 * Cluster, 24 * Cluster)], ranges);
    }

    [Fact]
    public void SpaceAfterTheLastClusterIsAlwaysCopied()
    {
        // El volumen tiene 64 clústeres y 3 sectores más, donde NTFS guarda la copia del sector de arranque.
        var volumeLength = (64L * Cluster) + 1536;

        var ranges = NtfsUsage.UsedRanges(Bitmap(64, 0), 64, Cluster, volumeLength, mergeGap: 0);

        Assert.Equal([new ByteRange(0, Cluster), new ByteRange(64 * Cluster, 1536)], ranges);
    }

    [Fact]
    public void TailIsJoinedToANearbyRange()
    {
        var volumeLength = (64L * Cluster) + 512;

        var ranges = NtfsUsage.UsedRanges(Bitmap(64, 62, 63), 64, Cluster, volumeLength, mergeGap: Cluster);

        Assert.Equal([new ByteRange(62 * Cluster, (2 * Cluster) + 512)], ranges);
    }

    [Fact]
    public void BitmapMustStartAtClusterZero()
    {
        var bitmap = Bitmap(64, 0);
        BinaryPrimitives.WriteInt64LittleEndian(bitmap, 8);

        Assert.Throws<ArgumentException>(() => NtfsUsage.UsedRanges(bitmap, 64, Cluster, 64 * Cluster));
    }
}
