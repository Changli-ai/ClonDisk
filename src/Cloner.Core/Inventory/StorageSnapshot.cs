namespace Cloner.Core.Inventory;

// Datos tal como los entrega Windows (Storage Management API), sin interpretar.
// DiskInventoryBuilder los convierte en el modelo público; así la interpretación
// se puede probar sin tocar discos.

internal sealed record RawDisk
{
    public required uint Number { get; init; }
    public string? FriendlyName { get; init; }
    public string? Model { get; init; }
    public string? SerialNumber { get; init; }
    public ulong Size { get; init; }
    public ushort BusType { get; init; }
    public ushort PartitionStyle { get; init; }
    public uint LogicalSectorSize { get; init; }
    public uint PhysicalSectorSize { get; init; }
    public bool IsBoot { get; init; }
    public bool IsSystem { get; init; }
    public bool IsOffline { get; init; }
    public bool IsReadOnly { get; init; }
    public ushort HealthStatus { get; init; }
    public uint? Signature { get; init; }
    public string? Guid { get; init; }
}

internal sealed record RawPhysicalDisk
{
    /// <summary>Coincide con el número de disco en configuraciones sin espacios de almacenamiento.</summary>
    public required string DeviceId { get; init; }

    public ushort MediaType { get; init; }
    public ushort HealthStatus { get; init; }

    /// <summary>Nulo si no se pudo leer.</summary>
    public RawReliability? Reliability { get; init; }
}

internal sealed record RawReliability
{
    public int? Temperature { get; init; }
    public long? ReadErrorsUncorrected { get; init; }
    public long? WriteErrorsUncorrected { get; init; }
    public int? Wear { get; init; }
    public long? PowerOnHours { get; init; }
}

internal sealed record RawPartition
{
    public required uint DiskNumber { get; init; }
    public required uint PartitionNumber { get; init; }
    public ulong Offset { get; init; }
    public ulong Size { get; init; }
    public string? GptType { get; init; }
    public ushort? MbrType { get; init; }
    public char DriveLetter { get; init; }
    public bool IsBoot { get; init; }
    public bool IsSystem { get; init; }
    public bool IsActive { get; init; }
    public bool IsHidden { get; init; }
    public IReadOnlyList<string> AccessPaths { get; init; } = [];
}

internal sealed record RawVolume
{
    public required string Path { get; init; }
    public string? FileSystem { get; init; }
    public string? Label { get; init; }
    public ulong Size { get; init; }
    public ulong SizeRemaining { get; init; }
}

internal sealed record RawEncryption
{
    /// <summary>Ruta del volumen, con la forma <c>\\?\Volume{GUID}\</c>.</summary>
    public required string VolumePath { get; init; }

    /// <summary>0 = desactivada, 1 = activada, 2 = desconocida.</summary>
    public uint ProtectionStatus { get; init; }

    /// <summary>0 = totalmente descifrado; otro valor = cifrado o en conversión. Nulo si no está disponible.</summary>
    public uint? ConversionStatus { get; init; }
}

internal sealed record StorageSnapshot
{
    public IReadOnlyList<RawDisk> Disks { get; init; } = [];
    public IReadOnlyList<RawPhysicalDisk> PhysicalDisks { get; init; } = [];
    public IReadOnlyList<RawPartition> Partitions { get; init; } = [];
    public IReadOnlyList<RawVolume> Volumes { get; init; } = [];

    /// <summary>Nulo si no se pudo consultar el cifrado (requiere administrador).</summary>
    public IReadOnlyList<RawEncryption>? Encryption { get; init; }
}

internal interface IStorageReader
{
    StorageSnapshot Read();
}
