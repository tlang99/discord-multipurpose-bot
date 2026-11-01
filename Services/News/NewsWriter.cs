using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModBot.Configuration;
using ModBot.Services.Chat;
using ModBot.Services.Moderation;
using ModBot.Services.Storage;

namespace ModBot.Services.News;

/// <summary>The bot's own write-up of an article, and the name it's credited to.</summary>
public record NewsWriteUp(string Text, string WriterName);

/// <summary>
/// Writes a news article up in the chatbot's voice, using the server's personality, for the news channel. The model
/// only sees the headline and the feed's short summary (not the full article), and is told to stick to those facts.
/// Returns null whenever a write-up shouldn't be used (turned off, model unreachable, word filter), so the caller
/// posts the article as it always did.
/// </summary>
/// <remarks>Tested against llama3.2 on real feed items: it kept the facts in nearly every case, but once turned "a
/// multiplayer disadvantage" into "a competitive edge". That's why the news site's own summary is shown alongside by
/// default (<see cref="NewsOptions.ShowOriginalSummary"/>) and the embed says the write-up is AI-written.</remarks>
public partial class NewsWriter
{
    private const int MaxLength = 700;

    private const string Instructions =
        "You're posting a news update in the server's news channel. Rewrite the article below as a short post in " +
        "your own voice: 2 to 4 sentences, under 500 characters. Use ONLY the facts in the headline and summary " +
        "given: don't add any details, numbers, dates, names or claims that aren't there, don't guess what the full " +
        "article says, and keep whether something is good or bad for players exactly as the summary says. If it's a " +
        "review or opinion piece, say it's someone's opinion. Don't repeat the headline word for word, don't include " +
        "links, and never write @everyone or @here. Reply with only the post itself.";

    private readonly IChatService _chat;
    private readonly ChatPersonality _personality;
    private readonly IGuildSettingsStore _settingsStore;
    private readonly NewsOptions _newsOptions;
    private readonly ChatOptions _chatOptions;
    private readonly ILogger<NewsWriter> _logger;

    public NewsWriter(IChatService chat, ChatPersonality personality, IGuildSettingsStore settingsStore,
        IOptions<NewsOptions> newsOptions, IOptions<ChatOptions> chatOptions, ILogger<NewsWriter> logger)
    {
        _chat = chat;
        _personality = personality;
        _settingsStore = settingsStore;
        _newsOptions = newsOptions.Value;
        _chatOptions = chatOptions.Value;
        _logger = logger;
    }

    public bool IsEnabled => _newsOptions.AiWriteUps && _chatOptions.IsEnabled;

    /// <param name="botName">The bot's name in that server, for the prompt and the credit.</param>
    public async Task<NewsWriteUp?> WriteAsync(NewsArticle article, ulong guildId, string botName, string serverName,
        CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
        {
            return null;
        }

        var systemPrompt = $"You are {botName}, the bot in the Discord server \"{serverName}\".\n\n" +
                           $"Your personality: {await _personality.GetAsync(guildId)}\n\n" +
                           Instructions;
        var articleText = $"Headline: {article.Title}\n" +
                          $"Summary from the news site: {article.Summary ?? "(none - the headline is all there is)"}";

        string text;
        try
        {
            text = await _chat.ReplyAsync(systemPrompt, [new ChatTurn(ChatRole.User, articleText)], cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Anything at all: a write-up is optional, and a failure here mustn't stop the article being posted.
            _logger.LogWarning(ex, "Couldn't write up '{Title}' for guild {GuildId}; posting the original summary.",
                article.Title, guildId);
            return null;
        }

        text = Clean(text);
        if (text.Length < 20)
        {
            return null;
        }

        // The feed's text is untrusted and goes into the prompt, so a hostile feed could try to get a link or a mass
        // mention into the news channel. The prompt forbids both; this makes sure.
        if (LinkOrMassMentionRegex().IsMatch(text))
        {
            _logger.LogInformation("Write-up of '{Title}' contained a link or mass mention in guild {GuildId}; posting " +
                                   "the original summary instead.", article.Title, guildId);
            return null;
        }

        // The model's words, posted publicly, so they follow the server's word rules like the chatbot's replies.
        var settings = await _settingsStore.GetAsync(guildId);
        if (settings.AutoModEnabled && TextFilter.FindViolation(text, settings) is not null)
        {
            _logger.LogInformation("Write-up of '{Title}' broke the word filter in guild {GuildId}; posting the original " +
                                   "summary instead.", article.Title, guildId);
            return null;
        }

        return new NewsWriteUp(text, botName);
    }

    /// <summary>Trims stray wrapping quotes (the model sometimes adds them) and keeps it to a sensible length, cutting
    /// at the end of a sentence where possible.</summary>
    private static string Clean(string text)
    {
        text = text.Trim();
        if (text.Length >= 2 && text[0] is '"' or '“' && text[^1] is '"' or '”')
        {
            text = text[1..^1].Trim();
        }
        else if (text.Length >= 1 && text[0] is '"' or '“' && text.IndexOfAny(['"', '“', '”'], 1) < 0)
        {
            // An opening quote that's never closed.
            text = text[1..].Trim();
        }

        if (text.Length <= MaxLength)
        {
            return text;
        }

        var cut = text[..MaxLength];
        var lastSentence = cut.LastIndexOfAny(['.', '!', '?']);
        return lastSentence > MaxLength / 2 ? cut[..(lastSentence + 1)] : cut.TrimEnd() + "…";
    }

    /// <summary>A web address (with or without http, "www." or a bare domain like evil.com/path), a Discord invite,
    /// or @everyone / @here.</summary>
    [GeneratedRegex(@"https?://|\bwww\.|\b[a-z0-9-]+\.(?:com|net|org|gg|io|co|ru|xyz|ly|me|tv|info|link|click|top)(?:/|\b)|discord(?:app)?\.com/invite|@everyone|@here",
        RegexOptions.IgnoreCase)]
    private static partial Regex LinkOrMassMentionRegex();
}
