using System.Text.RegularExpressions;

namespace Cloner.Core.FileCopy;

/// <summary>
/// Archivos que no se copian al clonar por archivos: los de paginación e hibernación, el contenido
/// de «System Volume Information» y lo que Windows declara en
/// <c>HKLM\SYSTEM\CurrentControlSet\Control\BackupRestore\FilesNotToBackup</c>.
/// Las rutas son relativas a la raíz del volumen y empiezan por «\».
/// </summary>
internal sealed class CopyExclusions
{
    private const string FilesNotToBackupKey = @"SYSTEM\CurrentControlSet\Control\BackupRestore\FilesNotToBackup";

    private readonly List<(Regex Pattern, bool Recursive, string Directory)> _rules = [];

    /// <summary>Exclusiones básicas, válidas para cualquier volumen.</summary>
    public static CopyExclusions Default()
    {
        var exclusions = new CopyExclusions();
        exclusions.Add(@"\pagefile.sys");
        exclusions.Add(@"\hiberfil.sys");
        exclusions.Add(@"\swapfile.sys");
        exclusions.Add(@"\System Volume Information\* /s");
        return exclusions;
    }

    /// <summary>Las básicas más las de FilesNotToBackup que se refieren al volumen de la letra indicada.</summary>
    public static CopyExclusions ForVolume(char? driveLetter)
    {
        var exclusions = Default();
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(FilesNotToBackupKey);
        if (key is null)
        {
            return exclusions;
        }

        foreach (var name in key.GetValueNames())
        {
            if (key.GetValue(name) is string[] values)
            {
                foreach (var value in values)
                {
                    exclusions.AddRegistryPattern(value, driveLetter);
                }
            }
        }

        return exclusions;
    }

    /// <summary>
    /// Añade un patrón con el formato de FilesNotToBackup: ruta con comodines en el último
    /// tramo y, opcionalmente, «/s» para incluir subcarpetas. Las que nombran otra unidad o
    /// variables de usuario no se aplican.
    /// </summary>
    public void AddRegistryPattern(string pattern, char? driveLetter)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        var text = pattern.Trim();
        var systemDrive = Environment.GetEnvironmentVariable("SystemDrive") ?? "C:";
        var windows = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
        text = Regex.Replace(text, "%SystemDrive%", systemDrive, RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "%(SystemRoot|windir)%", windows, RegexOptions.IgnoreCase);
        if (text.Contains('%', StringComparison.Ordinal))
        {
            return;
        }

        if (text.Length >= 2 && text[1] == ':')
        {
            var letter = char.ToUpperInvariant(text[0]);
            if (letter != '*' && (driveLetter is null || letter != char.ToUpperInvariant(driveLetter.Value)))
            {
                return;
            }

            text = text[2..];
        }

        if (text.StartsWith('\\'))
        {
            Add(text);
        }
    }

    public void Add(string pattern)
    {
        var text = pattern.Trim();
        var recursive = text.EndsWith(" /s", StringComparison.OrdinalIgnoreCase);
        if (recursive)
        {
            text = text[..^3].TrimEnd();
        }

        var separator = text.LastIndexOf('\\');
        var directory = text[..(separator + 1)];
        var name = text[(separator + 1)..];
        if (name.Length == 0)
        {
            name = "*";
        }

        // «*.*» en las rutas de Windows también abarca nombres sin punto.
        var regex = "^" + Regex.Escape(name).Replace(@"\*\.\*", ".*", StringComparison.Ordinal).Replace(@"\*", ".*", StringComparison.Ordinal).Replace(@"\?", ".", StringComparison.Ordinal) + "$";
        _rules.Add((new Regex(regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), recursive, directory));
    }

    /// <summary>Indica si un archivo (ruta relativa con «\» inicial) no se copia.</summary>
    public bool Excludes(string relativePath)
    {
        var separator = relativePath.LastIndexOf('\\');
        var directory = relativePath[..(separator + 1)];
        var name = relativePath[(separator + 1)..];
        foreach (var (pattern, recursive, ruleDirectory) in _rules)
        {
            var inDirectory = recursive
                ? directory.StartsWith(ruleDirectory, StringComparison.OrdinalIgnoreCase)
                : directory.Equals(ruleDirectory, StringComparison.OrdinalIgnoreCase);
            if (inDirectory && pattern.IsMatch(name))
            {
                return true;
            }
        }

        return false;
    }
}
