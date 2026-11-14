namespace Cloner.Core;

/// <summary>Error que impide planificar o completar un clonado. El mensaje se muestra al usuario.</summary>
public sealed class CloneException : Exception
{
    public CloneException()
    {
    }

    public CloneException(string message)
        : base(message)
    {
    }

    public CloneException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
