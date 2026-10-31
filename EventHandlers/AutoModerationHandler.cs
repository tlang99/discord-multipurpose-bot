using System.Text.RegularExpressions;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModBot.Configuration;
using ModBot.Services.ContentModeration;
using ModBot.Services.Moderation;
using ModBot.Services.Storage;

namespace ModBot.EventHandlers;

/// <summary>
/// Passive content moderation: scans incoming guild messages for banned/profane text,
/// invite links, and (via Sightengine) explicit/suggestive/offensive images and GIFs.
/// Independent of the slash-command moderation actions. Hook additional checks (spam,
/// mass mentions, etc.) in here as new private methods and call them from HandleMessageAsync.
///
/// Known gap: a pasted Tenor/Giphy link is re-encoded by Discord into a video (mp4) embed,
/// which the image endpoint used here can't read. Only directly-uploaded image/GIF
/// attachments and raw image/GIF URLs are scanned today. Covering embedded GIFs would mean
/// hooking MessageUpdated (embeds populate after the initial send) and adding a video-capable
/// backend call - a reasonable next step, not implemented yet.
/// </summary>
public class AutoModerationHandler
{
    private static readonly Regex InviteLinkRegex = new(
        @"(discord\.gg|discord(?:app)?\.com/invite)/\S+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex DirectImageUrlRegex = new(
        @"https?://\S+\.(?:png|jpe?g|gif|webp)(?:\?\S*)?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private const int MaxImagesPerMessage = 4;

    private readonly IGuildSettingsStore _settingsStore;
    private readonly IModerationService _moderationService;
    private readonly IImageModerationService _imageModerationService;
    private readonly SightengineOptions _sightengineOptions;
    private readonly ILogger<AutoModerationHandler> _logger;

    public AutoModerationHandler(
        IGuildSettingsStore settingsStore,
        IModerationService moderationService,
        IImageModerationService imageModerationService,
        IOptions<SightengineOptions> sightengineOptions,
        ILogger<AutoModerationHandler> logger)
    {
        _settingsStore = settingsStore;
        _moderationService = moderationService;
        _imageModerationService = imageModerationService;
        _sightengineOptions = sightengineOptions.Value;
        _logger = logger;
    }

    public async Task HandleMessageAsync(SocketMessage rawMessage)
    {
        if (rawMessage is not SocketUserMessage message)
        {
            return;
        }

        if (message.Author.IsBot || message.Author.IsWebhook)
        {
            return;
        }

        if (message.Channel is not SocketTextChannel channel)
        {
            return;
        }

        if (message.Author is SocketGuildUser author &&
            author.GuildPermissions.ManageMessages)
        {
            return;
        }

        var settings = await _settingsStore.GetAsync(channel.Guild.Id);
        if (!settings.AutoModEnabled)
        {
            return;
        }

        var violationReason = DetermineTextViolation(message.Content, settings.BannedWords, settings.ProfanityFilterEnabled, settings.BlockInviteLinks);

        if (violationReason is null &&
            settings.ImageModerationEnabled &&
            _imageModerationService.IsConfigured &&
            (settings.ScanNsfwChannels || !channel.IsNsfw))
        {
            violationReason = await DetermineImageViolationAsync(message, settings);
        }

        if (violationReason is null)
        {
            return;
        }

        try
        {
            await message.DeleteAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete message {MessageId} in {GuildId}", message.Id, channel.Guild.Id);
            return;
        }

        await _moderationService.LogActionAsync(
            channel.Guild,
            "Auto-Moderation",
            Color.DarkOrange,
            target: message.Author,
            moderator: null,
            reason: $"{violationReason} in #{channel.Name}");

        try
        {
            var notice = await channel.SendMessageAsync(
                $"{message.Author.Mention} your message was removed: {violationReason}.");
            _ = Task.Delay(TimeSpan.FromSeconds(6)).ContinueWith(_ => notice.DeleteAsync());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send auto-mod notice in {ChannelId}", channel.Id);
        }
    }

    private static string? DetermineTextViolation(
        string content, IReadOnlyCollection<string> bannedWords, bool profanityFilterEnabled, bool blockInvites)
    {
        if (blockInvites && InviteLinkRegex.IsMatch(content))
        {
            return "contained a server invite link";
        }

        var normalizedContent = TextNormalizer.Normalize(content);

        if (bannedWords.Count > 0 && bannedWords.Any(word => ContainsNormalized(normalizedContent, word)))
        {
            return "contained a banned word or phrase";
        }

        if (profanityFilterEnabled && ProfanityWordList.Default.Any(word => ContainsNormalized(normalizedContent, word)))
        {
            return "contained profanity";
        }

        return null;
    }

    private static bool ContainsNormalized(string normalizedContent, string word)
    {
        var normalizedWord = TextNormalizer.Normalize(word);
        return normalizedWord.Length > 0 && normalizedContent.Contains(normalizedWord, StringComparison.Ordinal);
    }

    private async Task<string?> DetermineImageViolationAsync(SocketUserMessage message, Models.GuildSettings settings)
    {
        var candidateUrls = CollectImageUrls(message).Take(MaxImagesPerMessage);

        foreach (var url in candidateUrls)
        {
            var report = await _imageModerationService.CheckImageAsync(url);
            if (!report.Success)
            {
                continue;
            }

            if (settings.BlockExplicitImages && report.ExplicitScore >= _sightengineOptions.ExplicitThreshold)
            {
                return "contained explicit image/GIF content";
            }

            if (settings.BlockSuggestiveImages && report.SuggestiveScore >= _sightengineOptions.SuggestiveThreshold)
            {
                return "contained suggestive image/GIF content";
            }

            if (settings.BlockOffensiveImages && report.OffensiveScore >= _sightengineOptions.OffensiveThreshold)
            {
                return "contained offensive image/GIF content";
            }
        }

        return null;
    }

    private static IEnumerable<string> CollectImageUrls(SocketUserMessage message)
    {
        foreach (var attachment in message.Attachments)
        {
            if (attachment.ContentType is not null &&
                attachment.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                yield return attachment.Url;
            }
        }

        foreach (Match match in DirectImageUrlRegex.Matches(message.Content))
        {
            yield return match.Value;
        }
    }
}
