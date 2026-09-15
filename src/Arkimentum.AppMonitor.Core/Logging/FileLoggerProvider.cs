using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Arkimentum.AppMonitor.Logging;

public sealed class FileLoggerOptions
{
    public string Directory { get; set; } = string.Empty;
    public string FilePrefix { get; set; } = "Arkimentum.AppMonitor";
    public int RetentionDays { get; set; } = 30;
    public int MaxFileSizeMb { get; set; } = 10;
    public LogLevel MinimumLevel { get; set; } = LogLevel.Information;
}

/// <summary>
/// Simple, dependency-free rolling file logger: one file per day per process role, rolled by size, old files pruned.
/// Lines: <c>2026-09-14 15:59:01.123 +02:00 [INF] Category: message</c>.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly FileLoggerOptions _options;
    private readonly BlockingCollection<string> _queue = new(new ConcurrentQueue<string>());
    private readonly Thread _writer;
    private readonly object _fileLock = new();
    private StreamWriter? _stream;
    private string? _currentPath;
    private DateTime _currentDate;
    private int _rollIndex;
    private DateTime _lastPrune = DateTime.MinValue;

    public FileLoggerProvider(FileLoggerOptions options)
    {
        _options = options;
        if (string.IsNullOrWhiteSpace(_options.Directory))
            _options.Directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Arkimentum", "AppMonitor", "Logs");
        _writer = new Thread(WriteLoop) { IsBackground = true, Name = "FileLogger" };
        _writer.Start();
    }

    public string Directory => _options.Directory;

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, ShortCategory(categoryName));

    internal bool IsEnabled(LogLevel level) => level != LogLevel.None && level >= _options.MinimumLevel;

    internal void Enqueue(string line)
    {
        if (!_queue.IsAddingCompleted) _queue.Add(line);
    }

    private void WriteLoop()
    {
        foreach (var line in _queue.GetConsumingEnumerable())
        {
            try
            {
                lock (_fileLock)
                {
                    EnsureFile();
                    _stream!.WriteLine(line);
                    _stream.Flush();
                }
            }
            catch
            {
                // logging must never take the process down
            }
        }
        lock (_fileLock) { _stream?.Dispose(); _stream = null; }
    }

    private void EnsureFile()
    {
        var today = DateTime.Now.Date;
        if (_stream is not null && _currentDate == today)
        {
            if (_stream.BaseStream.Length < _options.MaxFileSizeMb * 1024L * 1024L) return;
            _rollIndex++;
        }
        else if (_currentDate != today)
        {
            _rollIndex = 0;
        }

        _stream?.Dispose();
        _stream = null;
        System.IO.Directory.CreateDirectory(_options.Directory);
        _currentDate = today;
        while (true)
        {
            var name = _rollIndex == 0
                ? $"{_options.FilePrefix}_{today:yyyyMMdd}.log"
                : $"{_options.FilePrefix}_{today:yyyyMMdd}_{_rollIndex}.log";
            _currentPath = Path.Combine(_options.Directory, name);
            var fi = new FileInfo(_currentPath);
            if (fi.Exists && fi.Length >= _options.MaxFileSizeMb * 1024L * 1024L) { _rollIndex++; continue; }
            break;
        }
        var fs = new FileStream(_currentPath!, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        _stream = new StreamWriter(fs, new UTF8Encoding(false));
        Prune();
    }

    private void Prune()
    {
        if ((DateTime.UtcNow - _lastPrune).TotalHours < 1) return;
        _lastPrune = DateTime.UtcNow;
        try
        {
            var cutoff = DateTime.Now.AddDays(-Math.Max(1, _options.RetentionDays));
            foreach (var f in System.IO.Directory.EnumerateFiles(_options.Directory, $"{_options.FilePrefix}_*.log"))
            {
                try { if (File.GetLastWriteTime(f) < cutoff) File.Delete(f); } catch { }
            }
        }
        catch { }
    }

    private static string ShortCategory(string category)
    {
        var i = category.LastIndexOf('.');
        return i >= 0 && i < category.Length - 1 ? category[(i + 1)..] : category;
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _writer.Join(TimeSpan.FromSeconds(5));
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => provider.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var sb = new StringBuilder(256);
            sb.Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"))
              .Append(" [").Append(Level(logLevel)).Append("] ")
              .Append(category).Append(": ")
              .Append(formatter(state, exception));
            if (exception is not null) sb.AppendLine().Append(exception);
            provider.Enqueue(sb.ToString());
        }

        private static string Level(LogLevel l) => l switch
        {
            LogLevel.Trace => "TRC",
            LogLevel.Debug => "DBG",
            LogLevel.Information => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            LogLevel.Critical => "CRT",
            _ => "???",
        };
    }
}

public static class FileLoggerExtensions
{
    public static ILoggingBuilder AddArkimentumFile(this ILoggingBuilder builder, FileLoggerOptions options)
    {
        builder.AddProvider(new FileLoggerProvider(options));
        return builder;
    }

    public static LogLevel ParseLevel(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "trace" or "verbose" => LogLevel.Trace,
        "debug" => LogLevel.Debug,
        "warning" or "warn" => LogLevel.Warning,
        "error" => LogLevel.Error,
        "critical" or "fatal" => LogLevel.Critical,
        "none" or "off" => LogLevel.None,
        _ => LogLevel.Information,
    };
}
