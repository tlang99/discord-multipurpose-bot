using Discord;
using Discord.WebSocket;
using ModBot.Common;
using ModBot.Models;
using ModBot.Services.Settings;
using ModBot.Services.Storage;

namespace ModBot.Modules.Music;

/// <summary>
/// What people tell DJ mode they like, as the person asking: liking the song that's playing, their liked songs, and
/// their taste in their own words. Shared by the slash commands, the menu's Like button and the chatbot (through
/// <see cref="MusicControls"/>). Anyone can like and set a taste, from anywhere: it's theirs, and DJ mode only goes by
/// it while they're listening. The replies are for the person alone.
/// </summary>
public class DjTastes
{
    private readonly IMusicQueueStore _store;
    private readonly MusicService _music;
    private readonly MusicQueueService _queue;

    public DjTastes(IMusicQueueStore store, MusicService music, MusicQueueService queue)
    {
        _store = store;
        _music = music;
        _queue = queue;
    }

    /// <summary>Likes the song playing for <paramref name="user"/> (<paramref name="like"/> true), unlikes it (false),
    /// or whichever it isn't yet (null: the Like button and /like).</summary>
    public async Task<MusicReply> LikeAsync(SocketGuildUser user, bool? like = null)
    {
        if (_music.GetPlayer(user.Guild.Id)?.CurrentItem is not QueueTrackItem { Song: var song })
        {
            return MusicReply.Fail("Nothing's playing right now. See the songs you've liked with `/likes`.");
        }

        if (song.IsLiveStream)
        {
            return MusicReply.Fail("This one's a live stream, so it can't be liked: DJ mode only plays songs.");
        }

        var (was, now, dropped) = await _store.MutateAsync(user.Guild.Id, state =>
        {
            var liked = DjRules.IsLiked(state, user.Id, song);
            if ((like ?? !liked) == liked)
            {
                return (liked, liked, (QueuedSong?)null);
            }

            if (liked)
            {
                DjRules.Unlike(state, user.Id, s => DjRules.SameSong(s, song));
                return (true, false, null);
            }

            return (false, true, DjRules.Like(state, user.Id, MusicQueueRules.Copy(song, user.Id, user.DisplayName)));
        });

        var link = MusicFormatting.SongLink(song);
        if (was == now)
        {
            return MusicReply.Fail(now ? $"{link} is already in your liked songs." : $"{link} isn't in your liked songs.");
        }

        if (!now)
        {
            return Private($"💔 Took {link} out of your liked songs.");
        }

        return Private($"❤️ Liked {link}. While you're listening, DJ mode may play it again and picks songs like it." +
                       (dropped is null
                           ? ""
                           : $" (You've liked {DjRules.MostLikes}, the most I keep, so your oldest, " +
                             $"{MusicFormatting.SongLink(dropped)}, made room.)") +
                       await DjOffNoteAsync(user.Guild.Id));
    }

    /// <summary>Unlikes one of <paramref name="user"/>'s liked songs, by its ID (from the /likes list).</summary>
    public async Task<MusicReply> UnlikeAsync(SocketGuildUser user, Guid songId)
    {
        var song = await _store.MutateAsync(user.Guild.Id, state => DjRules.Unlike(state, user.Id, s => s.Id == songId));
        return song is null
            ? MusicReply.Fail("That song isn't in your liked songs any more.")
            : Private($"💔 Took {MusicFormatting.SongLink(song)} out of your liked songs.");
    }

    /// <summary><paramref name="userId"/>'s liked songs, newest first.</summary>
    public async Task<IReadOnlyList<QueuedSong>> LikedAsync(ulong guildId, ulong userId)
        => DjRules.TasteOf(await _store.GetAsync(guildId), userId)?.Liked.ToList() ?? [];

    /// <summary><paramref name="user"/>'s liked songs and taste, as a list to read.</summary>
    public async Task<MusicReply> ShowAsync(SocketGuildUser user)
    {
        var taste = DjRules.TasteOf(await _store.GetAsync(user.Guild.Id), user.Id);
        if (taste is null)
        {
            return MusicReply.Fail("You haven't told DJ mode what you like yet. Press **Like** on the music menu while " +
                                   "a song plays, or tell it your taste with `/taste` (or \"my music taste is 90s hip " +
                                   "hop, no country\").");
        }

        var said = taste.Taste.Length == 0
            ? "*No taste saved: tell it with `/taste`.*"
            : $"**Your taste:** {TextTools.Escape(taste.Taste)}";
        var liked = taste.Liked.Count == 0
            ? "*No liked songs yet.*"
            : string.Join('\n', taste.Liked.Select((s, i) => $"`{i + 1}.` {MusicFormatting.SongLink(s)}"));
        var embed = new EmbedBuilder()
            .WithColor(Colors.Music)
            .WithTitle("What you like, for DJ mode")
            .WithDescription(said + "\n\n" + liked)
            .WithFooter("DJ mode goes by these while you're listening · /likes to unlike one")
            .Build();
        return new MusicReply(true, null, embed, Announce: false);
    }

    /// <summary><paramref name="userId"/>'s taste in their own words, or blank.</summary>
    public async Task<string> TasteAsync(ulong guildId, ulong userId)
        => DjRules.TasteOf(await _store.GetAsync(guildId), userId)?.Taste ?? "";

    /// <summary>Saves <paramref name="user"/>'s taste in their own words, or forgets it when blank.</summary>
    public async Task<MusicReply> SetTasteAsync(SocketGuildUser user, string? taste)
    {
        var saved = await _store.MutateAsync(user.Guild.Id, state => DjRules.SetTaste(state, user.Id, taste));
        return saved.Length == 0
            ? Private("🎧 Forgot your music taste. DJ mode still goes by the songs you've liked.")
            : Private($"🎧 Saved. While you're listening, DJ mode leans towards: {TextTools.Escape(saved)}" +
                      await DjOffNoteAsync(user.Guild.Id));
    }

    /// <summary>A note for when the server has DJ mode off, so nobody wonders why nothing changed.</summary>
    private async Task<string> DjOffNoteAsync(ulong guildId)
        => await _queue.IsDjOnAsync(guildId)
            ? ""
            : "\n-# DJ mode is off on this server, so this waits until a server admin turns it on with `/settings feature`.";

    /// <summary>Worked, but only for the person: never posted to the whole channel from a button.</summary>
    private static MusicReply Private(string text) => MusicReply.Ok(text) with { Announce = false };
}
