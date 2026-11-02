using System.Collections.Concurrent;
using System.Text;
using Discord;
using Discord.Net;
using Discord.WebSocket;
using Lavalink4NET.Players;
using Lavalink4NET.Players.Queued;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModBot.Configuration;

namespace ModBot.Modules.Music;

/// <summary>
/// Music sessions (see <see cref="MusicSession"/>): one per server at a time, run from a menu message with buttons, the
/// /session commands, or by asking the chatbot. The rules live here so all three behave the same.
/// </summary>
public class MusicSessionService
{
    public const string ButtonPrefix = "msession";
    public const string AddButton = "add";
    public const string StartButton = "start";
    public const string SkipButton = "skip";
    public const string LeaveButton = "leave";
    public const string EndButton = "end";

    private readonly ConcurrentDictionary<ulong, MusicSession> _sessions = new();
    private readonly MusicService _music;
    private readonly DiscordSocketClient _client;
    private readonly MusicOptions _options;
    private readonly ILogger<MusicSessionService> _logger;

    public MusicSessionService(MusicService music, MusicPlayerEvents events, DiscordSocketClient client,
        IOptions<MusicOptions> options, ILogger<MusicSessionService> logger)
    {
        _music = music;
        _client = client;
        _options = options.Value;
        _logger = logger;

        events.TrackStarted = OnTrackStartedAsync;
        events.PlayerClosed = OnPlayerClosedAsync;
    }

    private int SongsPerPerson => Math.Max(1, _options.SessionSongsPerPerson);

    private TimeSpan LobbyIdleLimit => TimeSpan.FromMinutes(Math.Max(5, _options.SessionLobbyIdleMinutes));

    /// <summary>More than half the people listening.</summary>
    public static int VotesNeeded(int listeners) => Math.Max(1, listeners) / 2 + 1;

    /// <summary>The server's session, if one is running. A session that was never started and has sat untouched
    /// past the idle limit is closed here rather than by a timer.</summary>
    public MusicSession? Get(ulong guildId)
    {
        if (!_sessions.TryGetValue(guildId, out var session))
        {
            return null;
        }

        bool stale;
        lock (session.Sync)
        {
            stale = !session.Started && DateTimeOffset.UtcNow - session.LastActivity > LobbyIdleLimit;
        }

        if (stale)
        {
            Close(session, "This session closed because nobody used it for a while.");
            return null;
        }

        return session;
    }

    /// <summary>The session a menu button belongs to, or null if that session is over.</summary>
    public MusicSession? GetForButton(ulong guildId, string sessionId)
        => Get(guildId) is { } session && session.Id == sessionId ? session : null;

    /// <summary>Opens a session and posts its menu in <paramref name="channel"/>.</summary>
    public async Task<MusicReply> OpenAsync(SocketGuildUser user, ITextChannel channel)
    {
        if (Get(user.Guild.Id) is { } existing)
        {
            return MusicReply.Fail($"There's already a music session going{MenuLinkText(existing)}. Add songs to " +
                                   "that one, or ask the host to end it first.");
        }

        if (_music.GetPlayer(user.Guild.Id) is { } player && (player.CurrentItem is not null || player.Queue.Count > 0))
        {
            return MusicReply.Fail("Music is already playing. Let it finish or `/stop` it, then open a session so " +
                                   "everyone's songs take turns from the start.");
        }

        var session = new MusicSession(user.Guild.Id, user.Id, channel, DateTimeOffset.UtcNow);
        if (!_sessions.TryAdd(user.Guild.Id, session))
        {
            return MusicReply.Fail("Someone else just opened a music session - add your songs to theirs.");
        }

        try
        {
            session.Menu = await channel.SendMessageAsync(embed: BuildMenu(session, null),
                components: BuildButtons(session), allowedMentions: AllowedMentions.None);
        }
        catch (Exception ex)
        {
            _sessions.TryRemove(new KeyValuePair<ulong, MusicSession>(user.Guild.Id, session));
            _logger.LogWarning(ex, "Couldn't post a music session menu in channel {ChannelId}.", channel.Id);
            return MusicReply.Fail($"I couldn't post the session menu in {channel.Mention} - check I can send " +
                                   "messages there.");
        }

        // Pinned, so it's one click away however far the "Now playing" posts push it up.
        var pinned = await TryPinAsync(session.Menu);
        return MusicReply.Ok($"🎶 {user.Mention} opened a music session{MenuLinkText(session)}" +
                             (pinned ? " (pinned)" : "") + "! Everyone add songs " +
                             "(the **Add song** button, `/play`, or just ask me), and when you're ready, " +
                             $"{user.Mention} presses **Start**. Songs take turns: one from each person, round and round.");
    }

