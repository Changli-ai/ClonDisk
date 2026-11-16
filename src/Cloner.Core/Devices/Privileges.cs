using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security;

namespace Cloner.Core.Devices;

/// <summary>Activa privilegios del proceso, que un administrador tiene pero desactivados por defecto.</summary>
internal static class Privileges
{
    public const string Backup = "SeBackupPrivilege";
    public const string Restore = "SeRestorePrivilege";
    public const string Security = "SeSecurityPrivilege";
    public const string TakeOwnership = "SeTakeOwnershipPrivilege";

    /// <summary>Activa el privilegio; devuelve falso si el proceso no lo tiene.</summary>
    public static unsafe bool TryEnable(string name)
    {
        using var process = Process.GetCurrentProcess();
        if (!PInvoke.OpenProcessToken(process.SafeHandle, TOKEN_ACCESS_MASK.TOKEN_ADJUST_PRIVILEGES | TOKEN_ACCESS_MASK.TOKEN_QUERY, out var token))
        {
            return false;
        }

        using (token)
        {
            if (!PInvoke.LookupPrivilegeValue(null, name, out var luid))
            {
                return false;
            }

            var privileges = new TOKEN_PRIVILEGES { PrivilegeCount = 1 };
            privileges.Privileges[0] = new LUID_AND_ATTRIBUTES { Luid = luid, Attributes = TOKEN_PRIVILEGES_ATTRIBUTES.SE_PRIVILEGE_ENABLED };
            return PInvoke.AdjustTokenPrivileges((HANDLE)token.DangerousGetHandle(), false, &privileges, 0, null, null)
                && Marshal.GetLastPInvokeError() == 0;
        }
    }

    /// <exception cref="CloneException">El proceso no tiene el privilegio.</exception>
    public static void Enable(string name)
    {
        if (!TryEnable(name))
        {
            throw new CloneException($"No se pudo activar el privilegio {name}: hace falta ejecutar como administrador.", new Win32Exception(Marshal.GetLastPInvokeError()));
        }
    }
}
