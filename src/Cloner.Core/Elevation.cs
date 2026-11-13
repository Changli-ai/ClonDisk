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
}
