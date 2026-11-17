using System.Buffers.Binary;
using System.IO.Hashing;
using Cloner.Core.Cloning;
using Cloner.Core.Devices;
using Cloner.Core.FileCopy;
using Cloner.Core.Inventory;
using Cloner.Core.PartitionTables;

namespace Cloner.Core.Tests.Cloning;

/// <summary>Imágenes de disco sintéticas con tablas GPT y MBR válidas, rellenas de datos pseudoaleatorios.</summary>
internal static class DiskImages
{
    public const int Sector = 512;
    public const long MiB = 1024 * 1024;

    public static readonly Guid BasicDataType = new("ebd0a0a2-b9e5-4433-87c0-68b6b72699c7");

    /// <summary>Disco GPT de 128 entradas con particiones (offset y tamaño en bytes).</summary>
    public static byte[] Gpt(long sizeBytes, Guid diskGuid, params (long Offset, long Size)[] partitions)
    {
        var image = new byte[sizeBytes];
        Fill(image, partitions, seed: 1);

        var lastLba = (sizeBytes / Sector) - 1;
        var entries = new byte[128 * 128];
        for (var i = 0; i < partitions.Length; i++)
        {
            var entry = entries.AsSpan(i * 128, 128);
            BasicDataType.TryWriteBytes(entry);
            new Guid(i + 1, 0, 0, [0, 0, 0, 0, 0, 0, 0, 1]).TryWriteBytes(entry[16..]);
            BinaryPrimitives.WriteInt64LittleEndian(entry[32..], partitions[i].Offset / Sector);
            BinaryPrimitives.WriteInt64LittleEndian(entry[40..], ((partitions[i].Offset + partitions[i].Size) / Sector) - 1);
        }

        var header = new GptHeader
        {
            SectorSize = Sector,
            Revision = 0x00010000,
            HeaderSize = GptHeader.MinimumHeaderSize,
            MyLba = 1,
            AlternateLba = lastLba,
            FirstUsableLba = 34,
            LastUsableLba = lastLba - 33,
            DiskGuid = diskGuid,
            PartitionEntryLba = 2,
            EntryCount = 128,
            EntrySize = 128,
            EntriesCrc = Crc32.HashToUInt32(entries),
        };

        image[446 + 4] = 0xEE;
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(446 + 8), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(446 + 12), (uint)Math.Min(lastLba, uint.MaxValue));
        image[510] = 0x55;
        image[511] = 0xAA;

        header.Encode(1, lastLba, lastLba - 33, 2).CopyTo(image, Sector);
        entries.CopyTo(image, 2 * Sector);
        entries.CopyTo(image, (lastLba - 32) * Sector);
        header.Encode(lastLba, 1, lastLba - 33, lastLba - 32).CopyTo(image, lastLba * Sector);
        return image;
    }

    /// <summary>Disco MBR con particiones primarias NTFS (offset y tamaño en bytes).</summary>
    public static byte[] Mbr(long sizeBytes, uint signature, params (long Offset, long Size)[] partitions)
    {
        var image = new byte[sizeBytes];
        Fill(image, partitions, seed: 2);

        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(440), signature);
        for (var i = 0; i < partitions.Length; i++)
        {
            var entry = image.AsSpan(446 + (i * 16), 16);
            entry[4] = 0x07;
            BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], (uint)(partitions[i].Offset / Sector));
            BinaryPrimitives.WriteUInt32LittleEndian(entry[12..], (uint)(partitions[i].Size / Sector));
        }

        image[510] = 0x55;
        image[511] = 0xAA;
        return image;
    }

    /// <summary>Contenido que no es una tabla de particiones: un destino «usado» por otro sistema.</summary>
    public static byte[] Garbage(long sizeBytes, int seed = 3)
    {
        var image = new byte[sizeBytes];
        new Random(seed).NextBytes(image);
        image[510] = 0;
        return image;
    }

    private static void Fill(byte[] image, (long Offset, long Size)[] partitions, int seed)
    {
        var random = new Random(seed);
        foreach (var (offset, size) in partitions)
        {
            random.NextBytes(image.AsSpan((int)offset, (int)size));
        }
    }
}

