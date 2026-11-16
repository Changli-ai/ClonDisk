using System.Buffers.Binary;
using Cloner.Core.Inventory;
using Cloner.Core.PartitionTables;

namespace Cloner.Core.Layout;

/// <summary>
/// Valores de <c>HKLM\SYSTEM\MountedDevices</c>: Windows asigna las letras de unidad según la
/// identidad de cada partición. En GPT es «DMIO:ID:» seguido del GUID de la partición; en MBR,
/// la firma del disco seguida del desplazamiento de la partición. Si el clon cambia esos datos,
/// hay que traducir los valores para que Windows encuentre C: y el resto de letras.
/// </summary>
internal static class MountedDevices
{
    private static ReadOnlySpan<byte> GptPrefix => "DMIO:ID:"u8;

    /// <summary>
    /// Pares (identidad en el origen, identidad en el clon) de las particiones que cambian. Con
    /// conversión (RF-17), la identidad del clon tiene la forma del estilo nuevo.
    /// </summary>
    public static List<(byte[] Old, byte[] New)> Map(
        ReadOnlySpan<byte> sourceHead,
        PartitionTableInfo table,
        TargetLayout layout,
        TableIdentity? identity,
        int sectorSize)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(layout);

        var map = new List<(byte[], byte[])>();
        if (table.Style == PartitionStyle.Raw)
        {
            return map;
        }

        var sourceGuids = SourceGuids(sourceHead, table, sectorSize);
        var signature = BinaryPrimitives.ReadUInt32LittleEndian(sourceHead[440..]);
        var targetGpt = (layout.Style ?? table.Style) == PartitionStyle.Gpt;
        foreach (var placement in layout.Placements)
        {
            var offset = placement.Partition.OffsetBytes;
            byte[] oldValue;
            if (table.Gpt is not null)
            {
                if (!sourceGuids.TryGetValue(offset, out var oldGuid))
                {
                    continue;
                }

                oldValue = GptValue(oldGuid);
            }
            else
            {
                oldValue = MbrValue(signature, offset);
            }

            byte[] newValue;
            if (targetGpt)
            {
                var newGuid = identity is not null && identity.PartitionGuids.TryGetValue(offset, out var replaced)
                    ? replaced
                    : sourceGuids.GetValueOrDefault(offset);
                if (newGuid == Guid.Empty)
                {
                    continue;
                }

                newValue = GptValue(newGuid);
            }
            else
            {
                newValue = MbrValue(identity?.MbrSignature ?? signature, placement.TargetOffset);
            }

            if (!oldValue.AsSpan().SequenceEqual(newValue))
            {
                map.Add((oldValue, newValue));
            }
        }

        return map;
    }

    /// <summary>GUID único de cada partición de un origen GPT, por su desplazamiento.</summary>
    private static Dictionary<long, Guid> SourceGuids(ReadOnlySpan<byte> sourceHead, PartitionTableInfo table, int sectorSize)
    {
        var guids = new Dictionary<long, Guid>();
        if (table.Gpt is not { } gpt)
        {
            return guids;
        }

        var entries = sourceHead.Slice((int)(gpt.PartitionEntryLba * sectorSize), gpt.EntriesSectors * sectorSize);
        for (var i = 0; i < gpt.EntryCount; i++)
        {
            var entry = entries.Slice(i * (int)gpt.EntrySize, (int)gpt.EntrySize);
            if (entry[..16].IndexOfAnyExcept((byte)0) < 0)
            {
                continue;
            }

            guids[BinaryPrimitives.ReadInt64LittleEndian(entry[32..]) * sectorSize] = new Guid(entry.Slice(16, 16));
        }

        return guids;
    }

    /// <summary>Sustituye en una lista de valores los que identifican particiones del origen.</summary>
    public static int Translate(IDictionary<string, byte[]> values, IReadOnlyList<(byte[] Old, byte[] New)> map)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(map);

        var changed = 0;
        foreach (var name in values.Keys.ToList())
        {
            var replacement = map.FirstOrDefault(pair => pair.Old.AsSpan().SequenceEqual(values[name]));
            if (replacement.New is not null)
            {
                values[name] = replacement.New;
                changed++;
            }
        }

        return changed;
    }

    public static byte[] GptValue(Guid partition)
    {
        var value = new byte[GptPrefix.Length + 16];
        GptPrefix.CopyTo(value);
        partition.TryWriteBytes(value.AsSpan(GptPrefix.Length));
        return value;
    }

    public static byte[] MbrValue(uint signature, long offset)
    {
        var value = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(value, signature);
        BinaryPrimitives.WriteInt64LittleEndian(value.AsSpan(4), offset);
        return value;
    }
}
