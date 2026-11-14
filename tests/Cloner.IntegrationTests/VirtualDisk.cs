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
        var before = VirtualDiskNumbers();
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

        RunDiskPart(script.ToString());
        return new VirtualDisk(path, FindNewDisk(before));
    }

    /// <summary>Monta el disco; en solo lectura, Windows no puede modificarlo mientras tanto.</summary>
    public void Attach(bool readOnly = false)
    {
        var before = VirtualDiskNumbers();
        RunDiskPart($"select vdisk file=\"{FilePath}\"\r\nattach vdisk{(readOnly ? " readonly" : string.Empty)}\r\n");
        Number = FindNewDisk(before);
        IsAttached = true;
    }

    public void Detach()
    {
        if (IsAttached)
        {
            RunDiskPart($"select vdisk file=\"{FilePath}\"\r\ndetach vdisk\r\n");
            IsAttached = false;
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

    private static HashSet<int> VirtualDiskNumbers() =>
        new DiskInventory().GetDisks().Where(d => d.Bus == BusKind.Virtual).Select(d => d.Number).ToHashSet();

    /// <summary>El disco que acaba de aparecer; Windows tarda un momento en publicarlo.</summary>
    private static int FindNewDisk(HashSet<int> before)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var added = VirtualDiskNumbers().Except(before).ToList();
            if (added.Count == 1)
            {
                return added[0];
            }

            Thread.Sleep(250);
        }

        throw new InvalidOperationException("No apareció el disco virtual montado.");
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
