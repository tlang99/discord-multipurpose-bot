using System.Collections.Concurrent;
using System.Text;
using Discord;
using Discord.Net;
using Discord.WebSocket;
using Lavalink4NET.Players;
using Lavalink4NET.Protocol.Payloads.Events;
using Lavalink4NET.Tracks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModBot.Common;
using ModBot.Configuration;
using ModBot.Models;
using ModBot.Services.Storage;
using ModBot.Services.Moderation;
using ModBot.Services.Settings;

namespace ModBot.Modules.Music;

/// <summary>
/// Each server's standing music queue: anyone adds songs at any time (they're saved, so they wait through the bot
/// leaving voice and restarts), and once someone starts the music they play in turns, one from each person, with
/// votes to skip. Runs the pinned menu too. The slash commands, the menu's buttons and the chatbot all come through
/// here (via <see cref="MusicControls"/>), so they follow the same rules.
/// </summary>
public class MusicQueueService : IHostedService
{
    public const string ButtonPrefix = "mq";
    public const string AddButton = ButtonPrefix + ":add";
    public const string PlayButton = ButtonPrefix + ":play";
    public const string SkipButton = ButtonPrefix + ":skip";
    public const string PauseButton = ButtonPrefix + ":pause";
    public const string ResumeButton = ButtonPrefix + ":resume";
    public const string StopButton = ButtonPrefix + ":stop";
    public const string LoopButton = ButtonPrefix + ":loop";
    public const string VolumeDownButton = ButtonPrefix + ":vol-down";
    public const string VolumeUpButton = ButtonPrefix + ":vol-up";
    public const string BackButton = ButtonPrefix + ":back";
    public const string HistoryButton = ButtonPrefix + ":history";

    /// <summary>The History list's select menu: picking a song adds it again.</summary>
    public const string HistoryPick = ButtonPrefix + ":history-pick";
    public const string MineButton = ButtonPrefix + ":mine";

    /// <summary>The private list "Remove mine" opens: one of your songs, or all of them.</summary>
    public const string MinePick = ButtonPrefix + ":mine-pick";

    /// <summary>The <see cref="MinePick"/> choice for all of them.</summary>
    public const string AllMine = "all";

    private readonly IMusicQueueStore _store;
    private readonly IGuildSettingsStore _settings;
    private readonly MusicService _music;
    private readonly ServerSettingsService _serverSettings;
    private readonly DiscordSocketClient _client;
    private readonly MusicOptions _options;
    private readonly ILogger<MusicQueueService> _logger;
    private readonly DjPicker _dj;

    /// <summary>Skip votes for the song playing in each server. In memory: they only matter for that one song.</summary>
    private readonly ConcurrentDictionary<ulong, Votes> _votes = new();

    /// <summary>Votes to stop the music, like skip votes: for the song playing when they were cast.</summary>
    private readonly ConcurrentDictionary<ulong, Votes> _stopVotes = new();

    /// <summary>Who started the music playing in each server: they can stop it without a vote.</summary>
    private readonly ConcurrentDictionary<ulong, ulong> _starters = new();

    /// <summary>The newest thing worth knowing (who added a song, a saved song that had to be skipped), shown on the
    /// menu rather than posted, so the music channel stays quiet. In memory: it's only news until the next one.</summary>
    private readonly ConcurrentDictionary<ulong, (string Text, DateTimeOffset At)> _menuNotes = new();

    /// <summary>Puts <paramref name="text"/> on the server's menu as its newest note, dated so an old one reads as old.</summary>
    private void NoteOnMenu(ulong guildId, string text) => _menuNotes[guildId] = (text, DateTimeOffset.UtcNow);

    /// <summary>When the music last left voice in each server (/stop, the idle timeout, a disconnect), shown on the
    /// menu while nothing's playing, so a bot that left on its own doesn't look like it crashed.</summary>
    private readonly ConcurrentDictionary<ulong, DateTimeOffset> _leftVoiceAt = new();

    private const string MenuTitle = "🎶 Music queue";

    /// <summary>Keeping the menu at the bottom: wait for this long a quiet spell before reposting it...</summary>
    private static readonly TimeSpan RepostWhenQuietFor = TimeSpan.FromSeconds(8);

    /// <summary>...but not longer than this in a chat that never goes quiet...</summary>
    private static readonly TimeSpan RepostWithin = TimeSpan.FromMinutes(2);

    /// <summary>...and never more often than this.</summary>
    private static readonly TimeSpan RepostAtMostEvery = TimeSpan.FromMinutes(1);

    private readonly KeyedLock<ulong> _menuLocks = new();

    /// <summary>Where each server's menu is, so every message in the server needn't read the saved queue to check.</summary>
    private readonly ConcurrentDictionary<ulong, (ulong ChannelId, ulong MessageId)> _menus = new();

    private readonly ConcurrentDictionary<ulong, RepostTimer> _reposts = new();
    private int _menusRestored;

    public MusicQueueService(IMusicQueueStore store, IGuildSettingsStore settings, MusicService music,
        ServerSettingsService serverSettings,
        MusicPlayerEvents events, DjPicker dj,
        DiscordSocketClient client, IOptions<MusicOptions> options, ILogger<MusicQueueService> logger)
    {
        _dj = dj;
        _store = store;
        _settings = settings;
        _music = music;
        _serverSettings = serverSettings;
        _client = client;
        _options = options.Value;
        _logger = logger;

        events.TrackStarted = OnTrackStartedAsync;
        events.TrackEnded = OnTrackEndedAsync;
        events.PlayerClosed = OnPlayerClosedAsync;

        // The menu shows what's playing; "Now playing" posts as well would only push it up the channel.
        events.AnnounceSongs = guildId => !_menus.ContainsKey(guildId);
    }

    public int SongsPerPerson => Math.Max(1, _options.SongsPerPerson);

    private int MaxQueueLength => Math.Max(1, _options.MaxQueueLength);

    /// <summary>More than half the people listening.</summary>
    public static int VotesNeeded(int listeners) => Math.Max(1, listeners) / 2 + 1;

    public Task<MusicQueueState> GetAsync(ulong guildId) => _store.GetAsync(guildId);

