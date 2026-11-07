using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using ModBot.Models;
using ModBot.Services.Polls;
using ModBot.Services.Reminders;
using ModBot.Services.Search;

namespace ModBot.Services.Chat;

/// <summary>Something a chat message asked the bot to do.</summary>
public abstract record ChatAction;

/// <param name="StartPlaying">False for "add X to the queue": queue it without starting the music.</param>
public record PlayMusicAction(string Query, bool StartPlaying = true) : ChatAction;

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

/// <param name="Hours">How long it runs, if the message said; null for the default.</param>
/// <param name="FromModel">The model wrote the question, so the word filter applies to it even for moderators.</param>
public record CreatePollAction(string Question, IReadOnlyList<string> Answers, int? Hours, bool AllowMultiple,
    bool FromModel = false) : ChatAction;

/// <summary>The message asked for a poll, but what it should ask couldn't be worked out.</summary>
public record UnclearPollAction : ChatAction;

/// <summary>"End my poll": end the asker's open poll early (or the one they replied to).</summary>
/// <param name="SaidMine">"end my poll", rather than "end the poll" / "end that poll", which could mean someone
/// else's.</param>
public record EndPollAction(bool SaidMine = false) : ChatAction;

/// <summary>"Search for …" / "look up …": search the web and answer from the results.</summary>
public record WebSearchAction(string Query) : ChatAction;

