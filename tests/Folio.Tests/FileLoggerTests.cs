using Folio.Services;
using Microsoft.Extensions.Logging;

namespace Folio.Tests;

public sealed class FileLoggerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"folio-logs-{Guid.NewGuid():N}");
    private readonly FakeTime _time = new() { Now = new DateTimeOffset(2026, 11, 18, 15, 0, 0, TimeSpan.Zero) };

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string[] Files() => Directory.GetFiles(_dir, "folio-*.log").Select(Path.GetFileName).Order().ToArray()!;

    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); // Folio may still be writing
        return new StreamReader(stream).ReadToEnd();
    }

    [Fact]
    public void Writes_level_category_and_message_to_todays_file()
    {
        using (var provider = new FileLoggerProvider(_dir, _time))
        {
            provider.CreateLogger("Folio.Services.BackupService").LogWarning("Couldn't copy backup {Name}", "folio-1.db");
            provider.CreateLogger("Microsoft.Hosting.Lifetime").LogInformation("Application started");
        }

        var file = Assert.Single(Files());
        Assert.Equal($"folio-{_time.GetLocalNow():yyyyMMdd}.log", file);
        var lines = File.ReadAllLines(Path.Combine(_dir, file));
        Assert.Contains("WARN  BackupService: Couldn't copy backup folio-1.db", lines[0]);
        Assert.Contains("INFO  Hosting.Lifetime: Application started", lines[1]);
    }

    [Fact]
    public void Multi_line_messages_stay_on_one_line_and_exceptions_are_included()
    {
        using (var provider = new FileLoggerProvider(_dir, _time))
            provider.CreateLogger("Folio.Test").LogError(new InvalidOperationException("boom"), "first\nsecond");

        var text = File.ReadAllText(Path.Combine(_dir, Files()[0]));
        Assert.Contains("ERROR Test: first | second", text);
        Assert.Contains("System.InvalidOperationException: boom", text);
    }

    [Fact]
    public void Starts_a_new_file_each_day_and_keeps_only_the_newest()
    {
        using (var provider = new FileLoggerProvider(_dir, _time, keepDays: 3))
        {
            var log = provider.CreateLogger("Folio.Test");
            for (var day = 0; day < 5; day++)
            {
                log.LogInformation("day {Day}", day);
                _time.Now = _time.Now.AddDays(1);
            }
        }

        var files = Files();
        Assert.Equal(3, files.Length);
        Assert.Contains("day 4", File.ReadAllText(Path.Combine(_dir, files[^1])));
    }

    [Fact]
    public void The_log_can_be_read_while_folio_is_writing_it()
    {
        using var provider = new FileLoggerProvider(_dir, _time);
        provider.CreateLogger("Folio.Test").LogInformation("still running");
        Assert.Contains("still running", ReadShared(Path.Combine(_dir, Files()[0])));
    }

    [Fact]
    public void Concurrent_writes_never_interleave_lines()
    {
        using (var provider = new FileLoggerProvider(_dir, _time))
        {
            var log = provider.CreateLogger("Folio.Test");
            Parallel.For(0, 400, i => log.LogInformation("message {I:000}", i));
        }

        var lines = File.ReadAllLines(Path.Combine(_dir, Files()[0]));
        Assert.Equal(400, lines.Length);
        Assert.All(lines, l => Assert.Matches(@"INFO  Test: message \d{3}$", l));
    }
}