    /// <summary>Adds songs for <paramref name="user"/>. The music also starts if nothing's playing and they're in a
    /// voice channel; otherwise the songs just wait.</summary>
    public async Task<MusicReply> AddAsync(SocketGuildUser user, string query, ITextChannel fallbackChannel)
    {
        if (MusicService.CheckQuery(query) is { } badQuery)
        {
            return MusicReply.Fail(badQuery);
        }

        var lookup = await _music.FindTracksAsync(query, user.Guild.Id);
        if (lookup.Error is not null)
        {
            return MusicReply.Fail(lookup.Error);
        }

        // Titles end up on the pinned menu for everyone to see, so they follow the server's word filter, with the same
        // moderator exemption as auto-mod. Checked now, while the person can pick something else.
        var tracks = lookup.Tracks;
        var playlistName = lookup.PlaylistName;
        var filtered = 0;
        var settings = await _settings.GetAsync(user.Guild.Id);
        if (TextFilter.AppliesToBotPost(user))
        {
            tracks = tracks.Where(t => TextFilter.FindViolation(t.Title, settings) is null).ToList();
            filtered = lookup.Tracks.Count - tracks.Count;
            if (tracks.Count == 0)
            {
                return MusicReply.Fail(lookup.IsPlaylist
                    ? "Every song in that playlist has a title that breaks this server's word filter."
                    : "That song's title breaks this server's word filter, so it can't go in the queue.");
            }

            if (playlistName is not null && TextFilter.FindViolation(playlistName, settings) is not null)
            {
                playlistName = "a playlist";
            }
        }

        var songs = tracks.Select(t => new QueuedSong
        {
            Track = t.ToString(),
            Title = t.Title,
            Uri = t.Uri?.ToString(),
            Duration = t.Duration,
            IsLiveStream = t.IsLiveStream,
            PickerId = user.Id,
            PickerName = user.DisplayName,
        }).ToList();

        return await QueueSongsAsync(user, songs, lookup.IsPlaylist ? playlistName : null, filtered, fallbackChannel);
    }

    /// <summary>Adds a song from the server's history for <paramref name="user"/>, as if they'd asked for it: "play
    /// that again" (the newest) or one picked from the History list (by <paramref name="songId"/>).</summary>
    public async Task<MusicReply> AgainAsync(SocketGuildUser user, ITextChannel fallbackChannel, Guid? songId = null)
    {
        var history = (await _store.GetAsync(user.Guild.Id)).History;
        if ((songId is { } id ? history.FirstOrDefault(s => s.Id == id) : history.FirstOrDefault()) is not { } song)
        {
            return MusicReply.Fail(history.Count == 0
                ? "Nothing's been played yet, so there's nothing to play again."
                : "That song isn't in the history any more.");
        }

        // As when it was first added, its title follows the word filter for whoever adds it now.
        if (TextFilter.AppliesToBotPost(user) &&
            TextFilter.FindViolation(song.Title, await _settings.GetAsync(user.Guild.Id)) is not null)
        {
            return MusicReply.Fail("That song's title breaks this server's word filter, so it can't go in the queue.");
        }

        return await QueueSongsAsync(user, [MusicQueueRules.Copy(song, user.Id, user.DisplayName)], null, 0,
            fallbackChannel);
    }

    /// <summary>The music's recent history, newest first, for the History list and /history. Titles that break the
    /// server's word filter as it is now (it may have changed since they played) are left out, since the list is
    /// shown to people.</summary>
    public async Task<IReadOnlyList<QueuedSong>> HistoryAsync(ulong guildId)
    {
        var settings = await _settings.GetAsync(guildId);
        return (await _store.GetAsync(guildId)).History
            .Where(s => TextFilter.FindViolation(s.Title, settings) is null)
            .ToList();
    }

    /// <summary>Puts <paramref name="songs"/> in the queue for <paramref name="user"/> (as many as fit their cap), says
    /// where they landed, and starts the music if nothing's playing and they're in voice.</summary>
    /// <param name="playlistName">The playlist they came from, or null for a single song.</param>
    /// <param name="filtered">How many of the playlist were left out for breaking the word filter.</param>
    private async Task<MusicReply> QueueSongsAsync(SocketGuildUser user, List<QueuedSong> songs, string? playlistName,
        int filtered, ITextChannel fallbackChannel)
    {
        var (result, added, position, wait, limit) = await _store.MutateAsync(user.Guild.Id, state =>
        {
            // Read before the add: which limit a playlist that doesn't all fit was stopped by.
            var tighter = MusicQueueRules.TighterLimit(state, user.Id, SongsPerPerson, MaxQueueLength);
            var outcome = MusicQueueRules.Add(state, songs, user.Id, SongsPerPerson, MaxQueueLength, out var taken);
            var first = taken.Count > 0 ? taken[0].Id : Guid.Empty;
            return (outcome, taken, MusicQueueRules.PositionOf(state, first), MusicQueueRules.WaitBefore(state, first),
                tighter);
        });

        switch (result)
        {
            case QueueAddResult.PersonFull:
                return MusicReply.Fail($"You've got {SongsPerPerson} songs waiting, the most each person can have. " +
                                       "Add more once some have played.");
            case QueueAddResult.QueueFull:
                return MusicReply.Fail($"The queue is full ({MaxQueueLength} songs).");
        }

        // A playlist that didn't all fit says so plainly, so nobody thinks the rest are coming.
        var cap = limit == QueueAddResult.QueueFull
            ? $"the queue holds {MaxQueueLength}"
            : $"each person can have {SongsPerPerson} waiting";
        var what = playlistName is not null
            ? (added.Count < songs.Count
                  ? $"the first **{added.Count}** of {songs.Count} songs from **{TextTools.Escape(playlistName)}** " +
                    $"(the rest weren't added - {cap})"
                  : $"**{added.Count}** songs from **{TextTools.Escape(playlistName)}**") +
              (filtered > 0 ? $" (skipped {filtered} whose titles break the word filter)" : "")
            : $"{MusicFormatting.SongLink(added[0])} ({MusicFormatting.SongLength(added[0])})";
        var text = new StringBuilder($"➕ {user.Mention} added {what}");

        var player = _music.GetPlayer(user.Guild.Id);
        var idle = player?.CurrentItem is null;
        if (idle && user.VoiceChannel is not null)
        {
            var start = await StartAsync(user, fallbackChannel);
            text.Append(start.Success ? $". {start.Text}" : $". (Couldn't start the music: {start.Text})");
        }
        else
        {
            var eta = idle ? null : TimeUntil(player!, wait);
            text.Append(position > 0 ? $" - #{position} in the turns{(eta is null ? "" : $", {eta} away")}." : ".");
            if (idle)
            {
                text.Append(" Nothing's playing yet: join a voice channel and press **Play** on the music menu " +
                            "(or say \"start the music\") when you're ready.");
            }
        }

        NoteOnMenu(user.Guild.Id, $"➕ <@{user.Id}> added {what}.");
        RefreshMenu(user.Guild.Id);
        return MusicReply.Ok(text.ToString());
    }

    /// <summary>Roughly how long until a song plays, given how long the songs ahead of it last: what's left of the
    /// current one plus those. Null when that can't be known (a live stream, or a song looping until skipped).</summary>
    private static string? TimeUntil(MusicPlayer player, TimeSpan? songsAhead)
    {
        if (songsAhead is not { } ahead || player.CurrentItem?.Track is not { IsLiveStream: false } track ||
            player.RepeatMode == Lavalink4NET.Players.Queued.TrackRepeatMode.Track)
        {
            return null;
        }

        var left = track.Duration - (player.Position?.Position ?? TimeSpan.Zero);
        return MusicFormatting.RoughWait(ahead + (left > TimeSpan.Zero ? left : TimeSpan.Zero));
    }