/// <summary>Construye los <see cref="DiskInfo"/> que Windows daría para las imágenes de prueba.</summary>
internal static class TestDisks
{
    public static DiskInfo Info(
        int number,
        byte[] image,
        IReadOnlyList<PartitionInfo>? partitions = null,
        BusKind bus = BusKind.Virtual,
        string? serial = null)
    {
        var table = PartitionTable.Read(image.AsSpan(0, PartitionTable.HeadBytes), DiskImages.Sector);
        return new DiskInfo
        {
            Number = number,
            Model = "Microsoft Virtual Disk",
            SerialNumber = serial,
            SizeBytes = image.Length,
            Bus = bus,
            PartitionStyle = table.Style,
            LogicalSectorSize = DiskImages.Sector,
            PhysicalSectorSize = DiskImages.Sector,
            Identifier = table.Identifier,
            DevicePath = $@"\\?\scsi#disk&ven_msft&prod_virtual_disk#{number}",
            Location = $@"C:\pruebas\disco{number}.vhd",
            Health = HealthState.Healthy,
            Partitions = partitions ?? [],
        };
    }

    public static PartitionInfo Partition(int number, long offset, long size, char? letter = null, string? label = null) => new()
    {
        Number = number,
        OffsetBytes = offset,
        SizeBytes = size,
        Kind = PartitionKind.BasicData,
        DriveLetter = letter,
        Volume = new VolumeInfo
        {
            Path = $@"\\?\Volume{{0000000{number}-0000-0000-0000-00000000000{(letter ?? 'x')}}}\",
            FileSystem = "NTFS",
            Label = label,
            SizeBytes = size,
            FreeBytes = size / 2,
        },
    };

    public static PartitionTableInfo Table(byte[] image) =>
        PartitionTable.Read(image.AsSpan(0, PartitionTable.HeadBytes), DiskImages.Sector);
}

/// <summary>Disco en memoria. Comprueba la alineación de cada operación y registra las escrituras.</summary>
internal sealed class MemoryDisk(byte[] data, int sectorSize = DiskImages.Sector)
{
    public byte[] Data { get; } = data;
    public int SectorSize { get; } = sectorSize;
    public List<(long Offset, int Length)> Writes { get; } = [];
    public int LayoutRefreshes { get; private set; }

    /// <summary>Si devuelve una excepción para un desplazamiento, la lectura falla con ella.</summary>
    public Func<long, Exception?>? ReadFault { get; set; }

    /// <summary>Se invoca después de cada escritura.</summary>
    public Action<long>? AfterWrite { get; set; }

    /// <summary>Desplazamientos cuya lectura devuelve un byte alterado (simula una escritura defectuosa).</summary>
    public HashSet<long> CorruptReads { get; } = [];

    public IBlockDevice Open(bool writable) => new View(this, writable);

    private void CheckAlignment(long offset, int length)
    {
        if (offset % SectorSize != 0 || length % SectorSize != 0 || offset < 0 || offset + length > Data.Length)
        {
            throw new InvalidOperationException($"Acceso no alineado o fuera del disco: {offset} + {length}.");
        }
    }

    private sealed class View(MemoryDisk disk, bool writable) : IBlockDevice
    {
        public long Length => disk.Data.Length;
        public int SectorSize => disk.SectorSize;
        public bool CanWrite => writable;

        public void Read(long offset, Span<byte> buffer)
        {
            disk.CheckAlignment(offset, buffer.Length);
            if (disk.ReadFault?.Invoke(offset) is { } fault)
            {
                throw fault;
            }

            disk.Data.AsSpan((int)offset, buffer.Length).CopyTo(buffer);
            if (disk.CorruptReads.Contains(offset))
            {
                buffer[0] ^= 0xFF;
            }
        }

        public void Write(long offset, ReadOnlySpan<byte> data)
        {
            if (!writable)
            {
                throw new InvalidOperationException("Escritura en un disco abierto en solo lectura.");
            }

            disk.CheckAlignment(offset, data.Length);
            data.CopyTo(disk.Data.AsSpan((int)offset));
            disk.Writes.Add((offset, data.Length));
            disk.AfterWrite?.Invoke(offset);
        }

        public void Flush()
        {
        }

        public void RefreshLayout() => disk.LayoutRefreshes++;

        public void Dispose()
        {
        }
    }
}

