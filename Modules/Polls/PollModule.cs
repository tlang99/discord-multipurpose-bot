using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using ModBot.Models;
using ModBot.Services.Polls;
using ModBot.Services.Settings;

namespace ModBot.Modules.Polls;

/// <summary>/poll posts one of Discord's native polls in the channel; right-click → Apps → End poll ends one early.
/// The rules live in <see cref="PollPoster"/>, shared with the chatbot.</summary>
[RequireContext(ContextType.Guild)]
public class PollModule : InteractionModuleBase<SocketInteractionContext>
{
    private readonly PollPoster _poster;
    private readonly ILogger<PollModule> _logger;

    public PollModule(PollPoster poster, ILogger<PollModule> logger)
    {
        _poster = poster;
        _logger = logger;
    }

    [RequireFeature(ServerFeature.Polls)]
    [SlashCommand("poll", "Posts a poll in this channel. Leave out the answers for a Yes/No poll.")]
    public async Task PollAsync(
        [Summary("question", "What to ask")] [MaxLength(PollRules.MaxQuestionLength)] string question,
        [Summary("answers", "Up to 10, separated by commas (or by | if an answer has a comma). Empty: Yes/No")]
        [MaxLength(PollRules.MaxAnswers * (PollRules.MaxAnswerLength + 3))] string? answers = null,
        [Summary("hours", "How long it runs (default 24, up to 768 = 32 days)")] [MinValue(1)] [MaxValue(PollRules.MaxHours)]
        int? hours = null,
        [Summary("multiple", "Let people pick more than one answer")] bool multiple = false)
    {
        if (Context.Channel is not ITextChannel channel || Context.User is not SocketGuildUser member)
        {
            await RespondAsync("Polls only work in a server text channel.", ephemeral: true);
            return;
        }

        var check = PollRules.Check(question,
            string.IsNullOrWhiteSpace(answers) ? PollRules.YesNo : PollRules.SplitAnswers(answers),
            hours ?? _poster.DefaultHours, multiple);
        if (check.Draft is not { } draft)
        {
            await RespondAsync(check.Problem, ephemeral: true);
            return;
        }

        if (await _poster.PrepareAsync(member, channel, draft) is { } problem)
        {
            await RespondAsync(problem, ephemeral: true, allowedMentions: AllowedMentions.None);
            return;
        }

        try
        {
            await RespondAsync(PollPoster.Header(member.Id), poll: PollPoster.ToProperties(draft),
                allowedMentions: AllowedMentions.None);
        }
        catch (Exception ex)
        {
            _poster.Release(Context.Guild.Id, member.Id);
            _logger.LogWarning(ex, "Couldn't post a poll in channel {ChannelId}.", channel.Id);
            throw;
        }
    }

    [MessageCommand("End poll")]
    public async Task EndPollAsync(IMessage message)
    {
        if (message is not IUserMessage userMessage || Context.User is not SocketGuildUser member)
        {
            await RespondAsync("That isn't one of my polls.", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);
        await FollowupAsync(await _poster.EndAsync(member, userMessage), ephemeral: true,
            allowedMentions: AllowedMentions.None);
    }
}
