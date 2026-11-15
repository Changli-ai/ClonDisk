using System.Buffers.Binary;

namespace Cloner.Core.Devices;

/// <summary>Un tramo de bytes [Offset, Offset + Length).</summary>
internal readonly record struct ByteRange(long Offset, long Length)
{
    public long End => Offset + Length;
}

/// <summary>
/// Interpreta las respuestas de <c>FSCTL_GET_NTFS_VOLUME_DATA</c> y <c>FSCTL_GET_VOLUME_BITMAP</c>
/// para saber qué partes de un volumen NTFS están en uso (copia inteligente, RF-12).
/// </summary>
internal static class NtfsUsage
{
    /// <summary>Tamaño de NTFS_VOLUME_DATA_BUFFER sin la extensión opcional.</summary>
    public const int VolumeDataSize = 96;

    /// <summary>Cabecera de VOLUME_BITMAP_BUFFER: StartingLcn y BitmapSize.</summary>
    public const int BitmapHeaderSize = 16;

    /// <summary>Huecos libres menores se copian igualmente: menos operaciones y más largas.</summary>
    public const long DefaultMergeGap = 1024 * 1024;

    /// <summary>NTFS_VOLUME_DATA_BUFFER: TotalClusters en el byte 16 y BytesPerCluster en el 44.</summary>
    public static (long TotalClusters, int BytesPerCluster) ParseVolumeData(ReadOnlySpan<byte> data)
    {
        if (data.Length < VolumeDataSize)
        {
            throw new ArgumentException("Respuesta de NTFS demasiado corta.", nameof(data));
        }

        return (BinaryPrimitives.ReadInt64LittleEndian(data[16..]), checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data[44..])));
    }

    /// <summary>
    /// Tramos del volumen que hay que copiar: los clústeres en uso según el mapa de bits, más el
    /// final del volumen que queda fuera de los clústeres (allí está la copia del sector de
    /// arranque de NTFS). Los huecos menores que <paramref name="mergeGap"/> se incluyen.
    /// </summary>
    /// <param name="bitmapResponse">VOLUME_BITMAP_BUFFER completo, a partir del clúster 0.</param>
    public static List<ByteRange> UsedRanges(
        ReadOnlySpan<byte> bitmapResponse,
        long totalClusters,
        int bytesPerCluster,
        long volumeLength,
        long mergeGap = DefaultMergeGap)
    {
        if (BinaryPrimitives.ReadInt64LittleEndian(bitmapResponse) != 0)
        {
            throw new ArgumentException("El mapa de bits debe empezar en el clúster 0.", nameof(bitmapResponse));
        }

        var clusters = Math.Min(totalClusters, BinaryPrimitives.ReadInt64LittleEndian(bitmapResponse[8..]));
        var bits = bitmapResponse[BitmapHeaderSize..];
        var mergeClusters = Math.Max(0, mergeGap / bytesPerCluster);

        var ranges = new List<ByteRange>();
        long runStart = -1;
        long runEnd = -1;
        long cluster = 0;
        while (cluster < clusters)
        {
            // Salta rápido bytes enteros libres u ocupados.
            var index = (int)(cluster >> 3);
            if ((cluster & 7) == 0 && cluster + 8 <= clusters && (bits[index] == 0 || bits[index] == 0xFF))
            {
                if (bits[index] == 0xFF)
                {
                    Extend(ref runStart, ref runEnd, cluster, cluster + 8, mergeClusters, ranges, bytesPerCluster);
                }

                cluster += 8;
                continue;
            }

            if ((bits[index] & (1 << (int)(cluster & 7))) != 0)
            {
                Extend(ref runStart, ref runEnd, cluster, cluster + 1, mergeClusters, ranges, bytesPerCluster);
            }

            cluster++;
        }

        if (runStart >= 0)
        {
            ranges.Add(new ByteRange(runStart * bytesPerCluster, (runEnd - runStart) * bytesPerCluster));
        }

        // Lo que queda después del último clúster (copia del sector de arranque) se copia siempre.
        var clusterArea = clusters * bytesPerCluster;
        if (volumeLength > clusterArea)
        {
            if (ranges.Count > 0 && clusterArea - ranges[^1].End <= mergeGap)
            {
                var last = ranges[^1];
                ranges[^1] = new ByteRange(last.Offset, volumeLength - last.Offset);
            }
            else
            {
                ranges.Add(new ByteRange(clusterArea, volumeLength - clusterArea));
            }
        }

        return ranges;
    }

    private static void Extend(ref long runStart, ref long runEnd, long start, long end, long mergeClusters, List<ByteRange> ranges, int bytesPerCluster)
    {
        if (runStart >= 0 && start - runEnd <= mergeClusters)
        {
            runEnd = end;
            return;
        }

        if (runStart >= 0)
        {
            ranges.Add(new ByteRange(runStart * bytesPerCluster, (runEnd - runStart) * bytesPerCluster));
        }

        runStart = start;
        runEnd = end;
    }
}
