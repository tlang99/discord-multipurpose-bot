using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using ModBot.Common;
using ModBot.Models;
using ModBot.Services.Moderation;
using ModBot.Services.Search;
using ModBot.Services.Settings;
using ModBot.Services.Storage;

namespace ModBot.Modules.Search;

/// <summary>/search: looks something up on the web and answers in the channel, like a news post. The same
/// as asking the chatbot ("@bot search for …", or "hey ModBot, search for …" in voice); the work is
/// <see cref="WebSearchAnswerer"/>'s.</summary>
[RequireContext(ContextType.Guild)]
public class SearchModule : InteractionModuleBase<SocketInteractionContext>
{
    private readonly WebSearchAnswerer _search;
    private readonly IGuildSettingsStore _moderation;

    public SearchModule(WebSearchAnswerer search, IGuildSettingsStore moderation)
    {
        _search = search;
        _moderation = moderation;
    }

    [RequireFeature(ServerFeature.WebSearch)]
    [SlashCommand("search", "Looks something up on the web and answers here, with a link to the source.")]
    public async Task SearchAsync(
        [Summary("question", "What to look up")] [MaxLength(SearchRequestReader.MaxQueryLength)] string question)
    {
        if (Context.User is not SocketGuildUser user)
        {
            return;
        }

        // Everything that can refuse is checked first, so a refusal is private (once the reply is deferred publicly, it
        // can't be made private any more).
        var settings = await _moderation.GetAsync(Context.Guild.Id);
        if (TextFilter.AppliesTo(settings, user) && TextFilter.FindViolation(question, settings) is not null)
        {
            await RespondAsync("That search breaks this server's word filter.", ephemeral: true);
            return;
        }

        if (_search.WhyCantSearch(Context.Guild.Id, user.Id) is { } reason)
        {
            await RespondAsync(reason, ephemeral: true);
            return;
        }

        // Searching and writing the answer take a few seconds, longer than Discord waits for a first reply.
        await DeferAsync();
        var found = await _search.AnswerAsync(Context.Guild.Id, Context.Guild.Name, Context.Guild.CurrentUser.DisplayName,
            user.Id, question);

        // Web pages, and the model's answer from them, are third-party text: the server's word rules apply.
        if (TextFilter.FindViolation(found.Text + "\n" + MessageTools.EmbedText(found.Embed), settings) is not null)
        {
            await FollowupAsync("I found an answer, but it broke this server's word filter, so I'm keeping it to myself.",
                allowedMentions: AllowedMentions.None);
            return;
        }

        // An answer is all in the embed, like a news post (Discord already shows who used /search above it).
        await FollowupAsync(found.Text.Length > 0 ? found.Text : null, embed: found.Embed,
            allowedMentions: AllowedMentions.None);
    }
}
