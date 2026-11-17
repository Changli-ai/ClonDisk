using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
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
/// <remarks>
/// Un hilo recorre las carpetas y las crea; varios hilos copian los archivos a la vez, porque con
/// archivos pequeños el tiempo se va en abrir, crear y cerrar, no en transferir datos. Las fechas
/// de las carpetas se ponen al final, de la más profunda a la raíz.
/// </remarks>
internal sealed class FileTreeCopier
{
    /// <summary>Hilos que copian archivos a la vez.</summary>
    public const int DefaultWorkers = 8;

    private const int BufferSize = 4 * 1024 * 1024;
    private const int QueueCapacity = 4096;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint Delete = 0x00010000;
    private const uint WriteDac = 0x00040000;
    private const uint WriteOwner = 0x00080000;
    private const uint AccessSystemSecurity = 0x01000000;
    private const uint ReparseTagWof = 0x80000017;
    private const uint FileAttributeEncrypted = 0x4000;
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(200);

    private readonly CopyExclusions _exclusions;
    private readonly bool _withSecurity;
    private bool _withSacl;
    private readonly int _workers;
    private readonly Action<long, string>? _progress;
    private readonly CancellationToken _cancellation;
    private readonly Dictionary<(uint Serial, ulong Index), string?> _linked = [];
    private readonly ConcurrentQueue<FileJob> _deferredLinks = new();
    private readonly ConcurrentQueue<string> _errors = new();
    private readonly List<DirectoryDone> _directoriesDone = [];
    private readonly Lock _progressLock = new();
    private readonly Stopwatch _sinceProgress = Stopwatch.StartNew();
    private CancellationToken _token;
    private Exception? _fatal;
    private long _files;
    private long _directories;
    private long _hardLinks;
    private long _bytes;
    private long _skipped;

    /// <param name="withSecurity">Copiar propietario y permisos (requiere administrador).</param>
    /// <param name="progress">Recibe los bytes copiados hasta el momento y la ruta en curso; nunca desde dos hilos a la vez.</param>
    /// <param name="workers">Archivos que se copian a la vez.</param>
    public FileTreeCopier(CopyExclusions exclusions, bool withSecurity, Action<long, string>? progress, CancellationToken cancellation, int workers = DefaultWorkers)
    {
        _exclusions = exclusions;
        _withSecurity = withSecurity;
        _progress = progress;
        _cancellation = cancellation;
        _workers = Math.Max(1, workers);
        if (withSecurity)
        {
            Privileges.Enable(Privileges.Backup);
            Privileges.Enable(Privileges.Restore);
            Privileges.TryEnable(Privileges.TakeOwnership);
            _withSacl = Privileges.TryEnable(Privileges.Security);
        }
    }

    /// <summary>Copiar los nombres cortos 8.3 (por defecto, sí).</summary>
    internal bool ShortNames { get; init; } = true;

    /// <summary>Copiar la auditoría (SACL) si el proceso tiene el privilegio (por defecto, sí).</summary>
    internal bool Sacl
    {
        get => _withSacl;
        init => _withSacl = value && _withSacl;
    }