    /// <summary>Starts the music: the bot joins <paramref name="user"/>'s voice channel and plays the next turn.</summary>
    public async Task<MusicReply> StartAsync(SocketGuildUser user, ITextChannel fallbackChannel)
    {
        if (_music.GetPlayer(user.Guild.Id) is { CurrentItem: not null } playing)
        {
            return MusicReply.Fail(user.VoiceChannel?.Id == playing.VoiceChannelId
                ? "The music's already playing."
                : $"I'm already playing in <#{playing.VoiceChannelId}> - join that channel.");
        }

        if ((await _store.GetAsync(user.Guild.Id)).Songs.Count == 0)
        {
            return MusicReply.Fail("There's nothing in the queue yet - add some songs first.");
        }

        if (_music.GetJoinProblem(user) is { } problem)
        {
            return MusicReply.Fail(problem);
        }

        var (player, joinError) = await _music.JoinPlayerAsync(user, await AnnouncementChannelAsync(user.Guild, fallbackChannel));
        if (player is null)
        {
            return MusicReply.Fail(joinError);
        }

        // Someone else may have started it while the bot was joining (then it's playing already, and isn't theirs:
        // its volume, maybe changed or dipped since, is left alone).
        var (started, first) = await _music.WithPlaybackLockAsync(user.Guild.Id, async () =>
        {
            if (player.CurrentItem is not null)
            {
                return (true, (QueuedSong?)null);
            }

            // At the server's own volume, kept from last time. (This is the one place music starts from nothing.)
            await player.SetVolumeAsync((await _store.GetAsync(user.Guild.Id)).VolumePercent / 100f);
            return await PlayNextLockedAsync(player) is { } song ? (true, song) : (false, null);
        });
        if (!started)
        {
            return MusicReply.Fail("There's nothing in the queue yet - add some songs first.");
        }

        if (first is not null)
        {
            _starters[user.Guild.Id] = user.Id; // they can stop it without a vote
        }

        return MusicReply.Ok($"▶️ {user.Mention} started the music in <#{player.VoiceChannelId}>." +
                             (first is null ? "" : $" {NowPlayingLine(first)}"));
    }

    /// <summary>A vote to skip the song that's playing. Whoever picked it, and moderators, skip it outright.</summary>
    public async Task<MusicReply> VoteSkipAsync(SocketGuildUser user)
    {
        var player = _music.GetPlayer(user.Guild.Id);
        if (player?.CurrentItem is not { } current)
        {
            return MusicReply.Fail("Nothing's playing to skip.");
        }

        var moderator = Moderators.IsModerator(user);
        if (user.VoiceChannel?.Id != player.VoiceChannelId && !moderator)
        {
            return MusicReply.Fail($"Join <#{player.VoiceChannelId}> to vote - only people listening get a say.");
        }

        var song = (current as QueueTrackItem)?.Song;
        var listening = Listeners(user.Guild, player.VoiceChannelId);
        var needed = VotesNeeded(listening.Count);
        int count;
        string reason;
        if (song?.PickerId == user.Id)
        {
            (count, reason) = (needed, $"{user.Mention} skipped their own pick");
        }
        else if (moderator)
        {
            (count, reason) = (needed, $"{user.Mention} (a moderator) skipped it");
        }
        else if (!listening.Contains(user.Id))
        {
            return MusicReply.Fail(DeafenedVoter);
        }
        else
        {
            (count, var added) = AddVote(_votes, user, song?.Id, listening);
            if (!added)
            {
                return MusicReply.Fail($"You've already voted to skip this one ({count}/{needed}).");
            }

            reason = $"{count}/{needed} votes";
        }

        if (count < needed)
        {
            RefreshMenu(user.Guild.Id);
            // Short: how skipping works is on the menu, and this is read out again on every vote.
            return MusicReply.Vote($"🗳️ Skip vote **{count}/{needed}** for {MusicFormatting.TrackLink(current.Track)}.");
        }

        // Null when the song had already finished; otherwise the song that played next, if any.
        var skipped = await _music.WithPlaybackLockAsync<(QueuedSong? Next, bool)?>(user.Guild.Id, async () =>
        {
            // Only skip the song that was voted on: if it ended while the votes came in, the next one is innocent.
            if (player.CurrentItem is null || (player.CurrentItem as QueueTrackItem)?.Song.Id != song?.Id)
            {
                return null;
            }

            // A looped song is skipped for good, rather than the loop carrying on to the next person's song.
            player.RepeatMode = Lavalink4NET.Players.Queued.TrackRepeatMode.None;
            var playedNext = await PlayNextLockedAsync(player);
            if (playedNext is null)
            {
                await player.StopAsync();
            }

            return (playedNext, true);
        });

        if (skipped is not var (next, _))
        {
            return MusicReply.Fail("That song already finished.");
        }

        // Skipped the last one: with DJ mode on, it picks something else rather than going quiet.
        if (next is null)
        {
            Background.Run(_logger, () => PlayDjPickAsync(player), LogLevel.Warning,
                "DJ mode couldn't play a song after a skip in guild {GuildId}.", user.Guild.Id);
        }

        // What's on now, and whose it is, so the reply says it all (a voice skip is answered with just a chime).
        return MusicReply.Ok($"⏭️ Skipped {MusicFormatting.TrackLink(current.Track)} ({reason})." +
                             (next is null ? " That was the last song in the queue." : $" {NowPlayingLine(next)}"));
    }

    /// <summary>"Now playing: Title (@picker's pick)." for a reply, when a song has just started.</summary>
    private static string NowPlayingLine(QueuedSong song)
        => $"Now playing: {MusicFormatting.SongLink(song)} (<@{song.PickerId}>'s pick).";

    /// <summary>The people in <paramref name="channelId"/> who can hear the music, for votes: not bots, and not
    /// deafened (someone who's deafened can't hear it, so mustn't be needed to vote it off).</summary>
    private static HashSet<ulong> Listeners(SocketGuild guild, ulong channelId)
        => guild.GetVoiceChannel(channelId)?.ConnectedUsers
            .Where(u => !u.IsBot && !u.IsDeafened && !u.IsSelfDeafened).Select(u => u.Id).ToHashSet() ?? [];

