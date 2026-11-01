using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using ModBot.Models;

namespace ModBot.Services.Chat;

/// <summary>Something a chat message asked the bot to do.</summary>
public abstract record ChatAction;

public record PlayMusicAction(string Query) : ChatAction;

/// <param name="Players">Total squad size including the person asking, 2-6.</param>
public record CreateLfgAction(int Players, LfgDetails Details) : ChatAction;

/// <summary>The message asked for music, but which song couldn't be worked out.</summary>
public record UnclearSongAction : ChatAction;

/// <summary>
/// Works out whether a chat message is asking the bot to play music or post an LFG, and with what details.
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
/// </list>
/// Only ever sees the asker's own message, never the channel history, so nothing someone else wrote earlier can make
/// the bot act. No Discord types: running the action is the caller's job.
/// </remarks>
public partial class ChatActionParser
{
    private const string ClassifySchema =
        """{"type":"object","properties":{"action":{"type":"string","enum":["none","play_music","create_lfg"]}},"required":["action"]}""";

    private const string ClassifyPrompt =
        "You decide whether a Discord message is a direct request for the bot to DO something. Answer play_music only " +
        "if the person explicitly asks the bot to play, queue or put on a song or music. Answer create_lfg only if " +
        "they explicitly ask the bot to make, post or create an LFG or looking-for-group post, or to find them players " +
        "or a squad. Questions about music or LFG, opinions, and general chat are none. When unsure, answer none.";

    private const string PlaySchema =
        """{"type":"object","properties":{"query":{"type":"string"}},"required":["query"]}""";

    private const string PlayPrompt =
        "The message asks a Discord bot to play music. Give the YouTube search for what they want in \"query\": the " +
        "song and artist if they named them, or their own words for a genre or mood. Don't pick a specific song they " +
        "didn't ask for.";

    private readonly IChatService _chat;
    private readonly ILogger<ChatActionParser> _logger;

    public ChatActionParser(IChatService chat, ILogger<ChatActionParser> logger)
    {
        _chat = chat;
        _logger = logger;
    }

    /// <summary>The action <paramref name="message"/> asks for, or null if it's just chat (or the model couldn't be
    /// reached, so the caller falls back to chatting).</summary>
    public async Task<ChatAction?> ParseAsync(string message, CancellationToken cancellationToken = default)
    {
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

            default:
                return null;
        }
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
