using System.ComponentModel;
using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using Cloner.Core.Cloning;
using Cloner.Core.FileCopy;
using Cloner.Core.Inventory;
using Cloner.Core.Layout;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security;
using Windows.Win32.System.Registry;

namespace Cloner.Core.Devices;

/// <summary>Trabajo real sobre el clon montado: ampliar NTFS, montar, cargar su registro y ejecutar bcdboot.</summary>
internal sealed class TargetFinisher(IDiskInventory inventory) : ITargetFinisher
{
    private const string HiveName = "ClonDisk_Clon_SYSTEM";
    private static readonly TimeSpan MountTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan QuietPeriod = TimeSpan.FromSeconds(90);

    public IReadOnlyDictionary<long, string> WaitForVolumes(int diskNumber, IReadOnlyCollection<long> partitionOffsets)
    {
        var deadline = DateTime.UtcNow + MountTimeout;
        while (true)
        {
            var disk = inventory.GetDisks().FirstOrDefault(d => d.Number == diskNumber);
            var found = disk?.Partitions
                .Where(p => p.Volume is not null && partitionOffsets.Contains(p.OffsetBytes))
                .ToDictionary(p => p.OffsetBytes, p => p.Volume!.Path) ?? [];
            if (found.Count == partitionOffsets.Count)
            {
                return found;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new CloneException($"Windows no montó los volúmenes del clon a tiempo ({found.Count} de {partitionOffsets.Count}).");
            }

            Thread.Sleep(500);
        }
    }

    public void FormatVolume(string volumePath, string label, string fileSystem = "NTFS")
    {
        var path = WithSeparator(volumePath);
        using var searcher = new ManagementObjectSearcher(
            @"root\Microsoft\Windows\Storage",
            $"SELECT * FROM MSFT_Volume WHERE Path = '{path.Replace(@"\", @"\\", StringComparison.Ordinal)}'");
        using var results = searcher.Get();
        using var volume = results.Cast<ManagementObject>().FirstOrDefault()
            ?? throw new CloneException($"No se encontró el volumen {path} para formatearlo.");
        using var input = volume.GetMethodParameters("Format");
        input["FileSystem"] = fileSystem;
        input["FileSystemLabel"] = label;
        input["Full"] = false;
        input["Force"] = true;
        using var output = volume.InvokeMethod("Format", input, null);
        var code = Convert.ToUInt32(output?["ReturnValue"] ?? uint.MaxValue, System.Globalization.CultureInfo.InvariantCulture);
        if (code != 0)
        {
            throw new CloneException($"Windows no pudo formatear el volumen {path} (código {code}).");
        }
    }

    public void SetVolumeLabel(string volumePath, string label)
    {
        // Sin etiqueta se pasa nulo: una cadena vacía no la borra en todos los casos.
        if (!PInvoke.SetVolumeLabel(WithSeparator(volumePath), string.IsNullOrEmpty(label) ? null! : label))
        {
            var error = new Win32Exception(Marshal.GetLastPInvokeError());
            throw new CloneException($"No se pudo poner la etiqueta al volumen {volumePath}: {error.Message}", error);
        }
    }

    public FileCopyResult CopyFiles(string sourceRoot, string targetRoot, CopyExclusions exclusions, Action<long, string>? progress, CancellationToken cancellation) =>
        new FileTreeCopier(exclusions, withSecurity: true, progress, cancellation).Copy(sourceRoot, targetRoot);

    private static string WithSeparator(string path) => path.EndsWith('\\') ? path : path + "\\";

    public void ExtendVolume(string volumePath, long partitionBytes, int sectorSize)
    {
        // NTFS ocupa la partición menos el último sector, donde guarda la copia del sector de arranque.
        var sectors = (partitionBytes / sectorSize) - 1;
        using var handle = DiskDevice.OpenHandle(volumePath.TrimEnd('\\'), 0x80000000 | 0x40000000, unbuffered: false, $"el volumen {volumePath}");
        Span<byte> input = stackalloc byte[8];
        BitConverter.TryWriteBytes(input, sectors);
        DiskDevice.Control(handle, PInvoke.FSCTL_EXTEND_VOLUME, input);
    }

    public string? FindWindowsVolume(IEnumerable<string> volumePaths) =>
        volumePaths.FirstOrDefault(path => File.Exists(Path.Combine(path, "Windows", "System32", "config", "SYSTEM")));

    public void RepairBoot(BootRepair repair)
    {
        ArgumentNullException.ThrowIfNull(repair);
        WithLetters(repair.WindowsVolume, repair.SystemVolume, (windowsLetter, systemLetter) =>
        {
            if (repair.MountedDevicesMap.Count > 0)
            {
                TranslateMountedDevices($@"{windowsLetter}:\Windows\System32\config\SYSTEM", _ => repair.MountedDevicesMap);
            }

            RunBcdboot($@"{windowsLetter}:\Windows", systemLetter, repair.Uefi);
            return 0;
        });
    }