    /// <summary>Adds <paramref name="user"/>'s vote for <paramref name="target"/> (a song): votes for anything else are
    /// cleared first, and votes from people no longer listening don't count, since the bar is set by who's there
    /// now. Returns the count, and false if they'd already voted.</summary>
    private static (int Count, bool New) AddVote(ConcurrentDictionary<ulong, Votes> all, SocketGuildUser user,
        Guid? target, HashSet<ulong> listening)
    {
        var votes = all.GetOrAdd(user.Guild.Id, _ => new Votes());
        lock (votes)
        {
            if (votes.SongId != target)
            {
                votes.SongId = target;
                votes.Voters.Clear();
            }

            votes.Voters.IntersectWith(listening);
            var added = votes.Voters.Add(user.Id);
            return (votes.Voters.Count, added);
        }
    }

    /// <summary>Why a deafened person can't vote: they can't hear the music, so they don't count toward the
    /// votes.</summary>
    private const string DeafenedVoter = "You're deafened, so you can't vote on the music - undeafen to have a say.";

    /// <summary>
    /// Whether <paramref name="user"/> stopping the music goes through now (see <see cref="StopsAtOnce"/>); otherwise
    /// it's a vote, like a skip: more than half the listeners. Returns null to stop now, or the reply about the vote.
    /// </summary>
    public MusicReply? VoteStop(SocketGuildUser user, MusicPlayer player)
    {
        var inChannel = user.Guild.GetVoiceChannel(player.VoiceChannelId)?.ConnectedUsers.Count(u => !u.IsBot) ?? 0;
        var starter = _starters.TryGetValue(user.Guild.Id, out var id) && id == user.Id;
        if (StopsAtOnce(inChannel, Moderators.IsModerator(user), starter))
        {
            return null;
        }

        // The votes needed are from those who can hear it: if the others are deafened, the one listener decides.
        // (Deafening yourself can't help: it takes away your own vote.) Nobody hearing it at all, there's no one to
        // vote, so it just stops.
        var listening = Listeners(user.Guild, player.VoiceChannelId);
        if (listening.Count == 0)
        {
            return null;
        }

        if (!listening.Contains(user.Id))
        {
            return MusicReply.Fail(DeafenedVoter);
        }

        var needed = VotesNeeded(listening.Count);
        var (count, added) = AddVote(_stopVotes, user, (player.CurrentItem as QueueTrackItem)?.Song.Id, listening);
        if (count >= needed)
        {
            return null;
        }

        return added
            ? MusicReply.Vote($"🗳️ Stop vote **{count}/{needed}**: more than half the listeners stops the music.")
            : MusicReply.Fail($"You've already voted to stop ({count}/{needed}).");
    }

    /// <summary>Whether a stop goes through without a vote: with fewer than 3 people in the channel (everyone in it,
    /// deafened too, so someone deafening themselves can't bring it under 3), for whoever started the music, or for a
    /// moderator.</summary>
    public static bool StopsAtOnce(int inChannel, bool isModerator, bool isStarter)
        => inChannel < 3 || isModerator || isStarter;

    /// <summary>Removes the song at <paramref name="position"/> in play order: your own, or anyone's for moderators.</summary>
    public async Task<MusicReply> RemoveAsync(SocketGuildUser user, int position)
    {
        var moderator = Moderators.IsModerator(user);
        var (result, removed, total) = await _store.MutateAsync(user.Guild.Id, state =>
        {
            var count = state.Songs.Count;
            var outcome = MusicQueueRules.RemoveAt(state, position, user.Id, moderator, out var song);
            return (outcome, song, count);
        });

        return Removed(user, result, removed, $"There's no song at position {position} - the queue has {total}.");
    }

    /// <summary>Removes one waiting song by its ID (from the "Remove mine" list), if it's still there and theirs.</summary>
    public async Task<MusicReply> RemoveSongAsync(SocketGuildUser user, Guid songId)
    {
        var moderator = Moderators.IsModerator(user);
        var (result, removed) = await _store.MutateAsync(user.Guild.Id, state =>
            (MusicQueueRules.Remove(state, songId, user.Id, moderator, out var song), song));
        return Removed(user, result, removed, "That song isn't waiting any more - it may have played already.");
    }

    /// <summary>Removes the waiting song whose title has <paramref name="title"/> in it ("remove mr brightside from
    /// the queue"): one of theirs, or anyone's for a moderator. Several matching ones aren't guessed between.</summary>
    public async Task<MusicReply> RemoveTitledAsync(SocketGuildUser user, string title)
    {
        var moderator = Moderators.IsModerator(user);
        var (result, removed, matches) = await _store.MutateAsync(user.Guild.Id, state =>
        {
            var found = MusicQueueRules.FindByTitle(state, title, user.Id, moderator);
            if (found.Count != 1)
            {
                return (QueueRemoveResult.NotFound, (QueuedSong?)null, found.Count);
            }

            return (MusicQueueRules.Remove(state, found[0].Id, user.Id, moderator, out var song), song, 1);
        });

        return matches > 1
            ? MusicReply.Fail($"{matches} of {(moderator ? "the" : "your")} waiting songs match that - say its " +
                              "number from `/queue` (\"remove 3\"), or use the music menu's Remove mine.")
            : Removed(user, result, removed, moderator
                ? "No waiting song has that in its title. See `/queue`."
                : "None of your waiting songs has that in its title. See `/queue`.");
    }

    private MusicReply Removed(SocketGuildUser user, QueueRemoveResult result, QueuedSong? song, string notFound)
    {
        switch (result)
        {
            case QueueRemoveResult.NotFound:
                return MusicReply.Fail(notFound);
            case QueueRemoveResult.NotYours:
                return MusicReply.Fail("That's someone else's song - you can only remove your own. (Vote to skip it " +
                                       "when it comes on.)");
        }

        RefreshMenu(user.Guild.Id);
        return MusicReply.Ok($"🗑️ Removed {MusicFormatting.SongLink(song!)} from the queue.");
    }

    /// <summary>The person's waiting songs, in play order (with their place in it), for the "Remove mine" list.
    /// </summary>
    public async Task<List<(int Position, QueuedSong Song)>> SongsOfAsync(ulong guildId, ulong userId)
        => MusicQueueRules.Order(await _store.GetAsync(guildId))
            .Select((song, index) => (index + 1, song))
            .Where(s => s.song.PickerId == userId)
            .ToList();

    public async Task<MusicReply> RemoveMineAsync(SocketGuildUser user)
    {
        var removed = await _store.MutateAsync(user.Guild.Id, state => MusicQueueRules.RemoveAllFor(state, user.Id));
        if (removed == 0)
        {
            return MusicReply.Fail("You don't have any songs waiting.");
        }

        RefreshMenu(user.Guild.Id);
        return MusicReply.Ok($"🗑️ {user.Mention} took their {removed} song{(removed == 1 ? "" : "s")} out of the queue.");
    }

