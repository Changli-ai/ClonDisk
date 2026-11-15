using System.Security.Principal;

namespace Cloner.Core;

/// <summary>Comprobación de privilegios del proceso actual.</summary>
public static class Elevation
{
    /// <summary>
    /// Indica si el proceso se ejecuta con privilegios de administrador,
    /// necesarios para abrir discos físicos en crudo.
    /// </summary>
    public static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// Indica si se ejecuta dentro de WinPE, donde no hay servicio de instantáneas (VSS). WinPE
    /// se reconoce por la clave <c>HKLM\SYSTEM\CurrentControlSet\Control\MiniNT</c>.
    /// </summary>
    public static bool IsWinPE()
    {
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\MiniNT");
        return key is not null;
    }
}