    /// <summary>Adds songs to the session for <paramref name="user"/>: to the waiting list before it starts, or
    /// straight into the turn order once it has.</summary>
    public async Task<MusicReply> AddAsync(MusicSession session, SocketGuildUser user, string query)
    {
        if (MusicService.CheckQuery(query) is { } badQuery)
        {
            return MusicReply.Fail(badQuery);
        }

        bool started;
        lock (session.Sync)
        {
            started = session.Started;
        }

        // Before it starts anyone can add from anywhere; after, adding means being there to hear it, as with /play.
        var player = started ? _music.GetPlayer(session.GuildId) : null;
        if (started && player is not null && user.VoiceChannel?.Id != player.VoiceChannelId)
        {
            return MusicReply.Fail($"Join <#{player.VoiceChannelId}> to add songs to the session.");
        }

        var lookup = await _music.FindTracksAsync(query, session.GuildId);
        if (lookup.Error is not null)
        {
            return MusicReply.Fail(lookup.Error);
        }

        List<SessionTrackItem> added;
        int firstPosition;
        var queueLock = await _music.AcquireQueueLockAsync(session.GuildId);
        try
        {
            player = _music.GetPlayer(session.GuildId);
            lock (session.Sync)
            {
                if (session.Ended)
                {
                    return MusicReply.Fail("That session just ended.");
                }

                started = session.Started;
                if (started && player is null)
                {
                    // The music stopped a moment ago and the session is about to close with it.
                    return MusicReply.Fail("The music just stopped, so the session's over.");
                }

                var waiting = Waiting(session, player);
                var mine = waiting.Count(s => s.PickerId == user.Id);
                var room = Math.Min(SongsPerPerson - mine, _music.MaxQueueLength - waiting.Count);
                if (room <= 0)
                {
                    return MusicReply.Fail(mine >= SongsPerPerson
                        ? $"You've got {mine} songs waiting, the most each person can have. Add more once some play."
                        : $"The queue is full ({_music.MaxQueueLength} songs).");
                }

                added = lookup.Tracks.Take(room)
                    .Select(t => new SessionTrackItem(t, user.Id, user.DisplayName))
                    .ToList();
                if (!session.Participants.Contains(user.Id))
                {
                    session.Participants.Add(user.Id);
                }

                session.LastActivity = DateTimeOffset.UtcNow;
                if (!started)
                {
                    session.Lobby.AddRange(added);
                }
            }

            if (started && player is not null)
            {
                await player.Queue.AddRangeAsync(added.Cast<ITrackQueueItem>().ToList());
                await ReorderAsync(session, player);

                // The queue had run dry: start the next turn now rather than waiting for a song that isn't coming.
                if (player.CurrentItem is null && player.Queue.TryPeek(out var next) && next is not null)
                {
                    await player.Queue.RemoveAtAsync(0);
                    await player.PlayAsync(next);
                }
            }

            firstPosition = PositionOf(session, player, added[0].EntryId);
        }
        finally
        {
            queueLock.Release();
        }

        RefreshMenu(session);

        var what = lookup.IsPlaylist
            ? $"**{added.Count}** songs from **{Format.Sanitize(lookup.PlaylistName!)}**" +
              (added.Count < lookup.Tracks.Count ? $" (the most you can have waiting is {SongsPerPerson})" : "")
            : $"{MusicFormatting.TrackLink(added[0].Track)} ({MusicFormatting.Length(added[0].Track)})";
        var when = firstPosition switch
        {
            0 => " - it's playing now.",
            > 0 => $" - {(added.Count > 1 ? "the first one is" : "it's")} **#{firstPosition}** in the turn order.",
            _ => ".",
        };
        return MusicReply.Ok($"➕ {user.Mention} added {what} to the session{when}");
    }