/// <summary>
/// Works out whether a chat message is asking the bot to play music, post an LFG, set a reminder or post a poll, and
/// with what details.
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
/// <item>Polls: <see cref="PollRequestReader"/>, also before the model, for the same reason. A poll laid out plainly
/// ("question? a, b or c") is read by rules; otherwise the model pulls out the question and answers, and only answers
/// the person actually wrote are kept, with Yes/No when there are none.</item>
/// <item>Music controls (skip, pause, the queue, the menu...): <see cref="MusicCommandReader"/> first, for plainly
/// worded ones. Looser wording goes to the model: "music_control" as an answer to the request check (tested: it
/// caught 14 of 16 controls, the two misses being ones the reader handles, and flagged none of 11 chat messages, with
/// no change to the other answers), then a second call to pick which. That second call mixed up some (it read "this
/// song is awful, get rid of it" as removing a song), so controls that can't be undone are never done on its say-so
/// alone.</item>
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
        """{"type":"object","properties":{"command":{"type":"string","enum":["skip","pause","resume","stop","now_playing","show_queue","loop_song","loop_queue","loop_off","clear_queue","start_playing","remove_mine","show_menu","unclear"]}},"required":["command"]}""";

    private const string MusicControlPrompt =
        "The message asks a Discord music bot to control its music. Pick the command that matches: skip (skip or vote " +
        "to skip the current song), pause, resume (unpause), stop (stop the music and leave), now_playing (what song " +
        "is on), show_queue (list the waiting songs), loop_song (repeat this song), loop_queue (repeat the whole " +
        "queue), loop_off (stop repeating), clear_queue (remove every waiting song), start_playing (start playing the " +
        "queue, or start a music session), remove_mine (take the person's own songs out of the queue, or leave the " +
        "music session), show_menu (show the music menu). If none fit, answer unclear.";

    private const string PlaySchema =
        """{"type":"object","properties":{"query":{"type":"string"}},"required":["query"]}""";

    private const string PlayPrompt =
        "The message asks a Discord bot to play music. Give the YouTube search for what they want in \"query\": the " +
        "song and artist if they named them, or their own words for a genre or mood. Don't pick a specific song they " +
        "didn't ask for.";

    private const string PollSchema =
        """{"type":"object","properties":{"question":{"type":"string"},"answers":{"type":"array","items":{"type":"string"}}},"required":["question","answers"]}""";

    private const string PollPrompt =
        "The message asks a Discord bot to post a poll. In \"question\", give what the poll asks, in the person's own " +
        "words, written as a question. In \"answers\", copy the choices they listed, word for word, in their order. " +
        "If they didn't list any choices, give an empty list: never make choices up.";

    private readonly IChatService _chat;
    private readonly ReminderService _reminders;
    private readonly ILogger<ChatActionParser> _logger;

    public ChatActionParser(IChatService chat, ReminderService reminders, ILogger<ChatActionParser> logger)
    {
        _chat = chat;
        _reminders = reminders;
        _logger = logger;
    }

    /// <summary>A web search asked for in so many words ("search for …"), or null.</summary>
    public static WebSearchAction? ReadSearch(string message)
        => SearchRequestReader.Read(message) is { } query ? new WebSearchAction(query) : null;

    /// <summary>The action <paramref name="message"/> asks for, or null if it's just chat (or the model couldn't be
    /// reached, so the caller falls back to chatting).</summary>
    /// <param name="lfgGame">The server's LFG playlists and ranks (/settings lfg-game); blank when not given.</param>
    public async Task<ChatAction?> ParseAsync(string message, LfgGameProfile? lfgGame = null,
        CancellationToken cancellationToken = default)
    {
        // A web search is only done when asked for in so many words ("search for …"), so it's read by rules too, and
        // first: what follows "search for" is the search, whatever it says ("search for poll ideas").
        if (ReadSearch(message) is { } search)
        {
            return search;
        }

        // A reminder phrase plus a time is unambiguous, so it's settled without the model.
        if (ReminderRequestReader.Read(message, DateTimeOffset.UtcNow, _reminders.TimeZone) is { } reminder)
        {
            return new SetReminderAction(reminder.Time.DueAt, reminder.Text);
        }

        // And a poll phrase ("make a poll", "end my poll").
        if (PollRequestReader.Read(message) is { } poll)
        {
            return await ReadPollAsync(poll, cancellationToken);
        }

        if (PollRequestReader.IsEndRequest(message))
        {
            return new EndPollAction(PollRequestReader.SaysMine(message));
        }

        // So is a plainly worded music control ("skip this", "show the queue").
        if (MusicCommandReader.Read(message) is { } control)
        {
            return new MusicControlAction(control);
        }

        // Speech-to-text puts a comma wherever someone paused: "Play, stay fly." reads to the model as chat ("stay
        // cool"), while "Play stay fly." is plainly a song.
        message = PauseAfterPlay(message);

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
                var (players, details) = LfgRequestReader.Read(message, lfgGame ?? LfgGameProfile.Empty);
                return new CreateLfgAction(players, details);

            case "music_control":
                return await ReadMusicControlAsync(message, cancellationToken);

            default:
                return null;
        }
    }

    /// <summary>"Play, stay fly." as "Play stay fly.": punctuation straight after a leading "play" / "put on" /
    /// "queue" is a pause in speech, not a break in meaning.</summary>
    public static string PauseAfterPlay(string message) => PauseAfterPlayRegex().Replace(message, "$1 ", 1);

    [GeneratedRegex(@"^(\W*(?:(?:hey|ok(?:ay)?|please|can\s+you|could\s+you)[\s,]+)*(?:play|put\s+on|queue(?:\s+up)?))\s*[,.:;!\-–]+\s*",
        RegexOptions.IgnoreCase)]
    private static partial Regex PauseAfterPlayRegex();

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
                "loop_song" => MusicCommand.LoopSong,
                "loop_queue" => MusicCommand.LoopQueue,
                "loop_off" => MusicCommand.LoopOff,
                "clear_queue" => MusicCommand.ClearQueue,
                "start_playing" => MusicCommand.Start,
                "remove_mine" => MusicCommand.RemoveMine,
                "show_menu" => MusicCommand.ShowMenu,
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

    private async Task<ChatAction> ReadPollAsync(PollRequest request, CancellationToken cancellationToken)
    {
        if (request.Question is { } laidOutQuestion && request.Answers is { } laidOutAnswers)
        {
            return new CreatePollAction(laidOutQuestion, laidOutAnswers, request.Hours, request.AllowMultiple);
        }

        if (request.Text.Length == 0)
        {
            return new UnclearPollAction();
        }

        try
        {
            using var json = JsonDocument.Parse(await _chat.CompleteJsonAsync(PollPrompt, $"Message: {request.Text}",
                PollSchema, cancellationToken));
            var question = json.RootElement.GetProperty("question").GetString()?.Trim();
            if (string.IsNullOrEmpty(question))
            {
                return new UnclearPollAction();
            }

            var offered = json.RootElement.GetProperty("answers").EnumerateArray()
                .Select(a => a.GetString() ?? "");
            var answers = PollRequestReader.KeepAnswersFrom(request.Text, offered);
            if (answers.Count < PollRules.MinAnswers)
            {
                // No choices given: Yes/No, unless the question can't be answered that way ("what should we eat?").
                if (PollRequestReader.IsOpenQuestion(question))
                {
                    return new UnclearPollAction();
                }

                answers = PollRules.YesNo;
            }

            return new CreatePollAction(question, answers, request.Hours, request.AllowMultiple, FromModel: true);
        }
        // A model that's down or misconfigured (ChatServiceException) isn't caught: the chat handler says what's wrong
        // with the AI connection, rather than asking the person to reword a request that was fine.
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            _logger.LogDebug(ex, "Couldn't work out the poll from a chat message.");
            return new UnclearPollAction();
        }
    }

    private async Task<ChatAction> ReadSongAsync(string message, CancellationToken cancellationToken)
    {
        // "Add X" / "queue X" builds up the queue; "play X" also starts the music if it isn't playing.
        var start = !AddOnlyRegex().IsMatch(message);
        if (YouTubeLinkRegex().Match(message) is { Success: true } link)
        {
            return new PlayMusicAction(link.Value, start);
        }

        try
        {
            using var json = JsonDocument.Parse(
                await _chat.CompleteJsonAsync(PlayPrompt, $"Message: {message}", PlaySchema, cancellationToken));
            var query = json.RootElement.GetProperty("query").GetString()?.Trim();
            if (!string.IsNullOrEmpty(query))
            {
                return new PlayMusicAction(query.Length > 200 ? query[..200] : query, start);
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

    /// <summary>Asking to add or queue a song, rather than play it: "add X", "can you queue up X", "put X in the
    /// queue".</summary>
    [GeneratedRegex(@"^\W*(?:(?:hey|yo|ok|okay|please|pls|can you|could you|would you|will you)\s+)*(?:add|queue)\b|\b(?:to|in|on|into) the queue\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex AddOnlyRegex();
}
