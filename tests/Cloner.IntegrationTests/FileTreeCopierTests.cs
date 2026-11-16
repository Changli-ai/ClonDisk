using System.Diagnostics;
using Cloner.Core.FileCopy;

namespace Cloner.IntegrationTests;

/// <summary>Copia por archivos entre carpetas temporales del disco del sistema; no requiere administrador.</summary>
public sealed class FileTreeCopierTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cloner-files", Guid.NewGuid().ToString("N"));
    private readonly string _source;
    private readonly string _target;

    public FileTreeCopierTests()
    {
        _source = Path.Combine(_root, "origen");
        _target = Path.Combine(_root, "destino");
        Directory.CreateDirectory(_source);
        Directory.CreateDirectory(_target);
    }

    [Fact]
    public void CopiesContentStreamsLinksAttributesAndTimes()
    {
        var docs = Directory.CreateDirectory(Path.Combine(_source, "Documentos", "Sub"));
        var text = Path.Combine(_source, "Documentos", "nota.txt");
        File.WriteAllText(text, "contenido principal");
        File.WriteAllText(text + ":oculto", "flujo alternativo");
        var big = Path.Combine(docs.FullName, "grande.bin");
        var data = new byte[3 * 1024 * 1024 + 17];
        new Random(5).NextBytes(data);
        File.WriteAllBytes(big, data);
        var linked = Path.Combine(_source, "enlace.bin");
        RunCmd($"mklink /H \"{linked}\" \"{big}\"");
        File.WriteAllText(Path.Combine(_source, "pagefile.sys"), "no se copia");
        RunCmd($"mklink /J \"{Path.Combine(_source, "Union")}\" \"{docs.FullName}\"");

        var stamp = new DateTime(2020, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(text, stamp);
        File.SetAttributes(text, FileAttributes.Hidden | FileAttributes.ReadOnly);
        Directory.SetLastWriteTimeUtc(docs.FullName, stamp);

        var copier = new FileTreeCopier(CopyExclusions.Default(), withSecurity: false, progress: null, CancellationToken.None);
        var result = copier.Copy(_source, _target);

        Assert.Empty(result.Errors);
        Assert.Equal(1, result.HardLinks);
        Assert.Equal(1, result.Skipped);

        var copiedText = Path.Combine(_target, "Documentos", "nota.txt");
        Assert.Equal("contenido principal", File.ReadAllText(copiedText));
        Assert.Equal("flujo alternativo", File.ReadAllText(copiedText + ":oculto"));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(copiedText));
        Assert.Equal(FileAttributes.Hidden | FileAttributes.ReadOnly, File.GetAttributes(copiedText) & (FileAttributes.Hidden | FileAttributes.ReadOnly));
        Assert.Equal(stamp, Directory.GetLastWriteTimeUtc(Path.Combine(_target, "Documentos", "Sub")));

        var copiedBig = Path.Combine(_target, "Documentos", "Sub", "grande.bin");
        Assert.Equal(data, File.ReadAllBytes(copiedBig));
        File.AppendAllText(copiedBig, "x");
        Assert.Equal(data.Length + 1, new FileInfo(Path.Combine(_target, "enlace.bin")).Length);

        Assert.False(File.Exists(Path.Combine(_target, "pagefile.sys")));

        // La unión se copia como unión, sin seguirla: apunta al mismo sitio que la del origen.
        var junction = new DirectoryInfo(Path.Combine(_target, "Union"));
        Assert.True(junction.Attributes.HasFlag(FileAttributes.ReparsePoint));
        Assert.Equal(docs.FullName, junction.LinkTarget);
    }

    [Fact]
    public void StopsWhenCancelled()
    {
        File.WriteAllText(Path.Combine(_source, "a.txt"), "a");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var copier = new FileTreeCopier(CopyExclusions.Default(), withSecurity: false, progress: null, cancellation.Token);

        Assert.Throws<OperationCanceledException>(() => copier.Copy(_source, _target));
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        // Las uniones se borran sin seguirlas.
        foreach (var junction in Directory.EnumerateDirectories(_root, "Union", SearchOption.AllDirectories).ToList())
        {
            Directory.Delete(junction);
        }

        Directory.Delete(_root, recursive: true);
    }

    private static void RunCmd(string command)
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c {command}") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }
}
