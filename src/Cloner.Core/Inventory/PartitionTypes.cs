namespace Cloner.Core.Inventory;

/// <summary>Clasificación de particiones a partir de su identificador de tipo.</summary>
public static class PartitionTypes
{
    public static readonly Guid EfiSystem = new("c12a7328-f81f-11d2-ba4b-00a0c93ec93b");
    public static readonly Guid MicrosoftReserved = new("e3c9e316-0b5c-4db8-817d-f92df00215ae");
    public static readonly Guid BasicData = new("ebd0a0a2-b9e5-4433-87c0-68b6b72699c7");
    public static readonly Guid Recovery = new("de94bba4-06d1-4d40-a16a-bfd50179d6ac");
    public static readonly Guid LdmMetadata = new("5808c8aa-7e8f-42e0-85d2-e1e90434cfb3");
    public static readonly Guid LdmData = new("af9b60a0-1431-4f62-bc68-3311714a69ad");

    /// <summary>Clasifica un tipo GPT. Acepta el GUID con o sin llaves.</summary>
    public static PartitionKind FromGptType(string? gptType)
    {
        if (!Guid.TryParse(gptType, out var type))
        {
            return PartitionKind.Unknown;
        }

        if (type == EfiSystem) return PartitionKind.EfiSystem;
        if (type == MicrosoftReserved) return PartitionKind.MicrosoftReserved;
        if (type == BasicData) return PartitionKind.BasicData;
        if (type == Recovery) return PartitionKind.Recovery;
        if (type == LdmMetadata || type == LdmData) return PartitionKind.Dynamic;
        return PartitionKind.Other;
    }

    /// <summary>Clasifica un byte de tipo de partición MBR.</summary>
    public static PartitionKind FromMbrType(int mbrType) => mbrType switch
    {
        0x00 => PartitionKind.Unknown,
        0x01 or 0x04 or 0x06 or 0x0B or 0x0C or 0x0E => PartitionKind.BasicData, // FAT12/16/32
        0x07 => PartitionKind.BasicData,                                         // NTFS, exFAT
        0x05 or 0x0F => PartitionKind.Extended,
        0x27 => PartitionKind.Recovery,
        0x42 => PartitionKind.Dynamic,
        0xEF => PartitionKind.EfiSystem,
        _ => PartitionKind.Other,
    };
}