    /// <param name="sourceRoot">Raíz del origen, por ejemplo la de una instantánea.</param>
    /// <param name="targetRoot">Raíz del destino, ya formateado y vacío.</param>
    public FileCopyResult Copy(string sourceRoot, string targetRoot)
    {
        var source = WithSeparator(sourceRoot);
        var target = WithSeparator(targetRoot);

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(_cancellation);
        _token = stop.Token;
        using (var queue = new BlockingCollection<FileJob>(QueueCapacity))
        {
            var workers = Enumerable.Range(0, _workers)
                .Select(_ => Task.Factory.StartNew(() => Work(queue, stop), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default))
                .ToArray();
            try
            {
                // La raíz no se crea, pero sus permisos sí se copian.
                Try(@"\", () => CopyMetadata(source, target, reparse: false));
                Walk(source, target, @"\", depth: 0, queue);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Fail(ex, stop);
            }
            catch (OperationCanceledException)
            {
                // Cancelado por el usuario o por un fallo en otro hilo: se decide abajo.
            }
            finally
            {
                queue.CompleteAdding();
            }

            Task.WaitAll(workers);
        }

        if (_fatal is not null)
        {
            ExceptionDispatchInfo.Throw(_fatal);
        }

        _cancellation.ThrowIfCancellationRequested();

        // Enlaces duros cuyo primer nombre aún se estaba copiando en otro hilo.
        while (_deferredLinks.TryDequeue(out var job))
        {
            _cancellation.ThrowIfCancellationRequested();
            Try(job.RelativePath, () => CopyFile(job, deferIfBusy: false));
        }

        // Las fechas de las carpetas se ponen al final, de la más profunda a la raíz: crear su
        // contenido (y dar nombre corto a sus subcarpetas) las cambia.
        foreach (var directory in _directoriesDone.OrderByDescending(d => d.Depth))
        {
            _cancellation.ThrowIfCancellationRequested();
            Try(directory.RelativePath, () => FinishDirectory(directory));
        }

        Report(force: true, string.Empty);
        return new FileCopyResult(_files, _directories, _hardLinks, _bytes, _skipped, _errors.ToList());
    }

    /// <summary>Recorre una carpeta: crea sus subcarpetas y encola sus archivos.</summary>
    private void Walk(string source, string target, string relative, int depth, BlockingCollection<FileJob> queue)
    {
        foreach (var entry in Enumerate(source))
        {
            _token.ThrowIfCancellationRequested();
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
                    Interlocked.Increment(ref _directories);
                    _directoriesDone.Add(new DirectoryDone(targetPath, relativePath, info, isReparse, entry.ShortName, depth + 1));
                    if (!isReparse)
                    {
                        Walk(sourcePath + "\\", targetPath + "\\", relativePath + "\\", depth + 1, queue);
                    }
                });
                continue;
            }

            if (_exclusions.Excludes(relativePath))
            {
                Interlocked.Increment(ref _skipped);
                continue;
            }

            if ((entry.Attributes & FileAttributeEncrypted) != 0)
            {
                _errors.Enqueue($"{relativePath}: está cifrado con EFS y no se copia.");
                continue;
            }

            queue.Add(new FileJob(sourcePath, targetPath, relativePath, entry.ShortName, isReparse), _token);
        }
    }

    private void Work(BlockingCollection<FileJob> queue, CancellationTokenSource stop)
    {
        try
        {
            foreach (var job in queue.GetConsumingEnumerable(_token))
            {
                Try(job.RelativePath, () => CopyFile(job, deferIfBusy: true));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Fail(ex, stop);
        }
    }

    /// <summary>Guarda el primer fallo grave y detiene a los demás hilos.</summary>
    private void Fail(Exception ex, CancellationTokenSource stop)
    {
        Interlocked.CompareExchange(ref _fatal, ex, null);
        stop.Cancel();
    }

    private void CopyFile(FileJob job, bool deferIfBusy)
    {
        using var source = Open(job.SourcePath, GenericRead | (_withSacl ? AccessSystemSecurity : 0), FILE_CREATION_DISPOSITION.OPEN_EXISTING, job.Reparse);
        var identity = FileIdentity(source);
        var linked = identity.Links > 1;
        if (linked)
        {
            string? existing;
            lock (_linked)
            {
                if (!_linked.TryGetValue(identity.Key, out existing))
                {
                    // Este hilo copia el primer nombre; los demás serán enlaces a él.
                    _linked[identity.Key] = null;
                }
                else if (existing is null)
                {
                    if (deferIfBusy)
                    {
                        _deferredLinks.Enqueue(job);
                        return;
                    }

                    // Sin hilos en marcha, un primer nombre sin terminar es que falló: este pasa a ser el primero.
                    existing = null;
                }
            }

            if (existing is not null)
            {
                if (!PInvoke.CreateHardLink(job.TargetPath, existing))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                }

                Interlocked.Increment(ref _hardLinks);
                return;
            }
        }

        var info = BasicInfo(source);
        using (var target = Open(job.TargetPath, TargetAccess(), FILE_CREATION_DISPOSITION.CREATE_ALWAYS, job.Reparse))
        {
            Transfer(source, target, job.RelativePath);
            Apply(target, info);
            SetShortName(target, job.ShortName);
        }

        if (linked)
        {
            lock (_linked)
            {
                _linked[identity.Key] = job.TargetPath;
            }
        }

        Interlocked.Increment(ref _files);
    }

    /// <summary>Copia los flujos de una carpeta (seguridad, atributos extendidos, datos de enlace) y devuelve sus fechas.</summary>
    private FILE_BASIC_INFO CopyMetadata(string sourcePath, string targetPath, bool reparse)
    {
        using var source = Open(sourcePath, GenericRead | (_withSacl ? AccessSystemSecurity : 0), FILE_CREATION_DISPOSITION.OPEN_EXISTING, reparse);
        using var target = Open(targetPath, TargetAccess(), FILE_CREATION_DISPOSITION.OPEN_EXISTING, reparse);
        Transfer(source, target, sourcePath);
        return BasicInfo(source);
    }

    /// <summary>Nombre corto y fechas de una carpeta, con una sola apertura.</summary>
    private void FinishDirectory(DirectoryDone directory)
    {
        using var handle = Open(directory.TargetPath, GenericWrite | Delete, FILE_CREATION_DISPOSITION.OPEN_EXISTING, directory.Reparse);
        SetShortName(handle, directory.ShortName);
        Apply(handle, directory.Info);
    }

    private uint TargetAccess() => GenericWrite | Delete | (_withSecurity ? WriteDac | WriteOwner : 0) | (_withSacl ? AccessSystemSecurity : 0);

    /// <summary>BackupRead del origen y BackupWrite del destino, flujo a flujo.</summary>
    private unsafe void Transfer(SafeFileHandle source, SafeFileHandle target, string path)
    {
        void* readContext = null;
        void* writeContext = null;
        var sourceHandle = (HANDLE)source.DangerousGetHandle();
        var targetHandle = (HANDLE)target.DangerousGetHandle();
        var buffer = Buffer.Value!;
        try
        {
            fixed (byte* data = buffer)
            {
                while (true)
                {
                    _token.ThrowIfCancellationRequested();
                    uint read;
                    if (!PInvoke.BackupRead(sourceHandle, data, (uint)buffer.Length, &read, false, _withSecurity, &readContext))
                    {
                        throw new Win32Exception(Marshal.GetLastPInvokeError());
                    }

                    if (read == 0)
                    {
                        break;
                    }

                    uint written;
                    if (!PInvoke.BackupWrite(targetHandle, data, read, &written, false, _withSecurity, &writeContext) || written != read)
                    {
                        throw new Win32Exception(Marshal.GetLastPInvokeError());
                    }

                    Interlocked.Add(ref _bytes, read);
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

        Report(force: false, path);
    }

    /// <summary>Búfer de cada hilo, reutilizado entre archivos.</summary>
    private static readonly ThreadLocal<byte[]> Buffer = new(() => new byte[BufferSize]);

    /// <summary>Avisa del progreso como mucho cada 200 ms y siempre desde un solo hilo a la vez.</summary>
    private void Report(bool force, string path)
    {
        if (_progress is null)
        {
            return;
        }

        lock (_progressLock)
        {
            if (!force && _sinceProgress.Elapsed < ProgressInterval)
            {
                return;
            }

            _sinceProgress.Restart();
            _progress(Interlocked.Read(ref _bytes), path);
        }
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

    /// <summary>
    /// Nombre corto 8.3 sobre el identificador ya abierto (con acceso de borrado): solo con
    /// privilegio de restauración y si el volumen los admite. Si falla, la copia sigue siendo válida.
    /// </summary>
    private void SetShortName(SafeFileHandle handle, string? shortName)
    {
        if (_withSecurity && ShortNames && !string.IsNullOrEmpty(shortName))
        {
            PInvoke.SetFileShortName(handle, shortName);
        }
    }

    private static SafeFileHandle Open(string path, uint access, FILE_CREATION_DISPOSITION disposition, bool reparse)
    {
        var flags = FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_SEQUENTIAL_SCAN;
        if (reparse)
        {
            flags |= FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_OPEN_REPARSE_POINT;
        }

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
            if (DeviceGone(ex))
            {
                throw new CloneException($"El volumen dejó de estar disponible durante la copia ({path}): {ex.Message}", ex);
            }

            _errors.Enqueue($"{path}: {ex.Message}");
        }
    }

    /// <summary>
    /// El volumen de origen o de destino ha desaparecido (desmontado, desconectado o formateado por
    /// otro programa): seguir solo acumularía errores, así que la copia se detiene.
    /// </summary>
    private static bool DeviceGone(Exception ex)
    {
        const int NotReady = 21;
        const int ExternallyAltered = 1006;
        const int InvalidDrive = 15;
        const int DeviceRemoved = 1617;
        for (var current = ex; current is not null; current = current.InnerException)
        {
            var code = current is Win32Exception win32 ? win32.NativeErrorCode : current.HResult & 0xFFFF;
            if (code is NotReady or ExternallyAltered or InvalidDrive or DeviceRemoved)
            {
                return true;
            }
        }

        return false;
    }

    private static string WithSeparator(string path) => path.EndsWith('\\') ? path : path + "\\";

    private sealed record FileJob(string SourcePath, string TargetPath, string RelativePath, string? ShortName, bool Reparse);

    private sealed record DirectoryDone(string TargetPath, string RelativePath, FILE_BASIC_INFO Info, bool Reparse, string? ShortName, int Depth);

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