    /// <summary>Makes the person's song at <paramref name="position"/> (in play order) the one they play on their next
    /// turn. Only their own songs move, so nobody else waits longer.</summary>
    public async Task<MusicReply> PlayNextAsync(SocketGuildUser user, int position)
    {
        var (result, song, total, at, already) = await _store.MutateAsync(user.Guild.Id, state =>
        {
            var count = state.Songs.Count;
            var nextOfTheirs = MusicQueueRules.Order(state).FirstOrDefault(s => s.PickerId == user.Id)?.Id;
            var outcome = MusicQueueRules.PlayNextAt(state, position, user.Id, out var moved);
            return (outcome, moved, count, moved is null ? -1 : MusicQueueRules.PositionOf(state, moved.Id),
                moved is not null && moved.Id == nextOfTheirs);
        });

        switch (result)
        {
            case QueueRemoveResult.NotFound:
                return MusicReply.Fail($"There's no song at position {position} - the queue has {total}.");
            case QueueRemoveResult.NotYours:
                return MusicReply.Fail("That's someone else's song - you can only reorder your own.");
        }

        if (already)
        {
            return MusicReply.Fail($"{MusicFormatting.SongLink(song!)} is already the next of your songs - #{at} in " +
                                   "the turns.");
        }

        RefreshMenu(user.Guild.Id);
        return MusicReply.Ok($"⏫ {MusicFormatting.SongLink(song!)} is up next of {user.Mention}'s songs - " +
                             $"#{at} in the turns.");
    }

    /// <summary>Shuffles the person's own waiting songs (everyone else's stay where they are).</summary>
    public async Task<MusicReply> ShuffleMineAsync(SocketGuildUser user)
    {
        var count = await _store.MutateAsync(user.Guild.Id,
            state => MusicQueueRules.ShuffleMine(state, user.Id, Random.Shared));
        if (count < 2)
        {
            return MusicReply.Fail(count == 0
                ? "You don't have any songs waiting."
                : "You've only got one song waiting - nothing to shuffle.");
        }

        RefreshMenu(user.Guild.Id);
        return MusicReply.Ok($"🔀 {user.Mention} shuffled their {count} songs.");
    }

    /// <summary>Empties the whole queue: moderators only, since it's everyone's songs.</summary>
    public async Task<MusicReply> ClearAsync(SocketGuildUser user)
    {
        if (!Moderators.IsModerator(user))
        {
            return MusicReply.Fail("Only moderators can clear everyone's songs. To take out yours, use " +
                                   "`/remove-mine` (or say \"remove my songs\").");
        }

        var removed = await _store.MutateAsync(user.Guild.Id, MusicQueueRules.Clear);
        if (removed == 0)
        {
            return MusicReply.Fail("The queue was already empty.");
        }

        RefreshMenu(user.Guild.Id);
        return MusicReply.Ok($"🧹 {user.Mention} cleared {removed} song(s) from the queue.");
    }

    /// <summary>Whether finished songs go back in the queue (/loop queue); it stays on until turned off.</summary>
    public async Task<bool> LoopsQueueAsync(ulong guildId) => (await _store.GetAsync(guildId)).LoopQueue;

    public async Task SetLoopQueueAsync(ulong guildId, bool on)
    {
        await _store.MutateAsync(guildId, state => state.LoopQueue = on);
        RefreshMenu(guildId);
    }

    /// <summary>Posts the menu in <paramref name="channel"/> and pins it, replacing any older one.</summary>
    public async Task<(bool Pinned, string? Error)> PostMenuAsync(ITextChannel channel)
    {
        var guildId = channel.GuildId;
        return await _menuLocks.RunAsync<(bool, string?)>(guildId, async () =>
        {
            var state = await _store.GetAsync(guildId);
            IUserMessage menu;
            try
            {
                var player = _music.GetPlayer(guildId);
                menu = await channel.SendMessageAsync(embed: BuildMenu(state, player),
                    components: BuildButtons(state, player), allowedMentions: AllowedMentions.None);
            }
            catch (HttpException ex)
            {
                _logger.LogWarning(ex, "Couldn't post the music menu in channel {ChannelId}.", channel.Id);
                return (false, $"I couldn't post the menu in {channel.Mention} - check I can send messages and embed " +
                               "links there.");
            }

            _menus[guildId] = (channel.Id, menu.Id);
            var pinned = await MessageTools.TryPinAsync(menu, _logger, "the music menu");
            await _store.MutateAsync(guildId, s =>
            {
                s.MenuChannelId = channel.Id;
                s.MenuMessageId = menu.Id;
            });

            // Only one menu: take the old one down (deleting it unpins it too).
            if (state.MenuChannelId is { } oldChannelId && state.MenuMessageId is { } oldId && oldId != menu.Id &&
                await FetchMenuAsync(oldChannelId, oldId) is { } old)
            {
                try
                {
                    await old.DeleteAsync();
                }
                catch (HttpException ex)
                {
                    _logger.LogDebug(ex, "Couldn't delete the old music menu.");
                    await TryUnpinAsync(old);
                }
            }

            return (pinned, null);
        });
    }

    /// <summary>A link to the server's menu, or null if none has been posted (or it's gone).</summary>
    public async Task<string?> MenuLinkAsync(ulong guildId)
    {
        var state = await _store.GetAsync(guildId);
        return state.MenuChannelId is { } channelId && state.MenuMessageId is { } messageId
            ? MessageTools.Link(guildId, channelId, messageId)
            : null;
    }

    /// <summary>Redraws the menu in the background, so replies don't wait on it.</summary>
    public void RefreshMenu(ulong guildId)
        => Background.Run(_logger, () => RefreshMenuAsync(guildId), LogLevel.Debug,
            "Couldn't update the music menu in guild {GuildId}.", guildId);

    /// <summary>Where "Now playing" goes: the music channel, else the menu's channel, else where the request was made.</summary>
    private async Task<ITextChannel> AnnouncementChannelAsync(SocketGuild guild, ITextChannel fallback)
    {
        if ((await _serverSettings.MusicChannelAsync(guild)).Channel is { } musicChannel)
        {
            return musicChannel;
        }

        var state = await _store.GetAsync(guild.Id);
        return state.MenuChannelId is { } id && guild.GetTextChannel(id) is { } menuChannel ? menuChannel : fallback;
    }

    /// <summary>Plays the next turn in place of whatever's playing, and returns it; null if nothing's waiting. Call
    /// with the playback lock held.</summary>
    private async Task<QueuedSong?> PlayNextLockedAsync(MusicPlayer player)
    {
        while (await _store.MutateAsync(player.GuildId, MusicQueueRules.TakeNext) is { } song)
        {
            // Saved songs carry Lavalink's encoded track, so they play without another YouTube search.
            if (!LavalinkTrack.TryParse(song.Track, null, out var track))
            {
                _logger.LogWarning("Dropping a saved song that can't be read: '{Title}' in guild {GuildId}.",
                    song.Title, player.GuildId);
                NoteOnMenu(player.GuildId, $"⚠️ Skipped {MusicFormatting.SongLink(song)} (<@{song.PickerId}>'s " +
                                           "pick): its saved copy couldn't be read. Add it again to hear it.");
                continue;
            }

            await player.PlayAsync(new QueueTrackItem(track, song), enqueue: false);
            return song;
        }

        return null;
    }

