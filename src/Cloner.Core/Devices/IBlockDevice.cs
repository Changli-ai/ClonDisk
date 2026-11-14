namespace Cloner.Core.Devices;

/// <summary>Un dispositivo de bloques: un disco abierto en crudo o, en pruebas, un sustituto en memoria.</summary>
/// <remarks>Desplazamientos y longitudes deben ser múltiplos de <see cref="SectorSize"/>.</remarks>
internal interface IBlockDevice : IDisposable
{
    /// <summary>Capacidad en bytes.</summary>
    long Length { get; }

    /// <summary>Tamaño del sector lógico en bytes.</summary>
    int SectorSize { get; }

    bool CanWrite { get; }

    /// <summary>Lee exactamente <c>buffer.Length</c> bytes a partir de <paramref name="offset"/>.</summary>
    void Read(long offset, Span<byte> buffer);

    /// <summary>Escribe todo <paramref name="data"/> a partir de <paramref name="offset"/>.</summary>
    void Write(long offset, ReadOnlySpan<byte> data);

    /// <summary>Vacía las cachés hasta el medio físico.</summary>
    void Flush();

    /// <summary>Pide a Windows que vuelva a leer la tabla de particiones del disco.</summary>
    void RefreshLayout();
}

/// <summary>Datos que el propio dispositivo declara, para confirmar que es el disco esperado.</summary>
internal sealed record DeviceFacts
{
    public required int DeviceNumber { get; init; }
    public required long SizeBytes { get; init; }
    public required int SectorSize { get; init; }
    public string? ProductId { get; init; }
    public string? SerialNumber { get; init; }
    public Inventory.BusKind Bus { get; init; }
}

/// <summary>Un volumen montado de un disco, que hay que bloquear antes de copiar.</summary>
/// <param name="Path">Ruta con la forma <c>\\?\Volume{GUID}\</c>.</param>
/// <param name="Name">Nombre para mostrar: letra, etiqueta o número de partición.</param>
internal sealed record VolumeRef(string Path, string Name);
