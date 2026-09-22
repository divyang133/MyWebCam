using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace LocalWebcam.Diagnostics;

/// <summary>
/// Minimal file sink for <see cref="Microsoft.Extensions.Logging"/>, shared
/// by the desktop and mobile apps, so log history survives without an
/// attached debugger (spec section 24's log export needs something durable
/// to export in the first place). Rotates the file once rather than growing
/// forever across long-running sessions.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private const long MaxSizeBytes = 5 * 1024 * 1024;

    private readonly string _filePath;
    private readonly object _writeLock = new();
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new();

    public FileLoggerProvider(string filePath)
    {
        _filePath = filePath;
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        RotateIfTooLarge();
    }

    public string FilePath => _filePath;

    public ILogger CreateLogger(string categoryName) => _loggers.GetOrAdd(categoryName, name => new FileLogger(name, this));

    internal void Write(string message)
    {
        lock (_writeLock)
        {
            File.AppendAllText(_filePath, message + Environment.NewLine);
        }
    }

    private void RotateIfTooLarge()
    {
        var info = new FileInfo(_filePath);
        if (!info.Exists || info.Length < MaxSizeBytes)
        {
            return;
        }

        var previousPath = _filePath + ".old";
        File.Delete(previousPath);
        File.Move(_filePath, previousPath);
    }

    public void Dispose()
    {
        _loggers.Clear();
    }

    private sealed class FileLogger(string categoryName, FileLoggerProvider provider) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var line = $"{DateTime.Now:HH:mm:ss.fff} [{logLevel}] {categoryName}: {formatter(state, exception)}";
            if (exception is not null)
            {
                line += Environment.NewLine + exception;
            }

            provider.Write(line);
        }
    }
}