    private async Task OnTrackStartedAsync(MusicPlayer player, ITrackQueueItem item)
    {
        _votes.TryRemove(player.GuildId, out _);
        _stopVotes.TryRemove(player.GuildId, out _);
        _leftVoiceAt.TryRemove(player.GuildId, out _); // playing again: the last "left voice" time is old news
        try
        {
            if (item is QueueTrackItem { Song: var song })
            {
                await _store.MutateAsync(player.GuildId, state => MusicQueueRules.RecordPlayed(state, song));
            }
        }
        catch (Exception ex)
        {
            // Only the history: the song still plays, and the menu still shows it.
            _logger.LogWarning(ex, "Couldn't note the song that started in guild {GuildId}.", player.GuildId);
        }
        finally
        {
            RefreshMenu(player.GuildId);
        }
    }

    /// <summary>Sets the server's volume (kept for next time) and, if the music's playing, the player's.</summary>
    /// <param name="change">The new volume from the saved one: worked out as it's saved, so two presses of 🔊 at once
    /// make two steps, not one. Returns what it was and what it is now (the same if nothing changed).</param>
    public async Task<(int Was, int Now)> SetVolumeAsync(ulong guildId, Func<int, int> change, MusicPlayer? player)
    {
        var (was, now) = await _store.MutateAsync(guildId, state =>
        {
            var before = state.VolumePercent;
            state.VolumePercent = MusicQueueRules.ClampVolume(change(before));
            return (before, state.VolumePercent);
        });

        if (now != was)
        {
            if (player is not null)
            {
                await player.SetVolumeAsync(now / 100f);
            }

            RefreshMenu(guildId);
        }

        return (was, now);
    }

    /// <summary>Goes back to the song before this one: it plays now, and the song that was playing goes back at the
    /// front of its picker's songs, so it isn't lost. It doesn't use up anyone's turn.</summary>
    public async Task<MusicReply> BackAsync(SocketGuildUser user, MusicPlayer player)
    {
        // All under the playback lock, so a song ending (or a skip) meanwhile can't make it put back a song that's
        // already finished, or go back from the wrong one.
        var reply = await _music.WithPlaybackLockAsync(user.Guild.Id, async () =>
        {
            var current = (player.CurrentItem as QueueTrackItem)?.Song;
            if (MusicQueueRules.PreviousOf(await _store.GetAsync(user.Guild.Id), current) is not { } previous)
            {
                return MusicReply.Fail("There's no song before this one yet.");
            }

            if (!LavalinkTrack.TryParse(previous.Track, null, out var track))
            {
                return MusicReply.Fail("I couldn't read the saved copy of the last song. Add it again by name.");
            }

            // A looped song would otherwise carry on looping the one gone back to, so the loop's turned off, and the
            // reply says so. (Only the song loop: looping the queue is saved separately and stays as it is.)
            var wasLooping = player.RepeatMode == Lavalink4NET.Players.Queued.TrackRepeatMode.Track;
            player.RepeatMode = Lavalink4NET.Players.Queued.TrackRepeatMode.None;
            try
            {
                await player.PlayAsync(new QueueTrackItem(track, MusicQueueRules.Copy(previous)), enqueue: false);
            }
            catch
            {
                // Nothing changed: the song is still playing, and still looping if it was.
                player.RepeatMode = wasLooping
                    ? Lavalink4NET.Players.Queued.TrackRepeatMode.Track
                    : Lavalink4NET.Players.Queued.TrackRepeatMode.None;
                throw;
            }

            // Only once the song gone back to is playing: otherwise the one cut off would be queued while it plays on.
            await _store.MutateAsync(user.Guild.Id, state => MusicQueueRules.GoBack(state, current));
            return MusicReply.Ok($"⏮️ {user.Mention} went back to {MusicFormatting.SongLink(previous)}." +
                                 (current is null
                                     ? ""
                                     : $" {MusicFormatting.SongLink(current)} is back in the queue, next of " +
                                       $"<@{current.PickerId}>'s songs.") +
                                 (wasLooping ? " (Looping the song is off now.)" : ""));
        });

        RefreshMenu(user.Guild.Id);
        return reply;
    }

    private Task OnTrackEndedAsync(MusicPlayer player, ITrackQueueItem item, TrackEndReason reason)
    {
        // Off the player's event so starting the next song never waits on (or inside) the event that ended this one.
        Background.Run(_logger, async () =>
        {
            try
            {
                if (reason == TrackEndReason.Finished && item is QueueTrackItem { Song: var song } &&
                    (await _store.GetAsync(player.GuildId)).LoopQueue)
                {
                    await _store.MutateAsync(player.GuildId, state => MusicQueueRules.Requeue(state, song));
                }

                var queueRanOut = await _music.WithPlaybackLockAsync(player.GuildId, async () =>
                {
                    // A skip or /play may have started something else in the meantime; only move on if what's
                    // "current" is still the song that ended (or nothing).
                    var stillCurrent = player.CurrentItem is null ||
                                       (player.CurrentItem as QueueTrackItem)?.Song.Id == (item as QueueTrackItem)?.Song.Id;
                    return player.State != PlayerState.Destroyed && stillCurrent &&
                           await PlayNextLockedAsync(player) is null;
                });

                if (queueRanOut)
                {
                    await PlayDjPickAsync(player);
                }
            }
            finally
            {
                RefreshMenu(player.GuildId);
            }
        }, LogLevel.Warning, "Couldn't play the next song in guild {GuildId}.", player.GuildId);
        return Task.CompletedTask;
    }

    /// <summary>DJ mode, when the server has it on: the queue's run out, so the chatbot picks a song like the recent
    /// ones and it plays. It isn't queued or anyone's turn, so the next song someone adds plays as soon as this one
    /// ends.</summary>
    private async Task PlayDjPickAsync(MusicPlayer player)
    {
        var guild = _client.GetGuild(player.GuildId);
        if (guild is null || !(await _serverSettings.GetAsync(guild.Id)).IsEnabled(ServerFeature.MusicDj))
        {
            return;
        }

        // Picking takes the model a few seconds: done outside the playback lock, then played only if nothing else
        // started (or got queued) meanwhile.
        var history = (await _store.GetAsync(guild.Id)).History;
        if (await _dj.PickAsync(guild.Id, history, (guild.CurrentUser.Id, DjName)) is not { } pick ||
            !LavalinkTrack.TryParse(pick.Track, null, out var track))
        {
            return;
        }

        var played = await _music.WithPlaybackLockAsync(guild.Id, async () =>
        {
            if (player.State == PlayerState.Destroyed || player.CurrentItem is not null ||
                MusicQueueRules.Order(await _store.GetAsync(guild.Id)).Count > 0)
            {
                return false;
            }

            await player.PlayAsync(new QueueTrackItem(track, pick), enqueue: false);
            return true;
        });

        if (played)
        {
            NoteOnMenu(guild.Id, $"🎧 DJ mode put on {MusicFormatting.SongLink(pick)}: the queue ran out. Add a " +
                                 "song and it plays next.");
        }
    }

