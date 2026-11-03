using System.Text;
using Microsoft.Extensions.Logging;

namespace ModBot.Hosting;

/// <summary>Where the log files go and how long they're kept (<c>Logging:File</c> in appsettings.json).</summary>
public class FileLoggerOptions
{
    /// <summary>Folder for the log files, relative to where the bot runs. Empty turns file logging off.</summary>
    public string Folder { get; set; } = "logs";

    /// <summary>How many days of files to keep, counting today. Older files are deleted at startup and each new day.</summary>
    public int RetainDays { get; set; } = 5;
}

/// <summary>
/// Writes the log to one file per day (<c>logs/modbot-2026-11-02.log</c>), appending, one line per entry with the
/// server tag ("=> Goo Lagoon (123)") on the same line so a file can be searched by server, and deletes files older
/// than <see cref="FileLoggerOptions.RetainDays"/>. The same level filters as the console apply (see Program.cs), so
/// routine HTTP and Lavalink chatter stays out. Shows up in config as the "File" provider.
/// </summary>
[ProviderAlias("File")]
public sealed class FileLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private readonly FileLoggerOptions _options;
    private readonly object _lock = new();
    private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();
    private StreamWriter? _writer;
    private DateOnly _day;

    /// <summary>Set after shutdown, or once the folder turned out to be unusable (no permission, bad path): from then
    /// on entries only go to the console.</summary>
    private bool _stopped;

    public FileLoggerProvider(FileLoggerOptions options)
    {
        _options = options;
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

    public void Dispose()
    {
        lock (_lock)
        {
            _stopped = true;
            _writer?.Dispose();
            _writer = null;
        }
    }

    private void Write(LogLevel level, string category, string message, Exception? exception)
    {
        if (_options.Folder.Length == 0 || _stopped)
        {
            return;
        }

        var line = new StringBuilder()
            .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss"))
            .Append(' ').Append(LevelName(level)).Append(' ')
            .Append(category).Append(": ")
            .Append(OneLine(message));
        _scopes.ForEachScope((scope, text) => text.Append("  => ").Append(OneLine(scope?.ToString() ?? "")), line);
        if (exception is not null)
        {
            // Stack traces keep their lines, indented, so they're readable but clearly part of the entry above.
            line.AppendLine().Append("    ").Append(exception.ToString().Replace("\n", "\n    "));
        }

        lock (_lock)
        {
            if (_stopped)
            {
                return;
            }

            // A logger must never throw: the framework would pass it on to whatever code was logging, failing that
            // command or handler. The console still has every entry.
            try
            {
                WriterForToday().WriteLine(line.ToString());
            }
            catch (IOException)
            {
                // Full disk or a file locked for a moment: skip this entry and try again with the next one.
            }
            catch (Exception ex)
            {
                // No permission, a bad path, a read-only folder: it won't fix itself, so stop trying and say so once.
                _stopped = true;
                _writer?.Dispose();
                _writer = null;
                Console.Error.WriteLine($"Log files are turned off: can't write to '{_options.Folder}' ({ex.Message}). " +
                                        "Logs still show in the console. Check Logging:File:Folder.");
            }
        }
    }

    /// <summary>Today's file, opening it (and clearing out old ones) when the day changes. Call under the lock.</summary>
    private StreamWriter WriterForToday()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (_writer is not null && today == _day)
        {
            return _writer;
        }

        _writer?.Dispose();
        Directory.CreateDirectory(_options.Folder);
        var path = Path.Combine(_options.Folder, $"modbot-{today:yyyy-MM-dd}.log");
        _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
            Encoding.UTF8) { AutoFlush = true };
        _day = today;
        DeleteOldFiles(today);
        return _writer;
    }

    /// <summary>Deletes files older than the retention window. Best effort: a file it can't delete (open in an editor,
    /// no permission) is left for next time, and never stops today's logging.</summary>
    private void DeleteOldFiles(DateOnly today)
    {
        var oldest = today.AddDays(-(Math.Max(1, _options.RetainDays) - 1));
        try
        {
            foreach (var file in Directory.EnumerateFiles(_options.Folder, "modbot-*.log"))
            {
                var name = Path.GetFileNameWithoutExtension(file)["modbot-".Length..];
                if (DateOnly.TryParseExact(name, "yyyy-MM-dd", out var day) && day < oldest)
                {
                    try
                    {
                        File.Delete(file);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // Left for next time.
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Couldn't list the folder; nothing to clean up this time.
        }
    }

    /// <summary>Line breaks (including any in a server's name) become spaces, so one entry is one line and nothing
    /// can fake an extra entry.</summary>
    private static string OneLine(string text) => text.Replace("\r", "").Replace('\n', ' ');

    private static string LevelName(LogLevel level) => level switch
    {
        LogLevel.Trace => "trce",
        LogLevel.Debug => "dbug",
        LogLevel.Information => "info",
        LogLevel.Warning => "WARN",
        LogLevel.Error => "FAIL",
        LogLevel.Critical => "CRIT",
        _ => "    ",
    };

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => provider._scopes.Push(state);

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                provider.Write(logLevel, category, formatter(state, exception), exception);
            }
        }
    }
}
