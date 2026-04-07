using System;
using System.IO;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace HermesDesktop.Services;

/// <summary>
/// Simple file logger that writes structured log entries to a rotating log file.
/// Keeps the last 5 MB; rotates to .1 backup when exceeded.
/// </summary>
internal sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _logPath;
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new();
    private readonly object _writeLock = new();
    private const long MaxFileSizeBytes = 5 * 1024 * 1024; // 5 MB

    public FileLoggerProvider(string logPath)
    {
        _logPath = logPath;
        var dir = Path.GetDirectoryName(logPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
    }

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, name => new FileLogger(name, this));

    internal void WriteEntry(string category, LogLevel level, string message)
    {
        lock (_writeLock)
        {
            try
            {
                RotateIfNeeded();
                var timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff");
                var levelTag = level switch
                {
                    LogLevel.Trace => "TRC",
                    LogLevel.Debug => "DBG",
                    LogLevel.Information => "INF",
                    LogLevel.Warning => "WRN",
                    LogLevel.Error => "ERR",
                    LogLevel.Critical => "CRT",
                    _ => "???"
                };
                var shortCategory = category.Contains('.') ? category[(category.LastIndexOf('.') + 1)..] : category;
                File.AppendAllText(_logPath, $"[{timestamp}] [{levelTag}] {shortCategory}: {message}{Environment.NewLine}");
            }
            catch
            {
                // Logging must never crash the app
            }
        }
    }

    private void RotateIfNeeded()
    {
        if (!File.Exists(_logPath)) return;
        var info = new FileInfo(_logPath);
        if (info.Length <= MaxFileSizeBytes) return;

        var backup = _logPath + ".1";
        if (File.Exists(backup)) File.Delete(backup);
        File.Move(_logPath, backup);
    }

    public void Dispose() => _loggers.Clear();
}

internal sealed class FileLogger : ILogger
{
    private readonly string _category;
    private readonly FileLoggerProvider _provider;

    public FileLogger(string category, FileLoggerProvider provider)
    {
        _category = category;
        _provider = provider;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;
        var message = formatter(state, exception);
        if (exception is not null)
            message += Environment.NewLine + exception;
        _provider.WriteEntry(_category, logLevel, message);
    }
}
