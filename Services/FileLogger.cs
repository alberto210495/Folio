using System.Globalization;
using System.Text;

namespace Folio.Services;

/// <summary>
/// Writes log lines to <c>folio-yyyyMMdd.log</c> in one folder, a new file each day, keeping the newest
/// <see cref="KeepDays"/>. Used by the installed copy, which has no console. Levels and categories are
/// filtered by the normal <c>Logging</c> settings (provider alias <c>File</c>).
/// </summary>
[ProviderAlias("File")]
public sealed class FileLoggerProvider(string directory, TimeProvider time, int keepDays = FileLoggerProvider.KeepDays) : ILoggerProvider
{
    public const int KeepDays = 14;

    private readonly object _lock = new();
    private StreamWriter? _writer;
    private DateOnly _day;

    public string Directory { get; } = directory;

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, ShortCategory(categoryName));

    internal void Write(LogLevel level, string category, string message, Exception? exception)
    {
        var now = time.GetLocalNow();
        var line = new StringBuilder()
            .Append(now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)).Append(' ')
            .Append(Level(level)).Append(' ')
            .Append(category).Append(": ")
            .Append(message.ReplaceLineEndings(" | "));
        if (exception is not null) line.AppendLine().Append(exception);

        lock (_lock)
        {
            try
            {
                var today = DateOnly.FromDateTime(now.DateTime);
                if (_writer is null || today != _day) Roll(today);
                _writer!.WriteLine(line);
                _writer.Flush(); // a crash shouldn't lose the line that explains it
            }
            catch (IOException) { /* logging must never take Folio down */ }
            catch (UnauthorizedAccessException) { }
        }
    }

    private void Roll(DateOnly day)
    {
        _writer?.Dispose();
        System.IO.Directory.CreateDirectory(Directory);
        var path = Path.Combine(Directory, $"folio-{day:yyyyMMdd}.log");
        // FileShare.ReadWrite lets you open the log in an editor while Folio is still writing to it.
        _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false));
        _day = day;

        foreach (var old in new DirectoryInfo(Directory).GetFiles("folio-*.log")
                     .OrderByDescending(f => f.Name, StringComparer.Ordinal).Skip(Math.Max(1, keepDays)))
        {
            try { old.Delete(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>"Folio.Services.BackupService" → "BackupService"; framework categories keep their last two parts.</summary>
    private static string ShortCategory(string name)
    {
        var parts = name.Split('.');
        return name.StartsWith("Folio.", StringComparison.Ordinal) || parts.Length < 2 ? parts[^1] : $"{parts[^2]}.{parts[^1]}";
    }

    private static string Level(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRACE",
        LogLevel.Debug => "DEBUG",
        LogLevel.Information => "INFO ",
        LogLevel.Warning => "WARN ",
        LogLevel.Error => "ERROR",
        LogLevel.Critical => "CRIT ",
        _ => "     ",
    };

    public void Dispose()
    {
        lock (_lock)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel)) provider.Write(logLevel, category, formatter(state, exception), exception);
        }
    }
}
