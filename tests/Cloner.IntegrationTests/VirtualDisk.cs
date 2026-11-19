using System.Diagnostics;
using System.Globalization;
using System.Text;
using Cloner.Core.Inventory;

namespace Cloner.IntegrationTests;

/// <summary>
/// Disco virtual VHD de tamaño fijo, creado y montado con diskpart para las pruebas. Son los
/// únicos discos sobre los que las pruebas escriben. Requiere administrador.
/// </summary>
internal sealed class VirtualDisk : IDisposable
{
    private VirtualDisk(string path, int number)
    {
        FilePath = path;
        Number = number;
    }

    public string FilePath { get; }

    /// <summary>Número de disco que le asignó Windows al montarlo.</summary>
    public int Number { get; private set; }

    public bool IsAttached { get; private set; } = true;

    /// <summary>
    /// Crea y monta un VHD fijo. Con <paramref name="label"/> se inicializa como GPT con una
    /// partición NTFS; sin ella queda sin inicializar.
    /// </summary>
    public static VirtualDisk Create(string path, int sizeMiB, string? label = null)
    {
        var script = new StringBuilder()
            .AppendLine(CultureInfo.InvariantCulture, $"create vdisk file=\"{path}\" maximum={sizeMiB} type=fixed")
            .AppendLine(CultureInfo.InvariantCulture, $"select vdisk file=\"{path}\"")
            .AppendLine("attach vdisk");
        if (label is not null)
        {
            script.AppendLine("convert gpt")
                .AppendLine("create partition primary")
                .AppendLine(CultureInfo.InvariantCulture, $"format fs=ntfs quick label={label}");
        }

        try
        {
            RunDiskPart(script.ToString());
            return new VirtualDisk(path, FindByLocation(path));
        }
        catch
        {
            // Que un fallo a medias no deje el disco montado.
            TryDetach(path);
            throw;
        }
    }

    /// <summary>Crea y monta un VHD GPT como el de un Windows: partición EFI (FAT32) y otra NTFS de datos.</summary>
    public static VirtualDisk CreateWithEfi(string path, int sizeMiB)
    {
        var script = new StringBuilder()
            .AppendLine(CultureInfo.InvariantCulture, $"create vdisk file=\"{path}\" maximum={sizeMiB} type=fixed")
            .AppendLine(CultureInfo.InvariantCulture, $"select vdisk file=\"{path}\"")
            .AppendLine("attach vdisk")
            .AppendLine("convert gpt")
            .AppendLine("create partition efi size=40")
            .AppendLine("format fs=fat32 quick label=EFI")
            .AppendLine("create partition primary")
            .AppendLine("format fs=ntfs quick label=DATOS");
        try
        {
            RunDiskPart(script.ToString());
            return new VirtualDisk(path, FindByLocation(path));
        }
        catch
        {
            TryDetach(path);
            throw;
        }
    }

    /// <summary>Monta el disco; en solo lectura, Windows no puede modificarlo mientras tanto.</summary>
    public void Attach(bool readOnly = false)
    {
        RunDiskPart($"select vdisk file=\"{FilePath}\"\r\nattach vdisk{(readOnly ? " readonly" : string.Empty)}\r\n");
        IsAttached = true;
        Number = FindByLocation(FilePath);
    }

    public void Detach()
    {
        if (IsAttached)
        {
            WaitForSnapshotDriver();
            RunDiskPart($"select vdisk file=\"{FilePath}\"\r\ndetach vdisk\r\n");
            IsAttached = false;
        }
    }

    /// <summary>
    /// Tiempo que se deja pasar entre la última instantánea VSS y quitar un disco. Quitar un
    /// volumen pocos segundos después de liberar una instantánea suya hizo que volsnap dejara un
    /// temporizador en memoria liberada y el equipo se bloqueara (0x0A, 14/11/2026).
    /// </summary>
    public static readonly TimeSpan SnapshotQuietPeriod = TimeSpan.FromSeconds(90);

    private static readonly Lock SnapshotLock = new();
    private static DateTime _lastSnapshotUse = DateTime.MinValue;

    /// <summary>Las pruebas lo llaman cuando terminan de usar instantáneas (también al clonar en caliente).</summary>
    public static void NoteSnapshotUse()
    {
        lock (SnapshotLock)
        {
            _lastSnapshotUse = DateTime.UtcNow;
        }
    }

    private static void WaitForSnapshotDriver()
    {
        DateTime last;
        lock (SnapshotLock)
        {
            last = _lastSnapshotUse;
        }

        var remaining = last + SnapshotQuietPeriod - DateTime.UtcNow;
        if (remaining > TimeSpan.Zero)
        {
            Thread.Sleep(remaining);
        }
    }

    public DiskInfo Info() => new DiskInventory().GetDisks().Single(d => d.Number == Number);

    /// <summary>Contenido de datos del VHD fijo, sin el pie de 512 bytes que lo describe.</summary>
    public byte[] ReadData()
    {
        if (IsAttached)
        {
            throw new InvalidOperationException("Hay que desmontar el disco antes de leer su archivo.");
        }

        var bytes = File.ReadAllBytes(FilePath);
        return bytes[..^512];
    }

    public void Dispose()
    {
        try
        {
            Detach();
        }
        finally
        {
            File.Delete(FilePath);
        }
    }

    /// <summary>
    /// Número del disco montado desde <paramref name="path"/>: Windows informa la ruta del VHD como
    /// ubicación del disco. Tarda un momento en publicarlo.
    /// </summary>
    private static int FindByLocation(string path)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            var disk = new DiskInventory().GetDisks()
                .FirstOrDefault(d => d.Bus == BusKind.Virtual && string.Equals(d.Location, path, StringComparison.OrdinalIgnoreCase));
            if (disk is not null)
            {
                return disk.Number;
            }

            Thread.Sleep(250);
        }

        throw new InvalidOperationException($"No apareció el disco virtual montado desde {path}.");
    }

    private static void TryDetach(string path)
    {
        try
        {
            RunDiskPart($"select vdisk file=\"{path}\"\r\ndetach vdisk\r\n");
        }
        catch (InvalidOperationException)
        {
            // No estaba montado.
        }
    }

    private static void RunDiskPart(string script)
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"cloner-diskpart-{Guid.NewGuid():N}.txt");
        File.WriteAllText(scriptPath, script);
        try
        {
            using var process = Process.Start(new ProcessStartInfo("diskpart.exe", $"/s \"{scriptPath}\"")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            })!;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"diskpart terminó con el código {process.ExitCode}:\n{output}");
            }
        }
        finally
        {
            File.Delete(scriptPath);
        }
    }
}

/// <summary>Las pruebas que montan VHD se ejecutan una tras otra: diskpart y VDS no toleran bien el paralelismo.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class VirtualDiskTests
{
    public const string Name = "Discos virtuales";
}
