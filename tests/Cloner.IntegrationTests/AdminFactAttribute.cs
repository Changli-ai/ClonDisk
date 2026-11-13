using Cloner.Core;

namespace Cloner.IntegrationTests;

/// <summary>
/// Prueba que solo se ejecuta en una consola de administrador; en otro caso se omite.
/// Las pruebas que montan o escriben discos virtuales deben usar este atributo.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class AdminFactAttribute : FactAttribute
{
    public AdminFactAttribute()
    {
        if (!Elevation.IsAdministrator())
        {
            Skip = "Requiere una consola de administrador.";
        }
    }
}
