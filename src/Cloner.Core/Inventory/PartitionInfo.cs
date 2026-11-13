namespace Cloner.Core.Inventory;

/// <summary>Función de una partición, deducida de su tipo GPT o MBR.</summary>
public enum PartitionKind
{
    Unknown,
    EfiSystem,
    MicrosoftReserved,
    BasicData,
    Recovery,
    Extended,
    Dynamic,
    Other,
}

/// <summary>Estado del cifrado BitLocker o cifrado de dispositivo de un volumen.</summary>
public enum EncryptionState
{
    /// <summary>No se pudo determinar (requiere administrador) o no aplica.</summary>
    Unknown,
    Off,
    On,

    /// <summary>El volumen está cifrado pero la protección está desactivada o en pausa.</summary>
    Suspended,
}

/// <summary>Sistema de archivos montado sobre una partición.</summary>
public sealed record VolumeInfo
{
    /// <summary>Ruta del volumen, con la forma <c>\\?\Volume{GUID}\</c>.</summary>
    public required string Path { get; init; }

    public string? FileSystem { get; init; }
    public string? Label { get; init; }
    public long SizeBytes { get; init; }
    public long FreeBytes { get; init; }
    public long UsedBytes => Math.Max(0, SizeBytes - FreeBytes);
}

/// <summary>Una partición de un disco.</summary>
public sealed record PartitionInfo
{
    public required int Number { get; init; }
    public required long OffsetBytes { get; init; }
    public required long SizeBytes { get; init; }
    public PartitionKind Kind { get; init; }

    /// <summary>GUID de tipo (GPT) o byte de tipo como <c>0x07</c> (MBR).</summary>
    public string? TypeId { get; init; }

    public char? DriveLetter { get; init; }

    /// <summary>Es la partición desde la que se ejecuta Windows.</summary>
    public bool ContainsWindows { get; init; }

    /// <summary>Es la partición con el cargador de arranque (EFI o activa en MBR).</summary>
    public bool ContainsBootLoader { get; init; }

    public bool IsActive { get; init; }
    public bool IsHidden { get; init; }

    /// <summary>Nulo si la partición no tiene un volumen reconocido por Windows.</summary>
    public VolumeInfo? Volume { get; init; }

    public EncryptionState Encryption { get; init; }
}
