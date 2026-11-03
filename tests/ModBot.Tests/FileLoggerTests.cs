using Microsoft.Extensions.Logging;
using ModBot.Hosting;

namespace ModBot.Tests;

public sealed class FileLoggerTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "modbot-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public void Writes_one_line_per_entry_with_the_server_tag()
    {
        using (var provider = new FileLoggerProvider(new FileLoggerOptions { Folder = _folder }))
        {
            var logger = provider.CreateLogger("Test");
            using (logger.BeginScope("{Server}", "Goo\nLagoon (123)"))
            {
                logger.LogInformation("Music channel set to {Channel}.", "#music");
            }

            logger.LogWarning("No server here.");
        }

        var lines = File.ReadAllLines(TodaysFile());
        Assert.Equal(2, lines.Length);
        Assert.EndsWith("info Test: Music channel set to #music.  => Goo Lagoon (123)", lines[0]);
        Assert.EndsWith("WARN Test: No server here.", lines[1]);
    }

    [Fact]
    public void Keeps_only_the_last_few_days()
    {
        Directory.CreateDirectory(_folder);
        var today = DateOnly.FromDateTime(DateTime.Now);
        var kept = Path.Combine(_folder, $"modbot-{today.AddDays(-4):yyyy-MM-dd}.log");
        var old = Path.Combine(_folder, $"modbot-{today.AddDays(-5):yyyy-MM-dd}.log");
        var other = Path.Combine(_folder, "notes.log");
        File.WriteAllText(kept, "");
        File.WriteAllText(old, "");
        File.WriteAllText(other, "");

        using (var provider = new FileLoggerProvider(new FileLoggerOptions { Folder = _folder, RetainDays = 5 }))
        {
            provider.CreateLogger("Test").LogInformation("hello");
        }

        Assert.True(File.Exists(kept));
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(other)); // only its own files are touched
        Assert.True(File.Exists(TodaysFile()));
    }

    private string TodaysFile() => Path.Combine(_folder, $"modbot-{DateTime.Now:yyyy-MM-dd}.log");
}

public class FileLoggerFailureTests
{
    [Fact]
    public void An_unusable_folder_never_throws_into_the_caller()
    {
        // A path with a character Windows and Linux both refuse in a folder name ('\0').
        using var provider = new FileLoggerProvider(new FileLoggerOptions { Folder = "bad\0folder" });
        var logger = provider.CreateLogger("Test");

        logger.LogInformation("first");
        logger.LogInformation("second"); // file logging is off by now; still no throw
    }

    [Fact]
    public void Nothing_is_written_after_shutdown()
    {
        var folder = Path.Combine(Path.GetTempPath(), "modbot-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var provider = new FileLoggerProvider(new FileLoggerOptions { Folder = folder });
            var logger = provider.CreateLogger("Test");
            provider.Dispose();

            logger.LogInformation("too late");

            Assert.False(Directory.Exists(folder));
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }
}
