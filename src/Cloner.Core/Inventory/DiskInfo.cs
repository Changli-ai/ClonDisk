namespace Cloner.Core.Inventory;

/// <summary>Tecnología del medio de almacenamiento.</summary>
public enum MediaKind
{
    Unknown,
    Hdd,
    Ssd,
}

/// <summary>Bus por el que el disco se conecta al equipo.</summary>
public enum BusKind
{
    Unknown,
    Sata,
    Nvme,
    Usb,
    Ata,
    Scsi,
    Sas,
    Raid,
    Sd,
    Mmc,
    Virtual,
    Other,
}

/// <summary>Estilo de la tabla de particiones.</summary>
public enum PartitionStyle
{
    /// <summary>Disco sin inicializar.</summary>
    Raw,
    Mbr,
    Gpt,
}

/// <summary>Estado de salud que declara Windows para el disco.</summary>
public enum HealthState
{
    Unknown,
    Healthy,
    Warning,
    Unhealthy,
}

/// <summary>Contadores de fiabilidad (SMART). Cada valor es nulo si el disco no lo informa.</summary>
public sealed record DiskReliability
{
    public int? TemperatureCelsius { get; init; }
    public long? ReadErrorsUncorrected { get; init; }
    public long? WriteErrorsUncorrected { get; init; }

    /// <summary>Porcentaje de vida útil consumida (0–100), en SSD.</summary>
    public int? WearPercent { get; init; }

    public long? PowerOnHours { get; init; }
}

/// <summary>Un disco físico (o virtual montado) con su mapa de particiones.</summary>
public sealed record DiskInfo
{
    /// <summary>Índice del disco en Windows (el N de <c>\\.\PhysicalDriveN</c>). Puede cambiar entre arranques.</summary>
    public required int Number { get; init; }

    public required string Model { get; init; }
    public string? SerialNumber { get; init; }
    public required long SizeBytes { get; init; }
    public MediaKind Media { get; init; }
    public BusKind Bus { get; init; }
    public PartitionStyle PartitionStyle { get; init; }
    public int LogicalSectorSize { get; init; }
    public int PhysicalSectorSize { get; init; }

    /// <summary>GUID del disco (GPT) o firma en hexadecimal (MBR). Nulo en discos sin inicializar.</summary>
    public string? Identifier { get; init; }

    /// <summary>Contiene la partición desde la que se ejecuta Windows.</summary>
    public bool ContainsWindows { get; init; }

    /// <summary>Contiene la partición con el cargador de arranque.</summary>
    public bool ContainsBootLoader { get; init; }

    public bool IsOffline { get; init; }
    public bool IsReadOnly { get; init; }
    public HealthState Health { get; init; }

    /// <summary>Nulo si no se pudo leer (requiere administrador) o el disco no lo ofrece.</summary>
    public DiskReliability? Reliability { get; init; }

    public required IReadOnlyList<PartitionInfo> Partitions { get; init; }

    /// <summary>El disco participa en el arranque del sistema en uso.</summary>
    public bool IsSystemDisk => ContainsWindows || ContainsBootLoader;

    /// <summary>Espacio que no pertenece a ninguna partición.</summary>
    public long UnallocatedBytes
    {
        get
        {
            // Una partición extendida solo es el contenedor de las lógicas: no se suma.
            var allocated = Partitions.Where(p => p.Kind != PartitionKind.Extended).Sum(p => p.SizeBytes);
            return Math.Max(0, SizeBytes - allocated);
        }
    }
}