    /// <summary>Who DJ mode's songs show as picked by.</summary>
    private const string DjName = "DJ mode";

    private Task OnPlayerClosedAsync(ulong guildId)
    {
        _votes.TryRemove(guildId, out _);
        _stopVotes.TryRemove(guildId, out _);
        _starters.TryRemove(guildId, out _);
        _leftVoiceAt[guildId] = DateTimeOffset.UtcNow;
        RefreshMenu(guildId);
        return Task.CompletedTask;
    }

    private Task RefreshMenuAsync(ulong guildId)
        => _menuLocks.RunAsync(guildId, async () =>
        {
            var state = await _store.GetAsync(guildId);
            if (state.MenuChannelId is not { } channelId || state.MenuMessageId is not { } messageId)
            {
                return;
            }

            if (await FetchMenuAsync(channelId, messageId) is not { } menu)
            {
                // Deleted by someone: forget it, so this stops trying (and songs are announced again). /music-menu
                // posts a new one.
                _menus.TryRemove(new KeyValuePair<ulong, (ulong, ulong)>(guildId, (channelId, messageId)));
                await _store.MutateAsync(guildId, s =>
                {
                    if (s.MenuMessageId == messageId)
                    {
                        s.MenuChannelId = null;
                        s.MenuMessageId = null;
                    }
                });
                return;
            }

            var player = _music.GetPlayer(guildId);
            await menu.ModifyAsync(m =>
            {
                m.Embed = BuildMenu(state, player);
                m.Components = BuildButtons(state, player);
            });
        });

    private async Task<IUserMessage?> FetchMenuAsync(ulong channelId, ulong messageId)
    {
        if (_client.GetChannel(channelId) is not ITextChannel channel)
        {
            return null;
        }

        try
        {
            return await channel.GetMessageAsync(messageId) as IUserMessage;
        }
        catch (HttpException ex)
        {
            _logger.LogDebug(ex, "Couldn't fetch the music menu {MessageId}.", messageId);
            return null;
        }
    }

    private Embed BuildMenu(MusicQueueState state, MusicPlayer? player)
    {
        // Short: the rules are read once, and the menu is what everyone keeps looking at.
        var embed = new EmbedBuilder()
            .WithColor(Colors.Music)
            .WithTitle(MenuTitle)
            .WithFooter($"One song each in turn · up to {SongsPerPerson} waiting each · skipping and stopping take " +
                        "votes from the listeners");

        // Looping and the volume show plainly (both are saved, so they can still be set from last time); the Loop and
        // 🔉 🔊 buttons change them.
        var volume = state.VolumePercent;
        var status = LoopOf(state, player) switch
        {
            LoopMode.Song => "\n🔂 Looping this song.",
            LoopMode.Queue => "\n🔁 Looping the queue: finished songs go back in.",
            _ => "",
        } + (volume == 100 ? "" : $"\n{(volume < 100 ? "🔉" : "🔊")} Volume {volume}%.");

        if (player?.CurrentItem is { } current)
        {
            embed.WithDescription((player.State == PlayerState.Paused ? "⏸️ Paused" : "▶️ Playing") +
                                  $" in <#{player.VoiceChannelId}>.{status}");
            var song = (current as QueueTrackItem)?.Song;
            embed.AddField("Now playing", MusicFormatting.TrackLink(current.Track) +
                                          $" ({MusicFormatting.Length(current.Track!)})" +
                                          (song is null ? "" : $" - <@{song.PickerId}>'s pick") +
                                          VotesLine(_votes, state.GuildId, song?.Id, "⏭️", "skip") +
                                          VotesLine(_stopVotes, state.GuildId, song?.Id, "⏹️", "stop"));
        }
        else
        {
            var left = _leftVoiceAt.TryGetValue(state.GuildId, out var at)
                ? $" (left voice <t:{at.ToUnixTimeSeconds()}:R>)"
                : "";
            embed.WithDescription($"Not playing right now{left}. Add songs any time - they wait here. When you're " +
                                  $"ready, join a voice channel and press **Play**.{status}");
        }

        if (_menuNotes.TryGetValue(state.GuildId, out var note))
        {
            // Dated with Discord's relative time ("2 hours ago"), kept even when a long note is cut to a field's limit.
            var when = $" <t:{note.At.ToUnixTimeSeconds()}:R>";
            var room = 1024 - when.Length;
            embed.AddField("Latest", (note.Text.Length <= room ? note.Text : note.Text[..(room - 1)] + "…") + when);
        }

        var order = MusicQueueRules.Order(state);
        // How long it all lasts (live streams can't be timed, so then it isn't shown).
        var total = order.Any(s => s.IsLiveStream)
            ? ""
            : $" (~{MusicFormatting.RoughWait(order.Aggregate(TimeSpan.Zero, (sum, s) => sum + s.Duration))})";
        embed.AddField(order.Count == 0 ? "Up next" : $"Up next{total}", order.Count == 0
            ? "*Nothing yet - press **Add song**, use `/play`, or ask me.*"
            : FitField(order.Take(10).Select((s, i) => $"`{i + 1}.` {MusicFormatting.SongLink(s)} - <@{s.PickerId}>"),
                order.Count, "\n"));

        var turns = state.Participants.Where(p => state.Songs.Any(s => s.PickerId == p)).ToList();
        if (turns.Count > 0)
        {
            embed.AddField("Taking turns", FitField(turns.Select(p => $"<@{p}>"), turns.Count, " → "));
        }

        return embed.Build();
    }

    /// <summary>"\n⏭️ 2 votes to skip" for the menu, when the song playing has any; otherwise "".</summary>
    private static string VotesLine(ConcurrentDictionary<ulong, Votes> all, ulong guildId, Guid? songId, string icon,
        string what)
    {
        var count = all.TryGetValue(guildId, out var votes) && votes.SongId == songId ? votes.Voters.Count : 0;
        return count > 0 ? $"\n{icon} {count} vote{(count == 1 ? "" : "s")} to {what}" : "";
    }

    /// <summary>What's looping: the song (the player repeats it) or the queue (saved, so it lasts between sessions).
    /// </summary>
    public static LoopMode LoopOf(MusicQueueState state, MusicPlayer? player)
        => player?.RepeatMode == Lavalink4NET.Players.Queued.TrackRepeatMode.Track ? LoopMode.Song
            : state.LoopQueue ? LoopMode.Queue
            : LoopMode.Off;

