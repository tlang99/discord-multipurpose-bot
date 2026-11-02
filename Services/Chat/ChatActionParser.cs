using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using ModBot.Models;
using ModBot.Services.Reminders;

namespace ModBot.Services.Chat;

/// <summary>Something a chat message asked the bot to do.</summary>
public abstract record ChatAction;

public record PlayMusicAction(string Query) : ChatAction;

/// <param name="Players">Total squad size including the person asking, 2-6.</param>
public record CreateLfgAction(int Players, LfgDetails Details) : ChatAction;

/// <summary>The message asked for music, but which song couldn't be worked out.</summary>
public record UnclearSongAction : ChatAction;

/// <param name="Text">What to remind about; empty if the message didn't say ("remind me in 10 minutes").</param>
public record SetReminderAction(DateTimeOffset DueAt, string Text) : ChatAction;

/// <param name="FromModel">The model picked it, rather than the message saying it plainly. Controls that can't be
/// undone (stop, clear, end...) aren't done on the model's word: the person is asked to say it plainly.</param>
public record MusicControlAction(MusicCommandRequest Request, bool FromModel = false) : ChatAction;

/// <summary>The message asked to control the music, but not in a way that matched any control.</summary>
public record UnclearMusicAction : ChatAction;

/// <summary>
/// Works out whether a chat message is asking the bot to play music, post an LFG or set a reminder, and with what
/// details.
/// </summary>
/// <remarks>
/// Decided by testing against llama3.2:
/// <list type="bullet">
/// <item>Whether it's a request at all: the model, asked only that, with its answer forced to one of three values. It
/// got every test message right, including near misses like "do you know how to make an lfg?". Offered the actions as
/// tools instead, it called one for almost every message (asking for a Hardpoint tip posted an LFG).</item>
/// <item>Which song: the model, for turning "can you put on some lofi beats" into a search. YouTube links are taken
/// from the message as written, since the model rewrote them into song names.</item>
/// <item>LFG details: <see cref="LfgRequestReader"/>'s keyword rules, since the model invented ranks and mic rules
/// nobody asked for.</item>
/// <item>Reminders: <see cref="ReminderRequestReader"/>, checked before the model is asked anything. Adding them as a
/// fourth answer to the request check made the model worse at the other three.</item>
/// <item>Music controls (skip, pause, the queue, music sessions...): <see cref="MusicCommandReader"/> first, for
/// plainly worded ones. Looser wording goes to the model: "music_control" as an answer to the request check (tested:
/// it caught 14 of 16 controls, the two misses being ones the reader handles, and flagged none of 11 chat messages,
/// with no change to the other answers), then a second
/// call to pick which. That second call mixed up some (it read "this song is awful, get rid of it" as removing song
/// 1), so controls that can't be undone are never done on its say-so alone.</item>
/// </list>
/// Only ever sees the asker's own message, never the channel history, so nothing someone else wrote earlier can make
/// the bot act. No Discord types: running the action is the caller's job.
/// </remarks>
public partial class ChatActionParser
{
    private const string ClassifySchema =
        """{"type":"object","properties":{"action":{"type":"string","enum":["none","play_music","create_lfg","music_control"]}},"required":["action"]}""";

    private const string ClassifyPrompt =
        "You decide whether a Discord message is a direct request for the bot to DO something. Answer play_music only " +
        "if the person explicitly asks the bot to play, queue or put on a song or music. Answer create_lfg only if " +
        "they explicitly ask the bot to make, post or create an LFG or looking-for-group post, or to find them players " +
        "or a squad. Answer music_control only if they explicitly ask the bot to skip, pause, resume or stop the music, " +
        "show or change the song queue, or start, end, join or leave a music session. Questions about music or LFG, " +
        "opinions, and general chat are none. When unsure, answer none.";

    private const string MusicControlSchema =
        """{"type":"object","properties":{"command":{"type":"string","enum":["skip","pause","resume","stop","now_playing","show_queue","shuffle","loop_song","loop_queue","loop_off","clear_queue","start_session","begin_playing","end_session","leave_session","show_session","unclear"]}},"required":["command"]}""";

    private const string MusicControlPrompt =
        "The message asks a Discord music bot to control its music. Pick the command that matches: skip (skip or vote " +
        "to skip the current song), pause, resume (unpause), stop (stop the music and leave), now_playing (what song " +
        "is on), show_queue (list upcoming songs), shuffle, loop_song (repeat this song), loop_queue (repeat the whole " +
        "queue), loop_off (stop repeating), clear_queue (remove every upcoming song), start_session (open a new music " +
        "session where people take turns adding songs), begin_playing (start playing the session's songs), " +
        "end_session (close the music session), leave_session (the person leaves the session and their songs are " +
        "removed), show_session (show the session menu). If none fit, answer unclear.";