    /// <summary>Starts playback: the bot joins <paramref name="user"/>'s voice channel and the turns begin.</summary>
    public async Task<MusicReply> StartAsync(MusicSession session, SocketGuildUser user)
    {
        lock (session.Sync)
        {
            if (session.Started)
            {
                return MusicReply.Fail("The session's already playing.");
            }

            if (user.Id != session.HostId && !MusicPermissions.IsModerator(user))
            {
                return MusicReply.Fail($"Only the host (<@{session.HostId}>) can start the session.");
            }

            if (session.Lobby.Count == 0)
            {
                return MusicReply.Fail("Add some songs first - there's nothing to play yet.");
            }
        }

        if (_music.GetJoinProblem(user) is { } problem)
        {
            return MusicReply.Fail(problem);
        }

        var (player, joinError) = await _music.JoinPlayerAsync(user, session.Channel);
        if (player is null)
        {
            return MusicReply.Fail(joinError);
        }

        var queueLock = await _music.AcquireQueueLockAsync(session.GuildId);
        try
        {
            List<SessionTrackItem> order;
            lock (session.Sync)
            {
                if (session.Started || session.Ended)
                {
                    return MusicReply.Fail("The session's already playing.");
                }

                // Everyone may have left (taking their songs) while the bot was joining voice.
                if (session.Lobby.Count == 0)
                {
                    return MusicReply.Fail("Add some songs first - there's nothing to play yet.");
                }

                order = FairOrder.Arrange(session.Lobby, s => s.PickerId, session.Participants, null);
                session.Lobby.Clear();
                session.Started = true;
                session.LastActivity = DateTimeOffset.UtcNow;
            }

            await player.PlayAsync(order[0]);
            if (order.Count > 1)
            {
                await player.Queue.AddRangeAsync(order.Skip(1).Cast<ITrackQueueItem>().ToList());
            }
        }
        finally
        {
            queueLock.Release();
        }

        RefreshMenu(session);
        return MusicReply.Ok($"▶️ {user.Mention} started the session in <#{player.VoiceChannelId}>. Taking turns from " +
                             "here - vote to skip anything you don't want to hear.");
    }

