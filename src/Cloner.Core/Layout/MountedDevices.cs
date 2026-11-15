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

    /// <summary>Pares (identidad en el origen, identidad en el clon) de las particiones que cambian.</summary>
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
        if (table.Gpt is { } gpt)
        {
            var entries = sourceHead.Slice((int)(gpt.PartitionEntryLba * sectorSize), gpt.EntriesSectors * sectorSize);
            for (var i = 0; i < gpt.EntryCount; i++)
            {
                var entry = entries.Slice(i * (int)gpt.EntrySize, (int)gpt.EntrySize);
                if (entry[..16].IndexOfAnyExcept((byte)0) < 0)
                {
                    continue;
                }

                var offset = BinaryPrimitives.ReadInt64LittleEndian(entry[32..]) * sectorSize;
                var oldGuid = new Guid(entry.Slice(16, 16));
                var newGuid = identity is not null && identity.PartitionGuids.TryGetValue(offset, out var replaced) ? replaced : oldGuid;
                if (newGuid != oldGuid)
                {
                    map.Add((GptValue(oldGuid), GptValue(newGuid)));
                }
            }

            return map;
        }

        if (table.Style == PartitionStyle.Mbr)
        {
            var signature = BinaryPrimitives.ReadUInt32LittleEndian(sourceHead[440..]);
            var newSignature = identity?.MbrSignature ?? signature;
            foreach (var placement in layout.Placements)
            {
                var oldValue = MbrValue(signature, placement.Partition.OffsetBytes);
                var newValue = MbrValue(newSignature, placement.TargetOffset);
                if (!oldValue.AsSpan().SequenceEqual(newValue))
                {
                    map.Add((oldValue, newValue));
                }
            }
        }

        return map;
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
