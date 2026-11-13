using System.Management;
using System.Runtime.InteropServices;

namespace Cloner.Core.Inventory;

/// <summary>Inventario de los discos del equipo. Operación de solo lectura.</summary>
public interface IDiskInventory
{
    /// <summary>Devuelve los discos presentes, ordenados por número.</summary>
    /// <exception cref="DiskInventoryException">Windows no permitió consultar los discos.</exception>
    IReadOnlyList<DiskInfo> GetDisks();
}

/// <summary>Error al consultar los discos del equipo.</summary>
public sealed class DiskInventoryException : Exception
{
    public DiskInventoryException()
    {
    }

    public DiskInventoryException(string message)
        : base(message)
    {
    }

    public DiskInventoryException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Inventario obtenido de Windows mediante la Storage Management API.</summary>
public sealed class DiskInventory : IDiskInventory
{
    private readonly IStorageReader _reader;

    public DiskInventory()
        : this(new WmiStorageReader())
    {
    }

    internal DiskInventory(IStorageReader reader)
    {
        _reader = reader;
    }

    public IReadOnlyList<DiskInfo> GetDisks()
    {
        StorageSnapshot snapshot;
        try
        {
            snapshot = _reader.Read();
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException)
        {
            throw new DiskInventoryException($"No se pudieron consultar los discos del equipo: {ex.Message}", ex);
        }

        return DiskInventoryBuilder.Build(snapshot);
    }
}
