using System.Diagnostics;
using Cloner.Core;
using Cloner.Core.FileCopy;
using Xunit.Abstractions;

namespace Cloner.IntegrationTests;

/// <summary>
/// Mide la copia por archivos con muchos archivos pequeños y algunos grandes. Solo se ejecuta
/// con la variable de entorno CLONDISK_BENCH=1; con otra carpeta en CLONDISK_BENCH_TARGET se
/// puede medir hacia otro disco.
/// </summary>
public sealed class FileCopyBenchmark(ITestOutputHelper output) : IDisposable
{
    private readonly string _source = Path.Combine(Path.GetTempPath(), "cloner-bench", "origen");
    private readonly string _target = Path.Combine(
        Environment.GetEnvironmentVariable("CLONDISK_BENCH_TARGET") ?? Path.Combine(Path.GetTempPath(), "cloner-bench"),
        "destino-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void SmallAndLargeFiles()
    {
        if (Environment.GetEnvironmentVariable("CLONDISK_BENCH") != "1")
        {
            return;
        }

        const int Directories = 100;
        const int FilesPerDirectory = 200;
        const int SmallSize = 4096;
        const int LargeFiles = 4;
        const int LargeSize = 64 * 1024 * 1024;
        if (!Directory.Exists(_source))
        {
            var random = new Random(7);
            var small = new byte[SmallSize];
            for (var d = 0; d < Directories; d++)
            {
                var directory = Directory.CreateDirectory(Path.Combine(_source, $"carpeta{d:D3}"));
                for (var f = 0; f < FilesPerDirectory; f++)
                {
                    random.NextBytes(small);
                    File.WriteAllBytes(Path.Combine(directory.FullName, $"archivo{f:D4}.html"), small);
                }
            }

            var large = new byte[LargeSize];
            for (var i = 0; i < LargeFiles; i++)
            {
                random.NextBytes(large);
                File.WriteAllBytes(Path.Combine(_source, $"grande{i}.bin"), large);
            }
        }

        var withSecurity = Elevation.IsAdministrator();
        foreach (var (shortNames, sacl, workers) in new[] { (true, true, 8), (false, true, 8), (true, false, 8), (false, false, 8), (true, true, 1) })
        {
            var target = _target + $"-{shortNames}-{sacl}-{workers}";
            Directory.CreateDirectory(target);
            var watch = Stopwatch.StartNew();
            var copier = new FileTreeCopier(CopyExclusions.Default(), withSecurity, null, CancellationToken.None, workers) { ShortNames = shortNames, Sacl = sacl };
            var result = copier.Copy(_source, target);
            watch.Stop();
            Directory.Delete(target, recursive: true);

            Assert.Empty(result.Errors);
            Assert.Equal((Directories * FilesPerDirectory) + LargeFiles, result.Files);
            var seconds = watch.Elapsed.TotalSeconds;
            output.WriteLine(
                $"Seguridad: {withSecurity}, nombres cortos: {shortNames}, SACL: {copier.Sacl}, hilos: {workers}. {seconds:F1} s: "
                + $"{result.Files / seconds:F0} archivos/s, {result.Bytes / 1048576.0 / seconds:F1} MB/s.");
            if (!withSecurity)
            {
                break;
            }
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_target))
        {
            Directory.Delete(_target, recursive: true);
        }
    }
}