    public int RepairBootInPlace(string windowsVolume, string systemVolume, bool uefi, byte[] windowsIdentity)
    {
        ArgumentNullException.ThrowIfNull(windowsIdentity);
        return WithLetters(windowsVolume, systemVolume, (windowsLetter, systemLetter) =>
        {
            // La letra de Windows es C:; si en el registro apunta a otra identidad (la del original
            // o la que tenía antes de que Windows se la cambiara), se traduce a la actual.
            var changed = TranslateMountedDevices($@"{windowsLetter}:\Windows\System32\config\SYSTEM", values =>
                values.TryGetValue(@"\DosDevices\C:", out var current) && !current.AsSpan().SequenceEqual(windowsIdentity)
                    ? [(current, windowsIdentity)]
                    : []);
            RunBcdboot($@"{windowsLetter}:\Windows", systemLetter, uefi);
            return changed;
        });
    }

    /// <summary>Monta los volúmenes de Windows y de arranque en letras libres mientras dura la acción.</summary>
    private int WithLetters(string windowsVolume, string systemVolume, Func<char, char, int> action)
    {
        var windows = MountWithLetter(windowsVolume);
        try
        {
            var system = string.Equals(systemVolume, windowsVolume, StringComparison.OrdinalIgnoreCase)
                ? windows
                : MountWithLetter(systemVolume);
            try
            {
                return action(windows.Letter, system.Letter);
            }
            finally
            {
                if (system != windows)
                {
                    system.Release();
                }
            }
        }
        finally
        {
            windows.Release();
        }
    }

    public void WaitBeforeRemovingVolumes(Action<int>? secondsLeft = null)
    {
        var end = DateTime.UtcNow + QuietPeriod;
        while (DateTime.UtcNow < end)
        {
            secondsLeft?.Invoke((int)Math.Ceiling((end - DateTime.UtcNow).TotalSeconds));
            Thread.Sleep(TimeSpan.FromMilliseconds(Math.Min(1000, Math.Max(0, (end - DateTime.UtcNow).TotalMilliseconds))));
        }
    }

    public void RemoveDriveLetters(IEnumerable<string> volumePaths)
    {
        ArgumentNullException.ThrowIfNull(volumePaths);
        foreach (var volume in volumePaths)
        {
            foreach (var mountPoint in MountPoints(volume).Where(p => p.Length == 3 && p[1] == ':'))
            {
                // Sin la letra el volumen sigue accesible por su ruta \\?\Volume{GUID}\.
                PInvoke.DeleteVolumeMountPoint(mountPoint);
            }
        }
    }

