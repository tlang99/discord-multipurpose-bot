using Discord;
using Discord.Interactions;
using ModBot.Services.ContentModeration;
using ModBot.Services.Moderation;
using ModBot.Services.Storage;

namespace ModBot.Modules.Moderation;

[Group("modlog", "Configure where moderation actions get logged.")]
[RequireContext(ContextType.Guild)]
[RequireUserPermission(GuildPermission.ManageGuild)]
public class ModLogModule : InteractionModuleBase<SocketInteractionContext>
{
    private readonly IGuildSettingsStore _settingsStore;

    public ModLogModule(IGuildSettingsStore settingsStore)
    {
        _settingsStore = settingsStore;
    }

    [SlashCommand("set", "Sets the channel moderation actions are logged to.")]
    public async Task SetAsync(ITextChannel channel)
    {
        await _settingsStore.UpdateAsync(Context.Guild.Id, s => s.ModLogChannelId = channel.Id);
        await RespondAsync($"Moderation actions will now be logged to {channel.Mention}.");
    }

    [SlashCommand("disable", "Stops logging moderation actions.")]
    public async Task DisableAsync()
    {
        await _settingsStore.UpdateAsync(Context.Guild.Id, s => s.ModLogChannelId = null);
        await RespondAsync("Moderation logging has been disabled.");
    }

    [SlashCommand("show", "Shows the current moderation log channel.")]
    public async Task ShowAsync()
    {
        var settings = await _settingsStore.GetAsync(Context.Guild.Id);
        await RespondAsync(settings.ModLogChannelId is ulong id
            ? $"Moderation actions are logged to <#{id}>."
            : "Moderation logging is currently disabled.");
    }
}

[Group("automod", "Configure automatic content moderation.")]
[RequireContext(ContextType.Guild)]
[RequireUserPermission(GuildPermission.ManageGuild)]
public class AutoModModule : InteractionModuleBase<SocketInteractionContext>
{
    private readonly IGuildSettingsStore _settingsStore;
    private readonly IImageModerationService _imageModerationService;

    public AutoModModule(IGuildSettingsStore settingsStore, IImageModerationService imageModerationService)
    {
        _settingsStore = settingsStore;
        _imageModerationService = imageModerationService;
    }

    [SlashCommand("enable", "Turns automatic content moderation on or off.")]
    public async Task EnableAsync(bool enabled)
    {
        await _settingsStore.UpdateAsync(Context.Guild.Id, s => s.AutoModEnabled = enabled);
        await RespondAsync($"Auto-moderation is now **{(enabled ? "enabled" : "disabled")}**.");
    }

    [SlashCommand("block-invites", "Turns automatic deletion of Discord invite links on or off.")]
    public async Task BlockInvitesAsync(bool enabled)
    {
        await _settingsStore.UpdateAsync(Context.Guild.Id, s => s.BlockInviteLinks = enabled);
        await RespondAsync($"Blocking invite links is now **{(enabled ? "enabled" : "disabled")}**.");
    }

    [SlashCommand("profanity-filter", "Turns the built-in profanity word filter on or off.")]
    public async Task ProfanityFilterAsync(bool enabled)
    {
        await _settingsStore.UpdateAsync(Context.Guild.Id, s => s.ProfanityFilterEnabled = enabled);
        await RespondAsync($"The built-in profanity filter is now **{(enabled ? "enabled" : "disabled")}**.");
    }

    [SlashCommand("image-moderation", "Turns automatic scanning of images/GIFs for explicit, suggestive, or offensive content on or off.")]
    public async Task ImageModerationAsync(bool enabled)
    {
        await _settingsStore.UpdateAsync(Context.Guild.Id, s => s.ImageModerationEnabled = enabled);

        var warning = enabled && !_imageModerationService.IsConfigured
            ? "\n⚠️ No image moderation provider is configured yet (no Sightengine API credentials), so this will have no effect until one is set up."
            : string.Empty;

        await RespondAsync($"Image/GIF content scanning is now **{(enabled ? "enabled" : "disabled")}**.{warning}");
    }

    [SlashCommand("block-explicit-images", "Turns automatic removal of explicit images/GIFs on or off.")]
    public async Task BlockExplicitImagesAsync(bool enabled)
    {
        await _settingsStore.UpdateAsync(Context.Guild.Id, s => s.BlockExplicitImages = enabled);
        await RespondAsync($"Blocking explicit images/GIFs is now **{(enabled ? "enabled" : "disabled")}**.");
    }

    [SlashCommand("block-suggestive-images", "Turns automatic removal of suggestive (but not explicit) images/GIFs on or off.")]
    public async Task BlockSuggestiveImagesAsync(bool enabled)
    {
        await _settingsStore.UpdateAsync(Context.Guild.Id, s => s.BlockSuggestiveImages = enabled);
        await RespondAsync($"Blocking suggestive images/GIFs is now **{(enabled ? "enabled" : "disabled")}**.");
    }

    [SlashCommand("block-offensive-images", "Turns automatic removal of offensive images/GIFs (hate symbols, obscene gestures) on or off.")]
    public async Task BlockOffensiveImagesAsync(bool enabled)
    {
        await _settingsStore.UpdateAsync(Context.Guild.Id, s => s.BlockOffensiveImages = enabled);
        await RespondAsync($"Blocking offensive images/GIFs is now **{(enabled ? "enabled" : "disabled")}**.");
    }

    [SlashCommand("scan-nsfw-channels", "Controls whether channels marked NSFW in Discord are still scanned for images/GIFs.")]
    public async Task ScanNsfwChannelsAsync(bool enabled)
    {
        await _settingsStore.UpdateAsync(Context.Guild.Id, s => s.ScanNsfwChannels = enabled);
        await RespondAsync(enabled
            ? "Channels marked NSFW will now also be scanned for images/GIFs."
            : "Channels marked NSFW will be skipped by image/GIF scanning (default).");
    }

    [SlashCommand("word-add", "Adds a word or phrase to the banned word list.")]
    public async Task WordAddAsync(string word)
    {
        var normalized = word.Trim().ToLowerInvariant();

        if (TextNormalizer.Normalize(normalized).Length == 0)
        {
            await RespondAsync("That word has no letters or digits in it, so it can't be matched.", ephemeral: true);
            return;
        }

        var added = false;
        await _settingsStore.UpdateAsync(Context.Guild.Id, s =>
        {
            if (!s.BannedWords.Contains(normalized))
            {
                s.BannedWords.Add(normalized);
                added = true;
            }
        });

        await RespondAsync(added ? $"Added `{normalized}` to the banned word list." : "That word is already banned.",
            ephemeral: true);
    }

    [SlashCommand("word-remove", "Removes a word or phrase from the banned word list.")]
    public async Task WordRemoveAsync(string word)
    {
        var normalized = word.Trim().ToLowerInvariant();

        var removed = false;
        await _settingsStore.UpdateAsync(Context.Guild.Id, s => removed = s.BannedWords.Remove(normalized));

        await RespondAsync(removed ? $"Removed `{normalized}` from the banned word list." : "That word wasn't on the list.",
            ephemeral: true);
    }

    [SlashCommand("word-list", "Lists all currently banned words and phrases.")]
    public async Task WordListAsync()
    {
        var settings = await _settingsStore.GetAsync(Context.Guild.Id);

        if (settings.BannedWords.Count == 0)
        {
            await RespondAsync("No banned words are configured.", ephemeral: true);
            return;
        }

        await RespondAsync($"Banned words: `{string.Join("`, `", settings.BannedWords)}`", ephemeral: true);
    }
}