    /// <summary>A vote to skip the current song. The person who picked it, and moderators, skip it outright.</summary>
    public async Task<MusicReply> VoteSkipAsync(MusicSession session, SocketGuildUser user)
    {
        var player = _music.GetPlayer(session.GuildId);
        if (player?.CurrentItem is not { } current)
        {
            return MusicReply.Fail("Nothing's playing to skip.");
        }

        var moderator = MusicPermissions.IsModerator(user);
        if (user.VoiceChannel?.Id != player.VoiceChannelId && !moderator)
        {
            return MusicReply.Fail($"Join <#{player.VoiceChannelId}> to vote - only people listening get a say.");
        }

        var pick = current as SessionTrackItem;
        var listeners = user.Guild.GetVoiceChannel(player.VoiceChannelId)?.ConnectedUsers.Count(u => !u.IsBot) ?? 1;
        var needed = VotesNeeded(listeners);
        int votes;
        string reason;
        lock (session.Sync)
        {
            if (pick?.EntryId != session.VoteEntryId)
            {
                session.VoteEntryId = pick?.EntryId;
                session.SkipVotes.Clear();
            }

            if (pick?.PickerId == user.Id)
            {
                reason = $"{user.Mention} skipped their own pick";
                votes = needed;
            }
            else if (moderator)
            {
                reason = $"{user.Mention} (a moderator) skipped it";
                votes = needed;
            }
            else
            {
                if (!session.SkipVotes.Add(user.Id))
                {
                    return MusicReply.Fail($"You've already voted to skip this one ({session.SkipVotes.Count}/{needed}).");
                }

                votes = session.SkipVotes.Count;
                reason = $"{votes}/{needed} votes";
            }

            session.LastActivity = DateTimeOffset.UtcNow;
        }

        if (votes < needed)
        {
            RefreshMenu(session);
            return MusicReply.Ok($"🗳️ {user.Mention} voted to skip {MusicFormatting.TrackLink(current.Track)} " +
                                 $"(**{votes}/{needed}** - more than half the listeners skips it).") with { Announce = false };
        }

        var queueLock = await _music.AcquireQueueLockAsync(session.GuildId);
        try
        {
            // Only skip the song that was voted on: if it ended while votes came in, the next one is innocent.
            if ((player.CurrentItem as SessionTrackItem)?.EntryId != pick?.EntryId || player.CurrentItem is null)
            {
                return MusicReply.Fail("That song already finished.");
            }

            await player.SkipAsync();
        }
        finally
        {
            queueLock.Release();
        }

        var next = player.CurrentItem is { } now ? $" Up now: {MusicFormatting.TrackLink(now.Track)}." : " That's the last of the songs.";
        return MusicReply.Ok($"⏭️ Skipped {MusicFormatting.TrackLink(current.Track)} ({reason}).{next}");
    }

    /// <summary>Takes <paramref name="user"/> out of the session along with their waiting songs. If the host leaves,
    /// the next person takes over; if nobody's left, the session ends.</summary>
    public async Task<MusicReply> LeaveAsync(MusicSession session, SocketGuildUser user)
    {
        int removed;
        bool endIt;
        ulong? newHost = null;
        var queueLock = await _music.AcquireQueueLockAsync(session.GuildId);
        try
        {
            var player = _music.GetPlayer(session.GuildId);
            lock (session.Sync)
            {
                if (!session.Participants.Contains(user.Id) && session.HostId != user.Id)
                {
                    return MusicReply.Fail("You're not in this session - add a song to join it.");
                }

                session.Participants.Remove(user.Id);
                removed = session.Lobby.RemoveAll(s => s.PickerId == user.Id);
                if (session.HostId == user.Id)
                {
                    newHost = session.Participants.Count > 0 ? session.Participants[0] : null;
                    if (newHost is { } host)
                    {
                        session.HostId = host;
                    }
                }

                endIt = session.HostId == user.Id;
                session.LastActivity = DateTimeOffset.UtcNow;
            }

            if (player is not null)
            {
                for (var i = player.Queue.Count - 1; i >= 0; i--)
                {
                    if (player.Queue[i] is SessionTrackItem pick && pick.PickerId == user.Id)
                    {
                        await player.Queue.RemoveAtAsync(i);
                        removed++;
                    }
                }
            }
        }
        finally
        {
            queueLock.Release();
        }

        var songs = removed == 0 ? "" : $" and took out their {removed} waiting song{(removed == 1 ? "" : "s")}";
        if (endIt)
        {
            await EndCoreAsync(session, "The host left and nobody else was in it, so the session's over.");
            return MusicReply.Ok($"👋 {user.Mention} left the session{songs}. Nobody else was in it, so it's over.");
        }

        RefreshMenu(session);
        return MusicReply.Ok($"👋 {user.Mention} left the session{songs}." +
                             (newHost is { } h ? $" <@{h}> is the host now." : ""));
    }