    private static MessageComponent BuildButtons(MusicQueueState state, MusicPlayer? player)
    {
        var builder = new ComponentBuilder()
            .WithButton("Add song", AddButton, ButtonStyle.Success, new Emoji("➕"));

        if (player?.CurrentItem is null)
        {
            builder.WithButton("Play", PlayButton, ButtonStyle.Primary, new Emoji("▶️"));
        }
        else
        {
            builder.WithButton("Back", BackButton, ButtonStyle.Secondary, new Emoji("⏮️"));
            builder.WithButton("Vote skip", SkipButton, ButtonStyle.Primary, new Emoji("⏭️"));
            builder = player.State == PlayerState.Paused
                ? builder.WithButton("Resume", ResumeButton, ButtonStyle.Secondary, new Emoji("▶️"))
                : builder.WithButton("Pause", PauseButton, ButtonStyle.Secondary, new Emoji("⏸️"));
            builder.WithButton("Stop", StopButton, ButtonStyle.Danger, new Emoji("⏹️"));
        }

        // A second row: five buttons (a row's most) fill the first while the music plays.
        var loop = LoopOf(state, player);
        return builder
            .WithButton($"Loop: {loop switch { LoopMode.Song => "song", LoopMode.Queue => "queue", _ => "off" }}",
                LoopButton, loop == LoopMode.Off ? ButtonStyle.Secondary : ButtonStyle.Primary,
                new Emoji(loop == LoopMode.Song ? "🔂" : "🔁"), row: 1)
            .WithButton(null, VolumeDownButton, ButtonStyle.Secondary, new Emoji("🔉"), row: 1)
            .WithButton(null, VolumeUpButton, ButtonStyle.Secondary, new Emoji("🔊"), row: 1)
            .WithButton("History", HistoryButton, ButtonStyle.Secondary, new Emoji("🕘"), row: 1)
            .WithButton("Remove mine", MineButton, ButtonStyle.Secondary, new Emoji("🗑️"), row: 1)
            .Build();
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

    private async Task TryUnpinAsync(IUserMessage menu)
    {
        try
        {
            await menu.UnpinAsync();
        }
        catch (HttpException ex)
        {
            _logger.LogDebug(ex, "Couldn't unpin an old music menu in channel {ChannelId}.", menu.Channel.Id);
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Known before the bot connects, so songs aren't announced in servers that have a menu.
        foreach (var state in await _store.GetAllAsync())
        {
            if (state is { MenuChannelId: { } channelId, MenuMessageId: { } messageId })
            {
                _menus[state.GuildId] = (channelId, messageId);
            }
        }

        _client.MessageReceived += OnMessageReceivedAsync;

        // After a restart the menus still say what was playing before; set them straight once the bot is back.
        _client.Ready += () =>
        {
            if (Interlocked.Exchange(ref _menusRestored, 1) == 0)
            {
                foreach (var guildId in _menus.Keys)
                {
                    RefreshMenu(guildId);
                }
            }

            return Task.CompletedTask;
        };
    }

    /// <summary>Keeps the menu at the bottom of its channel: messages posted under it get it reposted below them,
    /// once the channel's had a quiet moment (see <see cref="RepostWhenQuietFor"/>).</summary>
    private Task OnMessageReceivedAsync(SocketMessage message)
    {
        if (message.Channel is not SocketTextChannel channel ||
            !_menus.TryGetValue(channel.Guild.Id, out var menu) || menu.ChannelId != channel.Id ||
            message.Id == menu.MessageId)
        {
            return Task.CompletedTask;
        }

        var fromBot = message.Author.Id == _client.CurrentUser.Id;

        // Discord's "pinned a message" notice for the menu: tidy it away (needs Manage Messages), and either way it
        // doesn't count as burying the menu, or every repost would set off another.
        if (message.Type == MessageType.ChannelPinnedMessage)
        {
            if (fromBot && channel.Guild.CurrentUser.GetPermissions(channel).ManageMessages)
            {
                Background.Run(_logger, () => message.DeleteAsync(), LogLevel.Debug,
                    "Couldn't delete the pin notice for the music menu.");
            }

            return Task.CompletedTask;
        }

        // A new copy of the menu itself (reposting) isn't something to repost over.
        if (!_options.KeepMenuAtBottom || fromBot && message.Embeds.Any(e => e.Title == MenuTitle))
        {
            return Task.CompletedTask;
        }

        ScheduleRepost(channel);
        return Task.CompletedTask;
    }

    private void ScheduleRepost(ITextChannel channel)
    {
        var timer = _reposts.GetOrAdd(channel.GuildId, _ => new RepostTimer());
        CancellationTokenSource pending;
        TimeSpan delay;
        lock (timer)
        {
            var now = DateTimeOffset.UtcNow;
            timer.BuriedSince ??= now;

            // After a quiet spell, but not later than RepostWithin of it first being buried, nor sooner than a
            // minute after the last repost.
            var when = Min(now + RepostWhenQuietFor, timer.BuriedSince.Value + RepostWithin);
            when = Max(when, timer.LastRepost + RepostAtMostEvery);
            delay = Max(when - now, TimeSpan.Zero);

            timer.Pending?.Cancel();
            timer.Pending?.Dispose();
            timer.Pending = pending = new CancellationTokenSource();
        }

        Background.Run(_logger, async () =>
        {
            try
            {
                await Task.Delay(delay, pending.Token);
            }
            catch (OperationCanceledException)
            {
                return; // a newer message pushed it back
            }

            lock (timer)
            {
                if (timer.Pending != pending)
                {
                    return;
                }

                timer.Pending = null;
                timer.BuriedSince = null;
                timer.LastRepost = DateTimeOffset.UtcNow;
            }

            // Only if this is still where the menu lives (an admin may have moved it meanwhile), and only while the
            // server has music on: with it off, the menu's buttons don't work, so it shouldn't keep jumping back down.
            if (_menus.TryGetValue(channel.GuildId, out var menu) && menu.ChannelId == channel.Id &&
                await _serverSettings.IsEnabledAsync(channel.GuildId, ServerFeature.Music))
            {
                var (_, error) = await PostMenuAsync(channel);
                if (error is null)
                {
                    _logger.LogInformation("Moved the music menu back to the bottom of #{Channel} in guild {GuildId}.",
                        channel.Name, channel.GuildId);
                }
            }
        }, LogLevel.Warning, "Couldn't move the music menu to the bottom of channel {ChannelId}.", channel.Id);
    }

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    private sealed class RepostTimer
    {
        public DateTimeOffset LastRepost { get; set; }

        public DateTimeOffset? BuriedSince { get; set; }

        public CancellationTokenSource? Pending { get; set; }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private sealed class Votes
    {
        public Guid? SongId { get; set; }

        public HashSet<ulong> Voters { get; } = [];
    }
}
