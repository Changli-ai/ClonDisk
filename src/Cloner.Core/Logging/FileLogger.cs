using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Cloner.Core.Logging;

/// <summary>Registro de una operación en un archivo de texto (RF-33). Cada línea se escribe al momento.</summary>
public sealed class FileLogger : ILogger, IDisposable
{
    private readonly StreamWriter _writer;
    private readonly TimeProvider _time;
    private readonly Lock _lock = new();

    private FileLogger(string path, TimeProvider time)
    {
        FilePath = path;
        _time = time;
        _writer = new StreamWriter(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    public string FilePath { get; }

    /// <summary>Crea un archivo nuevo en <paramref name="directory"/> con el nombre «prefijo-fecha-hora.log».</summary>
    public static FileLogger Create(string directory, string prefix, TimeProvider? time = null)
    {
        time ??= TimeProvider.System;
        Directory.CreateDirectory(directory);
        var stamp = time.GetLocalNow().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var path = Path.Combine(directory, $"{prefix}-{stamp}.log");
        for (var i = 2; File.Exists(path); i++)
        {
            path = Path.Combine(directory, $"{prefix}-{stamp}-{i}.log");
        }

        return new FileLogger(path, time);
    }

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);
        if (!IsEnabled(logLevel))
        {
            return;
        }

        var line = new StringBuilder()
            .Append(_time.GetLocalNow().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
            .Append(' ')
            .Append(Label(logLevel))
            .Append(' ')
            .Append(formatter(state, exception));
        if (exception is not null)
        {
            line.AppendLine().Append(exception);
        }

        lock (_lock)
        {
            _writer.WriteLine(line.ToString());
            _writer.Flush();
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _writer.Dispose();
        }
    }

    private static string Label(LogLevel level) => level switch
    {
        LogLevel.Trace or LogLevel.Debug => "DEPURACIÓN",
        LogLevel.Information => "INFO ",
        LogLevel.Warning => "AVISO",
        _ => "ERROR",
    };
}