    /// <summary>Ends the session (host or moderator): the music stops and the bot leaves voice.</summary>
    public async Task<MusicReply> EndAsync(MusicSession session, SocketGuildUser user)
    {
        ulong host;
        lock (session.Sync)
        {
            host = session.HostId;
        }

        if (user.Id != host && !MusicPermissions.IsModerator(user))
        {
            return MusicReply.Fail($"Only the host (<@{host}>) or a moderator can end the session. You can leave it " +
                                   "instead, which takes out your songs.");
        }

        await EndCoreAsync(session, $"Ended by {user.Mention}.");
        return MusicReply.Ok($"⏹️ {user.Mention} ended the music session.");
    }

    /// <summary>Posts the menu again at the bottom of the channel (the old one is removed).</summary>
    public async Task<MusicReply> ShowMenuAsync(MusicSession session)
    {
        await session.MenuLock.WaitAsync();
        try
        {
            var old = session.Menu;
            session.Menu = await session.Channel.SendMessageAsync(embed: await BuildMenuAsync(session),
                components: BuildButtons(session), allowedMentions: AllowedMentions.None);
            await TryPinAsync(session.Menu);
            if (old is not null)
            {
                // Deleting it unpins it too. If it can't be deleted, at least don't leave two menus pinned.
                try
                {
                    await old.DeleteAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Couldn't delete an old music session menu.");
                    await TryUnpinAsync(old);
                }
            }
        }
        finally
        {
            session.MenuLock.Release();
        }

        return MusicReply.Ok($"🎶 Here's the session menu{MenuLinkText(session)}.");
    }

    /// <summary>Puts the queue in turn order again after it changed.</summary>
    /// <remarks>Call with the queue lock held.</remarks>
    public async Task ReorderAsync(MusicSession session, MusicPlayer player)
    {
        List<ITrackQueueItem> order;
        lock (session.Sync)
        {
            order = FairOrder.Arrange(player.Queue.ToList(), PickerOf, session.Participants, session.CurrentPickerId);
        }

        await player.Queue.ClearAsync();
        await player.Queue.AddRangeAsync(order);
    }

    /// <summary>Who picked a queued song (0 for one queued outside a session).</summary>
    public static ulong PickerOf(ITrackQueueItem item) => item is SessionTrackItem pick ? pick.PickerId : 0;

    /// <summary>Redraws the menu in the background, so replies don't wait on it.</summary>
    public void RefreshMenu(MusicSession session) => _ = Task.Run(async () =>
    {
        try
        {
            await RefreshMenuAsync(session);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Couldn't update the music session menu in guild {GuildId}.", session.GuildId);
        }
    });

    private async Task RefreshMenuAsync(MusicSession session, string? endedNote = null)
    {
        await session.MenuLock.WaitAsync();
        try
        {
            if (session.Menu is not { } menu)
            {
                return;
            }

            var embed = endedNote is null ? await BuildMenuAsync(session) : BuildEndedMenu(endedNote);
            var buttons = endedNote is null ? BuildButtons(session) : new ComponentBuilder().Build();
            await menu.ModifyAsync(m =>
            {
                m.Embed = embed;
                m.Components = buttons;
            });

            // An old session's menu has nothing left to do, and pins are limited (50 a channel).
            if (endedNote is not null)
            {
                await TryUnpinAsync(menu);
            }
        }
        finally
        {
            session.MenuLock.Release();
        }
    }

    /// <summary>Pins the menu; false if the bot can't (it needs Pin Messages), which is fine: the menu works anyway.</summary>
    private async Task<bool> TryPinAsync(IUserMessage menu)
    {
        try
        {
            await menu.PinAsync();
            return true;
        }
        catch (HttpException ex)
        {
            _logger.LogInformation(ex, "Couldn't pin the music session menu in channel {ChannelId}.", menu.Channel.Id);
            return false;
        }
    }

    /// <summary>Unpins the menu. Tried even if pinning failed: the message object is a snapshot from when it was sent,
    /// so it can't say whether it's pinned now (and a moderator may have pinned it by hand).</summary>
    private async Task TryUnpinAsync(IUserMessage menu)
    {
        try
        {
            await menu.UnpinAsync();
        }
        catch (HttpException ex)
        {
            _logger.LogDebug(ex, "Couldn't unpin an old music session menu in channel {ChannelId}.", menu.Channel.Id);
        }
    }