    private const string PlaySchema =
        """{"type":"object","properties":{"query":{"type":"string"}},"required":["query"]}""";

    private const string PlayPrompt =
        "The message asks a Discord bot to play music. Give the YouTube search for what they want in \"query\": the " +
        "song and artist if they named them, or their own words for a genre or mood. Don't pick a specific song they " +
        "didn't ask for.";

    private readonly IChatService _chat;
    private readonly ReminderService _reminders;
    private readonly ILogger<ChatActionParser> _logger;

    public ChatActionParser(IChatService chat, ReminderService reminders, ILogger<ChatActionParser> logger)
    {
        _chat = chat;
        _reminders = reminders;
        _logger = logger;
    }

    /// <summary>The action <paramref name="message"/> asks for, or null if it's just chat (or the model couldn't be
    /// reached, so the caller falls back to chatting).</summary>
    public async Task<ChatAction?> ParseAsync(string message, CancellationToken cancellationToken = default)
    {
        // A reminder phrase plus a time is unambiguous, so it's settled without the model.
        if (ReminderRequestReader.Read(message, DateTimeOffset.UtcNow, _reminders.TimeZone) is { } reminder)
        {
            return new SetReminderAction(reminder.Time.DueAt, reminder.Text);
        }

        // So is a plainly worded music control ("skip this", "show the queue").
        if (MusicCommandReader.Read(message) is { } control)
        {
            return new MusicControlAction(control);
        }

        string kind;
        try
        {
            using var classified = JsonDocument.Parse(
                await _chat.CompleteJsonAsync(ClassifyPrompt, $"Message: {message}", ClassifySchema, cancellationToken));
            kind = classified.RootElement.GetProperty("action").GetString() ?? "none";
        }
        catch (Exception ex) when (ex is ChatServiceException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            _logger.LogDebug(ex, "Couldn't classify a chat message; treating it as chat.");
            return null;
        }

        switch (kind)
        {
            case "play_music":
                return await ReadSongAsync(message, cancellationToken);

            case "create_lfg":
                var (players, details) = LfgRequestReader.Read(message);
                return new CreateLfgAction(players, details);

            case "music_control":
                return await ReadMusicControlAsync(message, cancellationToken);

            default:
                return null;
        }
    }

    private async Task<ChatAction> ReadMusicControlAsync(string message, CancellationToken cancellationToken)
    {
        try
        {
            using var json = JsonDocument.Parse(await _chat.CompleteJsonAsync(MusicControlPrompt, $"Message: {message}",
                MusicControlSchema, cancellationToken));
            MusicCommand? command = json.RootElement.GetProperty("command").GetString() switch
            {
                "skip" => MusicCommand.Skip,
                "pause" => MusicCommand.Pause,
                "resume" => MusicCommand.Resume,
                "stop" => MusicCommand.Stop,
                "now_playing" => MusicCommand.NowPlaying,
                "show_queue" => MusicCommand.ShowQueue,
                "shuffle" => MusicCommand.Shuffle,
                "loop_song" => MusicCommand.LoopSong,
                "loop_queue" => MusicCommand.LoopQueue,
                "loop_off" => MusicCommand.LoopOff,
                "clear_queue" => MusicCommand.ClearQueue,
                "start_session" => MusicCommand.StartSession,
                "begin_playing" => MusicCommand.BeginSession,
                "end_session" => MusicCommand.EndSession,
                "leave_session" => MusicCommand.LeaveSession,
                "show_session" => MusicCommand.ShowSession,
                _ => null,
            };

            if (command is { } picked)
            {
                return new MusicControlAction(new MusicCommandRequest(picked), FromModel: true);
            }
        }
        catch (Exception ex) when (ex is ChatServiceException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            _logger.LogDebug(ex, "Couldn't work out the music control from a chat message.");
        }

        return new UnclearMusicAction();
    }

    private async Task<ChatAction> ReadSongAsync(string message, CancellationToken cancellationToken)
    {
        if (YouTubeLinkRegex().Match(message) is { Success: true } link)
        {
            return new PlayMusicAction(link.Value);
        }

        try
        {
            using var json = JsonDocument.Parse(
                await _chat.CompleteJsonAsync(PlayPrompt, $"Message: {message}", PlaySchema, cancellationToken));
            var query = json.RootElement.GetProperty("query").GetString()?.Trim();
            if (!string.IsNullOrEmpty(query))
            {
                return new PlayMusicAction(query.Length > 200 ? query[..200] : query);
            }
        }
        catch (Exception ex) when (ex is ChatServiceException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            _logger.LogDebug(ex, "Couldn't work out the song from a chat message.");
        }

        return new UnclearSongAction();
    }

    [GeneratedRegex(@"https?://(?:[\w-]+\.)*(?:youtube\.com|youtu\.be)/\S+", RegexOptions.IgnoreCase)]
    private static partial Regex YouTubeLinkRegex();
}
