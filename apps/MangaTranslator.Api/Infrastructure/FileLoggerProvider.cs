using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Text;

namespace MangaTranslator.Api.Infrastructure;

/// <summary>
/// Small, dependency-free file logger for the desktop installation. The
/// desktop shell and API have separate files so a user can report startup
/// failures even when the renderer never becomes available.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private const long MaximumLogBytes = 8 * 1024 * 1024;
    private readonly string _logPath;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new(StringComparer.Ordinal);

    public FileLoggerProvider(string logsRoot, string fileName = "api.log")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logsRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        Directory.CreateDirectory(logsRoot);
        _logPath = Path.Combine(logsRoot, fileName);
    }

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, category => new FileLogger(this, category));

    internal void Write(LogLevel logLevel, string categoryName, EventId eventId, string message, Exception? exception)
    {
        var builder = new StringBuilder()
            .Append(DateTimeOffset.UtcNow.ToString("O"))
            .Append(" [").Append(logLevel).Append("] ")
            .Append(categoryName);
        if (eventId.Id != 0 || !string.IsNullOrWhiteSpace(eventId.Name))
        {
            builder.Append(" (").Append(eventId.Id).Append('/').Append(eventId.Name).Append(')');
        }
        builder.AppendLine()
            .AppendLine(message);
        if (exception is not null)
        {
            builder.AppendLine(exception.ToString());
        }

        try
        {
            lock (_gate)
            {
                RotateIfNeeded(builder.Length * sizeof(char));
                File.AppendAllText(_logPath, builder.ToString(), Encoding.UTF8);
            }
        }
        catch
        {
            // Logging must never take down the API if the user log directory
            // is unavailable or locked by another process.
        }
    }

    private void RotateIfNeeded(long incomingBytes)
    {
        if (!File.Exists(_logPath)) return;
        var length = new FileInfo(_logPath).Length;
        if (length + incomingBytes <= MaximumLogBytes) return;

        var rotatedPath = _logPath + ".1";
        try
        {
            File.Move(_logPath, rotatedPath, overwrite: true);
        }
        catch
        {
            // Continue with the existing file if rotation is not possible.
        }
    }

    public void Dispose() => _loggers.Clear();

    private sealed class FileLogger(FileLoggerProvider provider, string categoryName) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel) || formatter is null) return;
            provider.Write(logLevel, categoryName, eventId, formatter(state, exception), exception);
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}