    private async Task<Embed> BuildMenuAsync(MusicSession session)
    {
        // A snapshot under the queue lock, so the menu never shows a half-reordered queue.
        var queueLock = await _music.AcquireQueueLockAsync(session.GuildId);
        try
        {
            return BuildMenu(session, _music.GetPlayer(session.GuildId));
        }
        finally
        {
            queueLock.Release();
        }
    }

    private Embed BuildMenu(MusicSession session, MusicPlayer? player)
    {
        lock (session.Sync)
        {
            var embed = new EmbedBuilder()
                .WithColor(new Color(0x1DB954))
                .WithTitle("🎶 Music session")
                .WithFooter($"Songs take turns: one from each person, round and round. Up to {SongsPerPerson} waiting " +
                            "each. A skip needs more than half the listeners - or whoever picked the song.");

            if (!session.Started)
            {
                embed.WithDescription($"Hosted by <@{session.HostId}>. **Building the queue** - add songs with the " +
                                      "button below, `/play`, or by asking me. When everyone's ready, the host " +
                                      "presses **Start** (from a voice channel).");
            }
            else
            {
                embed.WithDescription($"Hosted by <@{session.HostId}> · playing in " +
                                      (player is null ? "voice" : $"<#{player.VoiceChannelId}>") + ".");
                if (player?.CurrentItem is { } current)
                {
                    var pick = current as SessionTrackItem;
                    var votes = pick is not null && pick.EntryId == session.VoteEntryId ? session.SkipVotes.Count : 0;
                    embed.AddField("Now playing", $"{MusicFormatting.TrackLink(current.Track)}" +
                                                  (pick is null ? "" : $" - <@{pick.PickerId}>'s pick") +
                                                  (votes > 0 ? $"\n⏭️ {votes} vote{(votes == 1 ? "" : "s")} to skip" : ""));
                }
                else
                {
                    embed.AddField("Now playing", "Nothing - add a song to keep it going.");
                }
            }

            var upNext = Waiting(session, player);
            embed.AddField(session.Started ? "Up next" : "In the queue so far",
                upNext.Count == 0
                    ? "*Nothing yet.*"
                    : FitField(upNext.Select((s, i) =>
                        $"`{i + 1}.` {MusicFormatting.TrackLink(s.Track)} - <@{s.PickerId}>").Take(10), upNext.Count, "\n"));

            if (session.Participants.Count > 0)
            {
                embed.AddField("Taking turns",
                    FitField(session.Participants.Select(p => $"<@{p}>"), session.Participants.Count, " → "));
            }

            return embed.Build();
        }
    }

    /// <summary>As many of <paramref name="items"/> as fit in an embed field (1024 characters; song links with long
    /// titles get there fast), then "...and N more".</summary>
    private static string FitField(IEnumerable<string> items, int total, string separator)
    {
        const int limit = EmbedFieldBuilder.MaxFieldValueLength - 30; // room for the "...and N more" line
        var text = new StringBuilder();
        var shown = 0;
        foreach (var item in items)
        {
            if (text.Length + separator.Length + item.Length > limit)
            {
                break;
            }

            text.Append(shown == 0 ? "" : separator).Append(item);
            shown++;
        }

        if (shown < total)
        {
            text.Append(shown == 0 ? "" : "\n").Append($"*...and {total - shown} more*");
        }

        return text.ToString();
    }

    private static Embed BuildEndedMenu(string note) => new EmbedBuilder()
        .WithColor(Color.DarkGrey)
        .WithTitle("🎶 Music session - over")
        .WithDescription(note + " Open a new one with `/session open` or by asking me.")
        .Build();