/// <summary>Acceso a discos en memoria por número, con registro de los bloqueos de volumen.</summary>
internal sealed class FakeDiskAccess : IDiskAccess
{
    public Dictionary<int, MemoryDisk> Disks { get; } = [];
    public List<(IReadOnlyList<VolumeRef> Volumes, bool Dismount)> Locks { get; } = [];
    public List<(int Number, bool Writable)> Opened { get; } = [];

    /// <summary>Si no es nulo, bloquear volúmenes del destino falla con este mensaje.</summary>
    public string? LockFailure { get; set; }

    /// <summary>Nombres de volúmenes del origen que no se dejan bloquear (están en uso).</summary>
    public HashSet<string> BusySourceVolumes { get; } = [];

    /// <summary>Volúmenes conocidos: ruta → disco, posición de la partición y bloques en uso (nulo = no NTFS).</summary>
    public Dictionary<string, (int Disk, long Offset, long Size, IReadOnlyList<ByteRange>? Used)> Volumes { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Volúmenes de cada conjunto de instantáneas creado, y si se completó.</summary>
    public List<(IReadOnlyList<string> Volumes, bool Completed, bool Disposed)> Snapshots { get; } = [];

    /// <summary>Se invoca justo después de crear las instantáneas: simula cambios en el origen en uso.</summary>
    public Action? AfterSnapshot { get; set; }

    /// <summary>Si no es nulo, las instantáneas solo dejan leer hasta aquí, como las reales de NTFS.</summary>
    public long? SnapshotReadableLength { get; set; }

    /// <summary>Si no es nulo, crear instantáneas falla con este mensaje.</summary>
    public string? SnapshotFailure { get; set; }

    public IBlockDevice Open(DiskIdentity disk, bool writable)
    {
        Opened.Add((disk.Number, writable));
        return Disks[disk.Number].Open(writable);
    }

    public IDisposable LockVolumes(IReadOnlyList<VolumeRef> volumes, bool dismount)
    {
        if (dismount && LockFailure is not null)
        {
            throw new CloneException(LockFailure);
        }

        if (!dismount && volumes.FirstOrDefault(v => BusySourceVolumes.Contains(v.Name)) is { } busy)
        {
            throw new CloneException($"El volumen {busy.Name} está en uso.");
        }

        Locks.Add((volumes, dismount));
        return new Releaser();
    }

    public ISnapshotSet CreateSnapshots(IReadOnlyList<string> volumePaths, int sectorSize)
    {
        if (SnapshotFailure is not null)
        {
            throw new CloneException(SnapshotFailure);
        }

        // Cada instantánea congela una copia de la partición en el momento de crearla.
        var frozen = volumePaths.ToDictionary(
            path => path,
            path => (IVolumeSource)Freeze(path),
            StringComparer.OrdinalIgnoreCase);
        Snapshots.Add((volumePaths, false, false));
        AfterSnapshot?.Invoke();
        return new FakeSnapshotSet(this, Snapshots.Count - 1, frozen);
    }

    public FakeFinisher FakeFinisher { get; } = new();

    public ITargetFinisher Finisher => FakeFinisher;

    public IVolumeSource OpenVolume(string volumePath, int sectorSize)
    {
        var (disk, offset, size, used) = Volumes[volumePath];
        var view = new MemoryDisk(Disks[disk].Data.AsSpan((int)offset, (int)size).ToArray());
        return new FakeVolume(view.Open(writable: false), used);
    }

    private FakeVolume Freeze(string volumePath)
    {
        var (disk, offset, size, used) = Volumes[volumePath];
        var copy = Disks[disk].Data.AsSpan((int)offset, (int)size).ToArray();
        return new FakeVolume(new MemoryDisk(copy).Open(writable: false), used, SnapshotReadableLength);
    }

    private sealed class FakeVolume(IBlockDevice device, IReadOnlyList<ByteRange>? used, long? readable = null) : IVolumeSource
    {
        public IBlockDevice Device => device;

        public long Length => readable ?? device.Length;

        public IReadOnlyList<ByteRange>? ReadUsedRanges() => used;

        public void Dispose() => device.Dispose();
    }

    private sealed class FakeSnapshotSet(FakeDiskAccess owner, int index, Dictionary<string, IVolumeSource> volumes) : ISnapshotSet
    {
        public IVolumeSource Open(string volumePath) => volumes[volumePath];

        public string RootFor(string volumePath) => $@"\\?\GLOBALROOT\Instantanea{index}\{volumePath}";

        public void Complete() => owner.Snapshots[index] = owner.Snapshots[index] with { Completed = true };

        public void Dispose() => owner.Snapshots[index] = owner.Snapshots[index] with { Disposed = true };
    }

    private sealed class Releaser : IDisposable
    {
        public void Dispose()
        {
        }
    }
}

/// <summary>Remate falso: «monta» los volúmenes al instante y registra lo que se le pide.</summary>
internal sealed class FakeFinisher : ITargetFinisher
{
    public List<(string Volume, long Bytes)> Extended { get; } = [];
    public List<BootRepair> Repairs { get; } = [];
    public List<long> WaitedOffsets { get; } = [];
    public int QuietWaits { get; private set; }

