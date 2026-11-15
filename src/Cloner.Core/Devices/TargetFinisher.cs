using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Cloner.Core.Cloning;
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
        var windowsLetter = MountAtFreeLetter(repair.WindowsVolume);
        try
        {
            var systemLetter = string.Equals(repair.SystemVolume, repair.WindowsVolume, StringComparison.OrdinalIgnoreCase)
                ? windowsLetter
                : MountAtFreeLetter(repair.SystemVolume);
            try
            {
                TranslateMountedDevices($@"{windowsLetter}:\Windows\System32\config\SYSTEM", repair.MountedDevicesMap);
                RunBcdboot($@"{windowsLetter}:\Windows", systemLetter, repair.Uefi);
            }
            finally
            {
                if (systemLetter != windowsLetter)
                {
                    Unmount(systemLetter);
                }
            }
        }
        finally
        {
            Unmount(windowsLetter);
        }
    }

    public void WaitBeforeRemovingVolumes() => Thread.Sleep(QuietPeriod);

    private static void TranslateMountedDevices(string hivePath, IReadOnlyList<(byte[] Old, byte[] New)> map)
    {
        if (map.Count == 0)
        {
            return;
        }

        EnablePrivilege("SeRestorePrivilege");
        EnablePrivilege("SeBackupPrivilege");
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
            MountedDevices.Translate(values, map);
            foreach (var (name, data) in values.Where(pair => !ReferenceEquals(pair.Value, original[pair.Key])))
            {
                key.SetValue(name, data, RegistryValueKind.Binary);
            }
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

    private static unsafe char MountAtFreeLetter(string volumePath)
    {
        var used = PInvoke.GetLogicalDrives();
        for (var letter = 'Z'; letter >= 'D'; letter--)
        {
            if ((used & (1u << (letter - 'A'))) != 0)
            {
                continue;
            }

            var volume = volumePath.TrimEnd('\\') + "\\";
            if (PInvoke.SetVolumeMountPoint($@"{letter}:\", volume))
            {
                return letter;
            }
        }

        throw new CloneException("No hay ninguna letra de unidad libre para montar el clon.");
    }

    private static void Unmount(char letter) => PInvoke.DeleteVolumeMountPoint($@"{letter}:\");

    private static unsafe void EnablePrivilege(string name)
    {
        using var process = Process.GetCurrentProcess();
        if (!PInvoke.OpenProcessToken(process.SafeHandle, TOKEN_ACCESS_MASK.TOKEN_ADJUST_PRIVILEGES | TOKEN_ACCESS_MASK.TOKEN_QUERY, out var token))
        {
            throw new CloneException($"No se pudo activar el privilegio {name}.", new Win32Exception(Marshal.GetLastPInvokeError()));
        }

        using (token)
        {
            if (!PInvoke.LookupPrivilegeValue(null, name, out var luid))
            {
                throw new CloneException($"No se pudo activar el privilegio {name}.", new Win32Exception(Marshal.GetLastPInvokeError()));
            }

            var privileges = new TOKEN_PRIVILEGES { PrivilegeCount = 1 };
            privileges.Privileges[0] = new LUID_AND_ATTRIBUTES { Luid = luid, Attributes = TOKEN_PRIVILEGES_ATTRIBUTES.SE_PRIVILEGE_ENABLED };
            if (!PInvoke.AdjustTokenPrivileges((HANDLE)token.DangerousGetHandle(), false, &privileges, 0, null, null) || Marshal.GetLastPInvokeError() != 0)
            {
                throw new CloneException($"No se pudo activar el privilegio {name}: hace falta ejecutar como administrador.");
            }
        }
    }
}
