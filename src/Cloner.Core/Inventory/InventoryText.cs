namespace Cloner.Core.Inventory;

/// <summary>Textos en español para mostrar el inventario, compartidos por la CLI y la interfaz.</summary>
public static class InventoryText
{
    /// <summary>Aviso para discos con particiones cifradas (RF-04).</summary>
    public const string EncryptedPartitionsWarning =
        "Hay particiones cifradas con BitLocker: un clon hecho con Windows en uso quedará sin cifrar.";

    public static string Media(MediaKind media) => media switch
    {
        MediaKind.Hdd => "HDD",
        MediaKind.Ssd => "SSD",
        _ => "Tipo desconocido",
    };

    public static string Bus(BusKind bus) => bus switch
    {
        BusKind.Sata => "SATA",
        BusKind.Nvme => "NVMe",
        BusKind.Usb => "USB",
        BusKind.Ata => "ATA",
        BusKind.Scsi => "SCSI",
        BusKind.Sas => "SAS",
        BusKind.Raid => "RAID",
        BusKind.Sd => "SD",
        BusKind.Mmc => "MMC",
        BusKind.Virtual => "Virtual",
        BusKind.Other => "Otro",
        _ => "Bus desconocido",
    };

    public static string Style(PartitionStyle style) => style switch
    {
        PartitionStyle.Mbr => "MBR",
        PartitionStyle.Gpt => "GPT",
        _ => "Sin inicializar",
    };

    public static string Health(HealthState health) => health switch
    {
        HealthState.Healthy => "Correcto",
        HealthState.Warning => "Advertencia",
        HealthState.Unhealthy => "En mal estado",
        _ => "Desconocido",
    };

    public static string Kind(PartitionKind kind) => kind switch
    {
        PartitionKind.EfiSystem => "Sistema EFI",
        PartitionKind.MicrosoftReserved => "Reservada (MSR)",
        PartitionKind.BasicData => "Datos",
        PartitionKind.Recovery => "Recuperación",
        PartitionKind.Extended => "Extendida",
        PartitionKind.Dynamic => "Disco dinámico",
        PartitionKind.Other => "Otro",
        _ => "Desconocido",
    };

    public static string Encryption(EncryptionState encryption) => encryption switch
    {
        EncryptionState.Off => "No",
        EncryptionState.On => "Sí",
        EncryptionState.Suspended => "Sí (protección en pausa)",
        _ => "Desconocido",
    };

    /// <summary>Una frase por cada problema de salud detectado.</summary>
    public static IReadOnlyList<string> Issues(HealthIssues issues)
    {
        var messages = new List<string>();
        if (issues.HasFlag(HealthIssues.ReportedUnhealthy)) messages.Add("Windows informa de que el disco está en mal estado.");
        if (issues.HasFlag(HealthIssues.ReportedWarning)) messages.Add("Windows informa de una advertencia en el disco.");
        if (issues.HasFlag(HealthIssues.UncorrectedReadErrors)) messages.Add("El disco ha tenido errores de lectura no corregidos.");
        if (issues.HasFlag(HealthIssues.UncorrectedWriteErrors)) messages.Add("El disco ha tenido errores de escritura no corregidos.");
        if (issues.HasFlag(HealthIssues.HighWear)) messages.Add("El SSD ha consumido casi toda su vida útil.");
        if (issues.HasFlag(HealthIssues.HighTemperature)) messages.Add("La temperatura del disco es alta.");
        return messages;
    }

    /// <summary>Marcas de una partición: Windows, arranque, activa, oculta.</summary>
    public static string Flags(PartitionInfo partition)
    {
        ArgumentNullException.ThrowIfNull(partition);

        var flags = new List<string>();
        if (partition.ContainsWindows) flags.Add("Windows");
        if (partition.ContainsBootLoader) flags.Add("Arranque");
        if (partition.IsActive) flags.Add("Activa");
        if (partition.IsHidden) flags.Add("Oculta");
        return string.Join(", ", flags);
    }
}
