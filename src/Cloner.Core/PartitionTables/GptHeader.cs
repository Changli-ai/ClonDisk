using System.Buffers.Binary;
using System.IO.Hashing;

namespace Cloner.Core.PartitionTables;

/// <summary>Cabecera de una tabla de particiones GUID (GPT), según la especificación UEFI.</summary>
internal sealed record GptHeader
{
    public const int MinimumHeaderSize = 92;
    public const int MinimumEntrySize = 128;
    private const uint MaximumEntryCount = 16384;

    public required int SectorSize { get; init; }
    public required uint Revision { get; init; }
    public required int HeaderSize { get; init; }
    public required long MyLba { get; init; }
    public required long AlternateLba { get; init; }
    public required long FirstUsableLba { get; init; }
    public required long LastUsableLba { get; init; }
    public required Guid DiskGuid { get; init; }
    public required long PartitionEntryLba { get; init; }
    public required uint EntryCount { get; init; }
    public required uint EntrySize { get; init; }
    public required uint EntriesCrc { get; init; }

    private static ReadOnlySpan<byte> Signature => "EFI PART"u8;

    public long EntriesBytes => (long)EntryCount * EntrySize;

    /// <summary>Sectores que ocupa la matriz de entradas.</summary>
    public int EntriesSectors => checked((int)((EntriesBytes + SectorSize - 1) / SectorSize));

    /// <summary>Interpreta un sector completo; nulo si no es una cabecera GPT válida (firma, tamaños o CRC).</summary>
    public static GptHeader? TryParse(ReadOnlySpan<byte> sector)
    {
        if (sector.Length < 512 || !sector[..8].SequenceEqual(Signature))
        {
            return null;
        }

        var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(sector[12..]);
        if (headerSize < MinimumHeaderSize || headerSize > sector.Length)
        {
            return null;
        }

        var header = sector[..(int)headerSize];
        if (ComputeCrc(header) != BinaryPrimitives.ReadUInt32LittleEndian(header[16..]))
        {
            return null;
        }

        var entryCount = BinaryPrimitives.ReadUInt32LittleEndian(header[80..]);
        var entrySize = BinaryPrimitives.ReadUInt32LittleEndian(header[84..]);
        if (entryCount == 0 || entryCount > MaximumEntryCount || entrySize < MinimumEntrySize || entrySize % MinimumEntrySize != 0)
        {
            return null;
        }

        return new GptHeader
        {
            SectorSize = sector.Length,
            Revision = BinaryPrimitives.ReadUInt32LittleEndian(header[8..]),
            HeaderSize = (int)headerSize,
            MyLba = BinaryPrimitives.ReadInt64LittleEndian(header[24..]),
            AlternateLba = BinaryPrimitives.ReadInt64LittleEndian(header[32..]),
            FirstUsableLba = BinaryPrimitives.ReadInt64LittleEndian(header[40..]),
            LastUsableLba = BinaryPrimitives.ReadInt64LittleEndian(header[48..]),
            DiskGuid = new Guid(header.Slice(56, 16)),
            PartitionEntryLba = BinaryPrimitives.ReadInt64LittleEndian(header[72..]),
            EntryCount = entryCount,
            EntrySize = entrySize,
            EntriesCrc = BinaryPrimitives.ReadUInt32LittleEndian(header[88..]),
        };
    }

    /// <summary>Comprueba el CRC de la matriz de entradas.</summary>
    public bool EntriesMatch(ReadOnlySpan<byte> entries) =>
        entries.Length >= EntriesBytes && Crc32.HashToUInt32(entries[..(int)EntriesBytes]) == EntriesCrc;

    /// <summary>Genera un sector de cabecera con estas entradas y la posición indicada, con su CRC.</summary>
    public byte[] Encode(long myLba, long alternateLba, long lastUsableLba, long partitionEntryLba)
    {
        var sector = new byte[SectorSize];
        var span = sector.AsSpan();
        Signature.CopyTo(span);
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], Revision);
        BinaryPrimitives.WriteUInt32LittleEndian(span[12..], (uint)HeaderSize);
        BinaryPrimitives.WriteInt64LittleEndian(span[24..], myLba);
        BinaryPrimitives.WriteInt64LittleEndian(span[32..], alternateLba);
        BinaryPrimitives.WriteInt64LittleEndian(span[40..], FirstUsableLba);
        BinaryPrimitives.WriteInt64LittleEndian(span[48..], lastUsableLba);
        DiskGuid.TryWriteBytes(span.Slice(56, 16));
        BinaryPrimitives.WriteInt64LittleEndian(span[72..], partitionEntryLba);
        BinaryPrimitives.WriteUInt32LittleEndian(span[80..], EntryCount);
        BinaryPrimitives.WriteUInt32LittleEndian(span[84..], EntrySize);
        BinaryPrimitives.WriteUInt32LittleEndian(span[88..], EntriesCrc);
        BinaryPrimitives.WriteUInt32LittleEndian(span[16..], ComputeCrc(span[..HeaderSize]));
        return sector;
    }

    /// <summary>CRC32 de la cabecera con su propio campo de CRC a cero.</summary>
    private static uint ComputeCrc(ReadOnlySpan<byte> header)
    {
        var copy = header.ToArray();
        copy.AsSpan(16, 4).Clear();
        return Crc32.HashToUInt32(copy);
    }
}