    /// <summary>Letras y carpetas donde está montado un volumen.</summary>
    private static unsafe List<string> MountPoints(string volumePath)
    {
        var buffer = new char[1024];
        uint length;
        fixed (char* names = buffer)
        {
            if (!PInvoke.GetVolumePathNamesForVolumeName(WithSeparator(volumePath), names, (uint)buffer.Length, out length))
            {
                return [];
            }
        }

        return new string(buffer, 0, (int)Math.Min(length, (uint)buffer.Length)).Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    /// <summary>Carga el registro del clon, traduce los valores de MountedDevices según el mapa que devuelve <paramref name="mapFor"/> y cuenta los cambiados.</summary>
    private static int TranslateMountedDevices(string hivePath, Func<IReadOnlyDictionary<string, byte[]>, IReadOnlyList<(byte[] Old, byte[] New)>> mapFor)
    {
        Privileges.Enable(Privileges.Restore);
        Privileges.Enable(Privileges.Backup);
        var status = PInvoke.RegLoadKey(Registry.LocalMachine.Handle, HiveName, hivePath);
        if (status != WIN32_ERROR.ERROR_SUCCESS)
        {
            throw new CloneException($"No se pudo cargar el registro del clon: {new Win32Exception((int)status).Message}");
        }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"{HiveName}\MountedDevices", writable: true)
                ?? throw new CloneException("El registro del clon no tiene la clave MountedDevices.");
            var values = key.GetValueNames()
                .Where(name => key.GetValueKind(name) == RegistryValueKind.Binary)
                .ToDictionary(name => name, name => (byte[])key.GetValue(name)!);
            var original = values.ToDictionary(pair => pair.Key, pair => pair.Value);
            var map = mapFor(original);
            if (map.Count == 0)
            {
                return 0;
            }

            MountedDevices.Translate(values, map);
            var changed = 0;
            foreach (var (name, data) in values.Where(pair => !ReferenceEquals(pair.Value, original[pair.Key])))
            {
                key.SetValue(name, data, RegistryValueKind.Binary);
                changed++;
            }

            return changed;
        }
        finally
        {
            // El registro solo se descarga si no queda ninguna clave abierta.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            PInvoke.RegUnLoadKey(Registry.LocalMachine.Handle, HiveName);
        }
    }

    private static void RunBcdboot(string windowsPath, char systemLetter, bool uefi)
    {
        var bcdboot = Path.Combine(Environment.SystemDirectory, "bcdboot.exe");
        var start = new ProcessStartInfo(bcdboot, $"\"{windowsPath}\" /s {systemLetter}: /f {(uefi ? "UEFI" : "BIOS")}")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(start) ?? throw new CloneException("No se pudo ejecutar bcdboot.");
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new CloneException($"bcdboot no pudo rehacer el arranque del clon: {output.Trim()}");
        }
    }

    /// <summary>Monta un volumen con una letra mientras se use el resultado (para pruebas).</summary>
    internal (char Letter, IDisposable Release) MountTemporarily(string volumePath)
    {
        var mounted = MountWithLetter(volumePath);
        return (mounted.Letter, new Releaser(mounted.Release));
    }

    private sealed class Releaser(Action release) : IDisposable
    {
        public void Dispose() => release();
    }

    /// <summary>Una letra de unidad para un volumen del clon, y cómo quitarla al terminar.</summary>
    private sealed record Mounted(char Letter, Action Release);

    /// <summary>
    /// Da una letra a un volumen del clon. Si ya tiene una, se usa esa (y no se quita después).
    /// Si no, se le asigna una libre; la partición EFI a menudo no admite letra por la vía normal
    /// (SetVolumeMountPoint), así que entonces se asigna con diskpart, como se haría a mano.
    /// </summary>
    private Mounted MountWithLetter(string volumePath)
    {
        var volume = WithSeparator(volumePath);
        var existing = MountPoints(volume).FirstOrDefault(p => p.Length == 3 && p[1] == ':' && char.IsAsciiLetter(p[0]));
        if (existing is not null)
        {
            return new Mounted(char.ToUpperInvariant(existing[0]), () => { });
        }

        var lastError = 0;
        foreach (var letter in FreeLetters())
        {
            if (PInvoke.SetVolumeMountPoint($@"{letter}:\", volume))
            {
                return new Mounted(letter, () => PInvoke.DeleteVolumeMountPoint($@"{letter}:\"));
            }

            lastError = Marshal.GetLastPInvokeError();
        }

        var partition = inventory.GetDisks()
            .SelectMany(d => d.Partitions.Select(p => (Disk: d.Number, Partition: p)))
            .FirstOrDefault(x => x.Partition.Volume is { } v && string.Equals(WithSeparator(v.Path), volume, StringComparison.OrdinalIgnoreCase));
        var free = FreeLetters().FirstOrDefault();
        if (partition.Partition is not null && free != default)
        {
            RunDiskPart($"select disk {partition.Disk}\r\nselect partition {partition.Partition.Number}\r\nassign letter={free}\r\n");
            if (Directory.Exists($@"{free}:\"))
            {
                var disk = partition.Disk;
                var number = partition.Partition.Number;
                return new Mounted(free, () =>
                {
                    if (!PInvoke.DeleteVolumeMountPoint($@"{free}:\"))
                    {
                        TryRunDiskPart($"select disk {disk}\r\nselect partition {number}\r\nremove letter={free}\r\n");
                    }
                });
            }
        }

        var reason = lastError != 0 ? new Win32Exception(lastError).Message : "no hay letras de unidad libres";
        throw new CloneException($"No se pudo dar una letra de unidad al volumen {volume} del clon para rehacer su arranque: {reason}");
    }

    /// <summary>Letras sin usar, de la Z hacia la D.</summary>
    private static IEnumerable<char> FreeLetters()
    {
        var used = PInvoke.GetLogicalDrives();
        for (var letter = 'Z'; letter >= 'D'; letter--)
        {
            if ((used & (1u << (letter - 'A'))) == 0)
            {
                yield return letter;
            }
        }
    }

    private static void RunDiskPart(string script)
    {
        var path = Path.Combine(Path.GetTempPath(), $"clondisk-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, script);
        try
        {
            var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "diskpart.exe"), $"/s \"{path}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var process = Process.Start(start) ?? throw new CloneException("No se pudo ejecutar diskpart.");
            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                throw new CloneException($"diskpart no pudo asignar la letra: {output.Trim()}");
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void TryRunDiskPart(string script)
    {
        try
        {
            RunDiskPart(script);
        }
        catch (CloneException)
        {
            // Quitar la letra temporal no es imprescindible: desaparece al reiniciar o desconectar.
        }
    }
}