    private static MessageComponent BuildButtons(MusicSession session)
    {
        var builder = new ComponentBuilder()
            .WithButton("Add song", $"{ButtonPrefix}:{AddButton}:{session.Id}", ButtonStyle.Success, new Emoji("➕"));
        builder = session.Started
            ? builder.WithButton("Vote skip", $"{ButtonPrefix}:{SkipButton}:{session.Id}", ButtonStyle.Primary, new Emoji("⏭️"))
            : builder.WithButton("Start", $"{ButtonPrefix}:{StartButton}:{session.Id}", ButtonStyle.Primary, new Emoji("▶️"));
        return builder
            .WithButton("Leave", $"{ButtonPrefix}:{LeaveButton}:{session.Id}", ButtonStyle.Secondary, new Emoji("🚪"))
            .WithButton("End", $"{ButtonPrefix}:{EndButton}:{session.Id}", ButtonStyle.Danger, new Emoji("⏹️"))
            .Build();
    }

    /// <summary>The songs still to play, in turn order. Call under <see cref="MusicSession.Sync"/>.</summary>
    private static List<SessionTrackItem> Waiting(MusicSession session, MusicPlayer? player)
        => session.Started
            ? player?.Queue.OfType<SessionTrackItem>().ToList() ?? []
            : FairOrder.Arrange(session.Lobby, s => s.PickerId, session.Participants, null);

    /// <summary>Where a song stands: 0 playing now, 1 next, and so on; -1 if it's not there.</summary>
    private static int PositionOf(MusicSession session, MusicPlayer? player, Guid entryId)
    {
        if ((player?.CurrentItem as SessionTrackItem)?.EntryId == entryId)
        {
            return 0;
        }

        lock (session.Sync)
        {
            var index = Waiting(session, player).FindIndex(s => s.EntryId == entryId);
            return index < 0 ? -1 : index + 1;
        }
    }

    private static string MenuLinkText(MusicSession session)
        => session.Menu is { } menu ? $" ([menu]({menu.GetJumpUrl()}))" : "";

    private async Task EndCoreAsync(MusicSession session, string note)
    {
        if (!Close(session, null))
        {
            return;
        }

        if (_music.GetPlayer(session.GuildId) is { } player)
        {
            try
            {
                await player.StopAsync();
                await player.DisposeAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Couldn't stop the player when ending a session in guild {GuildId}.", session.GuildId);
            }
        }

        await RefreshMenuAsync(session, note);
    }

    /// <summary>Marks the session over and forgets it; false if it already was. With a note, also redraws the menu
    /// in the background.</summary>
    private bool Close(MusicSession session, string? note)
    {
        lock (session.Sync)
        {
            if (session.Ended)
            {
                return false;
            }

            session.Ended = true;
        }

        _sessions.TryRemove(new KeyValuePair<ulong, MusicSession>(session.GuildId, session));
        if (note is not null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await RefreshMenuAsync(session, note);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Couldn't mark a music session menu as over.");
                }
            });
        }

        return true;
    }

    private Task OnTrackStartedAsync(MusicPlayer player, ITrackQueueItem item)
    {
        if (!_sessions.TryGetValue(player.GuildId, out var session))
        {
            return Task.CompletedTask;
        }

        lock (session.Sync)
        {
            if (!session.Started)
            {
                return Task.CompletedTask;
            }

            var pick = item as SessionTrackItem;
            session.CurrentPickerId = pick?.PickerId;
            session.VoteEntryId = pick?.EntryId;
            session.SkipVotes.Clear();
        }

        RefreshMenu(session);
        return Task.CompletedTask;
    }

    private Task OnPlayerClosedAsync(ulong guildId)
    {
        // The music stopped some other way (/stop, the idle timeout, the bot was moved out): the session's over too.
        if (_sessions.TryGetValue(guildId, out var session))
        {
            bool started;
            lock (session.Sync)
            {
                started = session.Started;
            }

            if (started)
            {
                Close(session, "The music stopped, so the session's over.");
            }
        }

        return Task.CompletedTask;
    }
}