    /// <summary>Desplazamiento (en el destino) del volumen que contiene Windows, o nulo.</summary>
    public long? WindowsAt { get; set; }

    public static string VolumeAt(long offset) => $@"\\?\Volume{{clon-{offset}}}\";

    public IReadOnlyDictionary<long, string> WaitForVolumes(int diskNumber, IReadOnlyCollection<long> partitionOffsets)
    {
        WaitedOffsets.AddRange(partitionOffsets);
        return partitionOffsets.ToDictionary(o => o, VolumeAt);
    }

    public void ExtendVolume(string volumePath, long partitionBytes, int sectorSize) => Extended.Add((volumePath, partitionBytes));

    public string? FindWindowsVolume(IEnumerable<string> volumePaths) =>
        WindowsAt is { } offset && volumePaths.Contains(VolumeAt(offset)) ? VolumeAt(offset) : null;

    public void RepairBoot(BootRepair repair) => Repairs.Add(repair);

    public void WaitBeforeRemovingVolumes(Action<int>? secondsLeft = null)
    {
        QuietWaits++;
        secondsLeft?.Invoke(90);
    }

    public List<string> LettersRemoved { get; } = [];

    public void RemoveDriveLetters(IEnumerable<string> volumePaths) => LettersRemoved.AddRange(volumePaths);

    public List<(string Volume, string Label)> Formatted { get; } = [];
    public List<(string Volume, string Label)> Labels { get; } = [];
    public List<(string Source, string Target)> FileCopies { get; } = [];

    /// <summary>Si no es nulo, la copia por archivos lanza esta excepción.</summary>
    public Exception? FileCopyFailure { get; set; }

    public IReadOnlyList<string> FileCopyErrors { get; set; } = [];

    public List<string> FileSystems { get; } = [];

    public void FormatVolume(string volumePath, string label, string fileSystem = "NTFS")
    {
        Formatted.Add((volumePath, label));
        FileSystems.Add(fileSystem);
    }

    /// <summary>Si no es nula, poner la etiqueta falla con esta excepción.</summary>
    public Exception? LabelFailure { get; set; }

    public void SetVolumeLabel(string volumePath, string label)
    {
        if (LabelFailure is not null)
        {
            throw LabelFailure;
        }

        Labels.Add((volumePath, label));
    }

    public FileCopyResult CopyFiles(string sourceRoot, string targetRoot, CopyExclusions exclusions, Action<long, string>? progress, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (FileCopyFailure is not null)
        {
            throw FileCopyFailure;
        }

        FileCopies.Add((sourceRoot, targetRoot));
        progress?.Invoke(1000, @"\archivo.txt");
        return new FileCopyResult(10, 2, 0, 1000, 1, FileCopyErrors);
    }
}

/// <summary>Recoge los avisos de progreso en el mismo hilo, sin contexto de sincronización.</summary>
internal sealed class ProgressLog : IProgress<CloneProgress>
{
    private readonly Lock _lock = new();

    public List<CloneProgress> Reports { get; } = [];

    public void Report(CloneProgress value)
    {
        lock (_lock)
        {
            Reports.Add(value);
        }
    }
}

/// <summary>Reloj que solo avanza cuando la prueba lo pide.</summary>
internal sealed class ManualTime : TimeProvider
{
    private long _ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => Interlocked.Read(ref _ticks);

    public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
}
