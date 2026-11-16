using System.ComponentModel;
using System.Runtime.InteropServices;
using Cloner.Core.Devices;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.FileSystem;

namespace Cloner.Core.FileCopy;

/// <summary>Resultado de copiar un árbol de archivos.</summary>
internal sealed record FileCopyResult(long Files, long Directories, long HardLinks, long Bytes, long Skipped, IReadOnlyList<string> Errors);

/// <summary>
/// Copia un volumen archivo a archivo conservando lo que Windows necesita para seguir
/// funcionando: todos los flujos de cada archivo (datos, flujos alternativos, seguridad,
/// atributos extendidos y datos de enlace) mediante BackupRead/BackupWrite, enlaces duros
/// como enlaces, uniones y enlaces simbólicos sin seguirlos, nombres cortos, atributos y fechas.
/// </summary>
internal sealed class FileTreeCopier
{
    private const int BufferSize = 1024 * 1024;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint Delete = 0x00010000;
    private const uint WriteDac = 0x00040000;
    private const uint WriteOwner = 0x00080000;
    private const uint AccessSystemSecurity = 0x01000000;
    private const uint ReparseTagWof = 0x80000017;
    private const uint FileAttributeEncrypted = 0x4000;

    private readonly CopyExclusions _exclusions;
    private readonly bool _withSecurity;
    private readonly bool _withSacl;
    private readonly Action<long, string>? _progress;
    private readonly CancellationToken _cancellation;
    private readonly Dictionary<(uint Serial, ulong Index), string> _linked = [];
    private readonly List<string> _errors = [];
    private readonly byte[] _buffer = new byte[BufferSize];
    private long _files;
    private long _directories;
    private long _hardLinks;
    private long _bytes;
    private long _skipped;

    /// <param name="withSecurity">Copiar propietario y permisos (requiere administrador).</param>
    /// <param name="progress">Recibe los bytes copiados hasta el momento y la ruta en curso.</param>
    public FileTreeCopier(CopyExclusions exclusions, bool withSecurity, Action<long, string>? progress, CancellationToken cancellation)
    {
        _exclusions = exclusions;
        _withSecurity = withSecurity;
        _progress = progress;
        _cancellation = cancellation;
        if (withSecurity)
        {
            Privileges.Enable(Privileges.Backup);
            Privileges.Enable(Privileges.Restore);
            Privileges.TryEnable(Privileges.TakeOwnership);
            _withSacl = Privileges.TryEnable(Privileges.Security);
        }
    }

    /// <param name="sourceRoot">Raíz del origen, por ejemplo la de una instantánea.</param>
    /// <param name="targetRoot">Raíz del destino, ya formateado y vacío.</param>
    public FileCopyResult Copy(string sourceRoot, string targetRoot)
    {
        var source = WithSeparator(sourceRoot);
        var target = WithSeparator(targetRoot);

        // La raíz no se crea, pero sus permisos sí se copian.
        Try(@"\", () => CopyMetadata(source, target, reparse: false));
        CopyDirectory(source, target, @"\");
        return new FileCopyResult(_files, _directories, _hardLinks, _bytes, _skipped, _errors);
    }

    private void CopyDirectory(string source, string target, string relative)
    {
        var finished = new List<(string Target, FILE_BASIC_INFO Info, bool Reparse)>();
        foreach (var entry in Enumerate(source))
        {
            _cancellation.ThrowIfCancellationRequested();
            var relativePath = relative + entry.Name;
            var sourcePath = source + entry.Name;
            var targetPath = target + entry.Name;
            var isDirectory = (entry.Attributes & (uint)FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_DIRECTORY) != 0;
            var isReparse = (entry.Attributes & (uint)FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_REPARSE_POINT) != 0 && entry.ReparseTag != ReparseTagWof;

            if (isDirectory)
            {
                Try(relativePath, () =>
                {
                    Directory.CreateDirectory(targetPath);
                    var info = CopyMetadata(sourcePath, targetPath, isReparse);
                    _directories++;
                    if (!isReparse)
                    {
                        CopyDirectory(sourcePath + "\\", targetPath + "\\", relativePath + "\\");
                    }

                    SetShortName(targetPath, entry.ShortName, isDirectory: true);
                    finished.Add((targetPath, info, isReparse));
                });
                continue;
            }

            if (_exclusions.Excludes(relativePath))
            {
                _skipped++;
                continue;
            }

            if ((entry.Attributes & FileAttributeEncrypted) != 0)
            {
                _errors.Add($"{relativePath}: está cifrado con EFS y no se copia.");
                continue;
            }

            Try(relativePath, () => CopyFile(sourcePath, targetPath, relativePath, entry, isReparse));
        }

        // Las fechas de las carpetas se ponen al final: crear su contenido las cambia.
        foreach (var (path, info, reparse) in finished)
        {
            Try(path, () => SetBasicInfo(path, info, isDirectory: true, reparse));
        }
    }

    private void CopyFile(string sourcePath, string targetPath, string relativePath, FindEntry entry, bool reparse)
    {
        using var source = Open(sourcePath, GenericRead | (_withSacl ? AccessSystemSecurity : 0), FILE_CREATION_DISPOSITION.OPEN_EXISTING, reparse, isDirectory: false);
        var identity = FileIdentity(source);
        if (identity.Links > 1 && _linked.TryGetValue(identity.Key, out var existing))
        {
            if (!PInvoke.CreateHardLink(targetPath, existing))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            _hardLinks++;
            return;
        }

        var info = BasicInfo(source);
        using (var target = Open(targetPath, TargetAccess(), FILE_CREATION_DISPOSITION.CREATE_ALWAYS, reparse, isDirectory: false))
        {
            Transfer(source, target, relativePath);
            Apply(target, info);
        }

        SetShortName(targetPath, entry.ShortName, isDirectory: false);
        if (identity.Links > 1)
        {
            _linked[identity.Key] = targetPath;
        }

        _files++;
    }

    /// <summary>Copia los flujos de una carpeta (seguridad, atributos extendidos, datos de enlace) y devuelve sus fechas.</summary>
    private FILE_BASIC_INFO CopyMetadata(string sourcePath, string targetPath, bool reparse)
    {
        using var source = Open(sourcePath, GenericRead | (_withSacl ? AccessSystemSecurity : 0), FILE_CREATION_DISPOSITION.OPEN_EXISTING, reparse, isDirectory: true);
        using var target = Open(targetPath, TargetAccess(), FILE_CREATION_DISPOSITION.OPEN_EXISTING, reparse, isDirectory: true);
        Transfer(source, target, sourcePath);
        return BasicInfo(source);
    }

    private uint TargetAccess() => GenericWrite | Delete | (_withSecurity ? WriteDac | WriteOwner : 0) | (_withSacl ? AccessSystemSecurity : 0);

    /// <summary>BackupRead del origen y BackupWrite del destino, flujo a flujo.</summary>
    private unsafe void Transfer(SafeFileHandle source, SafeFileHandle target, string path)
    {
        void* readContext = null;
        void* writeContext = null;
        var sourceHandle = (HANDLE)source.DangerousGetHandle();
        var targetHandle = (HANDLE)target.DangerousGetHandle();
        try
        {
            fixed (byte* buffer = _buffer)
            {
                while (true)
                {
                    _cancellation.ThrowIfCancellationRequested();
                    uint read;
                    if (!PInvoke.BackupRead(sourceHandle, buffer, BufferSize, &read, false, _withSecurity, &readContext))
                    {
                        throw new Win32Exception(Marshal.GetLastPInvokeError());
                    }

                    if (read == 0)
                    {
                        break;
                    }

                    uint written;
                    if (!PInvoke.BackupWrite(targetHandle, buffer, read, &written, false, _withSecurity, &writeContext) || written != read)
                    {
                        throw new Win32Exception(Marshal.GetLastPInvokeError());
                    }

                    _bytes += read;
                    _progress?.Invoke(_bytes, path);
                }
            }
        }
        finally
        {
            // Una llamada con bAbort libera el contexto interno de cada operación.
            uint ignored;
            if (readContext is not null)
            {
                PInvoke.BackupRead(sourceHandle, null, 0, &ignored, true, false, &readContext);
            }

            if (writeContext is not null)
            {
                PInvoke.BackupWrite(targetHandle, null, 0, &ignored, true, false, &writeContext);
            }
        }
    }

    private static void SetBasicInfo(string path, FILE_BASIC_INFO info, bool isDirectory, bool reparse)
    {
        using var handle = Open(path, GenericWrite, FILE_CREATION_DISPOSITION.OPEN_EXISTING, reparse, isDirectory);
        Apply(handle, info);
    }

    private static unsafe void Apply(SafeFileHandle handle, FILE_BASIC_INFO info)
    {
        if (!PInvoke.SetFileInformationByHandle((HANDLE)handle.DangerousGetHandle(), FILE_INFO_BY_HANDLE_CLASS.FileBasicInfo, &info, (uint)sizeof(FILE_BASIC_INFO)))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    private static unsafe FILE_BASIC_INFO BasicInfo(SafeFileHandle handle)
    {
        FILE_BASIC_INFO info;
        if (!PInvoke.GetFileInformationByHandleEx((HANDLE)handle.DangerousGetHandle(), FILE_INFO_BY_HANDLE_CLASS.FileBasicInfo, &info, (uint)sizeof(FILE_BASIC_INFO)))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        return info;
    }

    private static ((uint Serial, ulong Index) Key, uint Links) FileIdentity(SafeFileHandle handle)
    {
        if (!PInvoke.GetFileInformationByHandle(handle, out var info))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        return ((info.dwVolumeSerialNumber, ((ulong)info.nFileIndexHigh << 32) | info.nFileIndexLow), info.nNumberOfLinks);
    }

    /// <summary>Nombre corto 8.3: solo con privilegio de restauración y si el volumen los admite.</summary>
    private void SetShortName(string path, string? shortName, bool isDirectory)
    {
        if (!_withSecurity || string.IsNullOrEmpty(shortName))
        {
            return;
        }

        try
        {
            using var handle = Open(path, GenericWrite | Delete, FILE_CREATION_DISPOSITION.OPEN_EXISTING, reparse: true, isDirectory);
            PInvoke.SetFileShortName(handle, shortName);
        }
        catch (IOException)
        {
            // Sin nombre corto la copia sigue siendo válida.
        }
    }

    private static SafeFileHandle Open(string path, uint access, FILE_CREATION_DISPOSITION disposition, bool reparse, bool isDirectory)
    {
        var flags = FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_SEQUENTIAL_SCAN;
        if (reparse)
        {
            flags |= FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_OPEN_REPARSE_POINT;
        }

        _ = isDirectory;
        var handle = PInvoke.CreateFile(
            path,
            access,
            FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE | FILE_SHARE_MODE.FILE_SHARE_DELETE,
            null,
            disposition,
            flags,
            null);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new IOException(new Win32Exception(error).Message, new Win32Exception(error));
        }

        return handle;
    }

    private void Try(string path, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            _errors.Add($"{path}: {ex.Message}");
        }
    }

    private static string WithSeparator(string path) => path.EndsWith('\\') ? path : path + "\\";

    private readonly record struct FindEntry(string Name, string? ShortName, uint Attributes, uint ReparseTag);

    private static unsafe List<FindEntry> Enumerate(string directory)
    {
        var entries = new List<FindEntry>();
        WIN32_FIND_DATAW data;
        HANDLE find;
        fixed (char* pattern = directory + "*")
        {
            find = PInvoke.FindFirstFileEx(new PCWSTR(pattern), FINDEX_INFO_LEVELS.FindExInfoStandard, &data, FINDEX_SEARCH_OPS.FindExSearchNameMatch, null, FIND_FIRST_EX_FLAGS.FIND_FIRST_EX_LARGE_FETCH);
        }

        if ((nint)find.Value == -1 || find.Value == null)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error == 2 || error == 18)
            {
                return entries;
            }

            throw new IOException(new Win32Exception(error).Message, new Win32Exception(error));
        }

        try
        {
            do
            {
                var name = data.cFileName.ToString();
                if (name is "." or "..")
                {
                    continue;
                }

                var shortName = data.cAlternateFileName.ToString();
                entries.Add(new FindEntry(name, shortName.Length > 0 ? shortName : null, data.dwFileAttributes, data.dwReserved0));
            }
            while (PInvoke.FindNextFile(find, &data));
        }
        finally
        {
            PInvoke.FindClose(find);
        }

        return entries;
    }
}
