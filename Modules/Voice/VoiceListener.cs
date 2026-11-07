using System.Collections.Concurrent;
using System.Diagnostics;
using Discord;
using Discord.Audio;
using Discord.WebSocket;
using Lavalink4NET;
using Lavalink4NET.Players;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModBot.Common;
using ModBot.Configuration;
using ModBot.EventHandlers;
using ModBot.Models;
using ModBot.Modules.Music;
using ModBot.Services.Chat;
using ModBot.Services.Moderation;
using ModBot.Services.Settings;
using ModBot.Services.Voice;

namespace ModBot.Modules.Voice;

/// <summary>
/// Voice commands: sits in a voice channel, turns what people say into text on this machine, and hands
/// anything said after its name ("hey ModBot, skip this song") to the chatbot as that person's request, so it's done
/// exactly like the same words typed as an @mention, and reads the answer aloud (Piper). Everything else is thrown
/// away as soon as it's checked, and no audio is ever kept.
/// </summary>
/// <remarks>
/// <para>One voice connection per server is all Discord allows, and music (Lavalink) needs it to play, so the bot
/// listens only while no music is playing: starting the music stops the listening (<see cref="MusicService"/> calls
/// <see cref="StopAsync"/>), and it won't start listening over music.</para>
/// <para>It posts one short "listening" line when it joins, and nothing else about itself: no per-person heads-up,
/// no "stopped" notices. The chime when it hears its name is the other sign.</para>
/// <para>Voice auto-join (opt-in, and only with voice commands on too): the bot joins by itself when people
/// are in a voice channel, the busiest one if several are (<see cref="AutoJoinPicker"/>), and comes back when the
/// music's done. Told to stop listening, or taken out of the channel, it stays out of that channel until everyone's
/// left it.</para>
/// </remarks>
public sealed class VoiceListener
{
    /// <summary>Said just the wake word, then paused: their next sentence counts as the request, within this long.</summary>
    private static readonly TimeSpan WakeWordWait = TimeSpan.FromSeconds(8);

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);

    /// <summary>How long a dropped voice connection has to come back before listening ends.</summary>
    private static readonly TimeSpan ReconnectGrace = TimeSpan.FromSeconds(15);

    /// <summary>Sentences waiting to be turned into text, at most. Past this, new ones are dropped rather than queued:
    /// noisy mics shouldn't build a backlog that holds audio in memory and delays every answer.</summary>
    private const int MaxWaitingSpeech = 4;

    /// <summary>Of those, at most this many from one person, so one noisy mic leaves room for everyone else.</summary>
    private const int MaxWaitingSpeechEach = 2;

    /// <summary>Shorter than this is a cough or a click, not a sentence.</summary>
    private static readonly TimeSpan ShortestSpeech = TimeSpan.FromMilliseconds(400);

    /// <summary>How long after someone joins voice before the bot joins them: people hopping straight back out, or
    /// several arriving at once, then cause one join (or none) instead of a flurry.</summary>
    private static readonly TimeSpan AutoJoinDelay = TimeSpan.FromSeconds(3);

    /// <summary>How long after the bot leaves voice (music done, a restart) before it looks for people to join.</summary>
    private static readonly TimeSpan AfterLeavingDelay = TimeSpan.FromSeconds(5);

    /// <summary>After a join by itself fails, it doesn't try again in that server for this long.</summary>
    private static readonly TimeSpan AfterFailedJoin = TimeSpan.FromMinutes(1);

    /// <summary>Made on first use, so a problem making it can only ever break the chime, never the listener.</summary>
    private static readonly Lazy<byte[]> ChimeSound = new(VoiceAudio.Chime);

    private static readonly Lazy<byte[]> ExitChimeSound = new(VoiceAudio.ExitChime);

    /// <summary>After the bot stops talking, it goes on ignoring what it hears for this long: its own voice can still be
    /// coming back through someone's mic.</summary>
    private static readonly TimeSpan EchoTail = TimeSpan.FromMilliseconds(500);

    private readonly DiscordSocketClient _client;
    private readonly SpeechToText _speech;
    private readonly TextToSpeech _voiceOut;
    private readonly IAudioService _audio;
    private readonly Lazy<ChatHandler> _chat;
    private readonly IChatService _model;
    private readonly ServerSettingsService _settings;
    private readonly ChatOptions _chatOptions;
    private readonly VoiceOptions _options;
    private readonly ILogger<VoiceListener> _logger;
    private readonly ConcurrentDictionary<ulong, Session> _sessions = new();
    private readonly ConcurrentDictionary<ulong, bool> _joining = new();
    private readonly KeyedLock<ulong> _startStop = new();

    /// <summary>Per server, the voice channel someone told it to stop listening in (or took it out of): it doesn't
    /// join that one by itself until everyone's left it.</summary>
    private readonly ConcurrentDictionary<ulong, ulong> _stayOut = new();

    private readonly ConcurrentDictionary<(ulong GuildId, TimeSpan Delay), bool> _autoJoinPending = new();
    private readonly ConcurrentDictionary<ulong, DateTimeOffset> _failedJoins = new();

    /// <summary>Per server, how many music joins are under way (two /play at once is two): auto-join keeps off the
    /// connection while any are.</summary>
    private readonly ConcurrentDictionary<ulong, int> _musicStarting = new();

    public VoiceListener(DiscordSocketClient client, SpeechToText speech, TextToSpeech voiceOut, IAudioService audio,
        Lazy<ChatHandler> chat, IChatService model, ServerSettingsService settings, IOptions<ChatOptions> chatOptions,
        IOptions<VoiceOptions> options, ILogger<VoiceListener> logger)
    {
        _client = client;
        _speech = speech;
        _voiceOut = voiceOut;
        _audio = audio;
        _chat = chat;
        _model = model;
        _settings = settings;
        _chatOptions = chatOptions.Value;
        _options = options.Value;
        _logger = logger;
        _client.UserVoiceStateUpdated += OnVoiceStateUpdatedAsync;
        // On connecting (and reconnecting), join anyone already sitting in voice.
        _client.GuildAvailable += guild =>
        {
            Background.Run(_logger, () => ClearLeftoverStatusesAsync(guild), LogLevel.Debug,
                "Couldn't clear leftover voice statuses in guild {GuildId}.", guild.Id);
            ScheduleAutoJoin(guild.Id, AfterLeavingDelay);
            return Task.CompletedTask;
        };
    }

    /// <summary>Whether voice commands can work on this bot at all; null if they can, else why not.</summary>
    public string? SetupProblem => _speech.Problem;

    /// <summary>Listening, or joining to listen. Music checks this before it takes the voice connection, so it counts
    /// while joining too; <see cref="StopAsync"/> then waits for the join to finish before ending it.</summary>
    public bool IsListening(ulong guildId) => _sessions.ContainsKey(guildId) || _joining.ContainsKey(guildId);

    /// <summary>
    /// Joins <paramref name="user"/>'s voice channel and starts listening. Returns what to tell the person who asked.
    /// </summary>
    /// <param name="askedIn">Where they asked; replies to voice requests go here when the bot can't post in the voice
    /// channel's own chat.</param>
    public Task<string> StartAsync(SocketGuildUser user, SocketTextChannel askedIn)
        => _startStop.RunAsync(user.Guild.Id, async () =>
        {
            var guild = user.Guild;
            if (SetupProblem is { } problem)
            {
                return $"Voice commands aren't set up on this bot yet. For whoever runs it: {problem}";
            }

            // Every voice request goes through the chatbot, so without it the bot would listen and do nothing.
            if (!_chatOptions.IsEnabled)
            {
                return "Voice commands go through the chatbot, which is turned off in the bot's own settings.";
            }

            if (!await _settings.IsEnabledAsync(guild.Id, ServerFeature.Chat))
            {
                return "Voice commands go through the chatbot, which is off on this server. A server admin can turn " +
                       "it on with `/settings feature`.";
            }

            if (user.VoiceChannel is not { } channel)
            {
                return "Join a voice channel first, then ask me again.";
            }

            if (_sessions.TryGetValue(guild.Id, out var existing))
            {
                return existing.Channel.Id == channel.Id
                    ? $"I'm already listening in {channel.Mention}."
                    : $"I'm already listening in {existing.Channel.Mention} - stop me there first.";
            }

            if (MusicIsPlaying(guild.Id))
            {
                return "I can't listen while music's playing (one voice connection does both, and music has it). " +
                       "Stop the music first, or ask me again when it's over.";
            }

            if (!ChannelAccess.CanListenIn(guild.CurrentUser, channel))
            {
                return $"I need **View Channel** and **Connect** in {channel.Mention} to listen there.";
            }

            // Replies go in the voice channel's own chat, where the people talking will see them, or else where they
            // asked. Only a channel the asker can post in too: a spoken request is only run where the speaker could
            // have typed it (ChatHandler), so anywhere else every request would be ignored without a word.
            bool BothCanPost(SocketTextChannel c) =>
                ChannelAccess.CanPost(guild.CurrentUser, c) && ChannelAccess.CanPost(user, c);
            var replyChannel = BothCanPost(channel) ? channel : BothCanPost(askedIn) ? askedIn : null;
            if (replyChannel is null)
            {
                return $"Neither {channel.Mention}'s chat nor this channel is somewhere we can both post, so I'd " +
                       "have nowhere to answer. Ask in a channel you can post in.";
            }

            if (await JoinHoldingLockAsync(guild, channel, replyChannel, user) is { } failed)
            {
                return failed;
            }

            // Asked for by name, so it's welcome back in a channel it was told to stay out of.
            _stayOut.TryRemove(guild.Id, out _);
            // The listening line already went where they asked, when that's where replies go.
            return replyChannel.Id == askedIn.Id
                ? ""
                : $"🎙️ Listening in {channel.Mention} - my replies go in its chat.";
        });

    /// <summary>
    /// Joins <paramref name="channel"/> and starts listening: the part shared by being asked to and joining by itself
    /// (<paramref name="startedBy"/> null). Call holding <see cref="_startStop"/>. Returns why it couldn't, or null.
    /// </summary>
    private async Task<string?> JoinHoldingLockAsync(SocketGuild guild, SocketVoiceChannel channel,
        SocketTextChannel replyChannel, SocketGuildUser? startedBy)
    {
        IAudioClient voice;
        // The chime and spoken replies need the Speak permission, and an unmuted bot: muted, it sends no sound at all.
        var canPlay = (_options.Chime || _voiceOut.Problem is null) && CanSpeakIn(channel);
        var session = new Session(guild, channel, replyChannel, autoJoined: startedBy is null, canPlay,
            ConversationLength);
        _joining[guild.Id] = true;
        try
        {
            // Muted unless it makes a sound (the chime, spoken replies), but not deafened: a deafened bot is sent no
            // audio.
            var connecting = channel.ConnectAsync(selfDeaf: false, selfMute: !canPlay);
            if (await Task.WhenAny(connecting, Task.Delay(ConnectTimeout)) != connecting)
            {
                await LeaveAsync(channel);
                // The join can still finish later: leave then too, or the bot would sit there without listening.
                Background.Run(_logger, async () =>
                {
                    await connecting;
                    await _startStop.RunAsync(guild.Id, async () =>
                    {
                        // Not if music has taken the connection meanwhile: leaving would stop it.
                        if (!_sessions.ContainsKey(guild.Id) && !MusicIsPlaying(guild.Id))
                        {
                            await LeaveAsync(channel);
                        }
                    });
                }, LogLevel.Debug, "A timed-out voice join failed in guild {GuildId}.", guild.Id);
                NoteFailedJoin(guild.Id, startedBy);
                return $"Joining {channel.Mention} timed out - try again in a moment.";
            }

            voice = await connecting;
            session.Voice = voice;
            _sessions[guild.Id] = session;
            _failedJoins.TryRemove(guild.Id, out _);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Couldn't join voice channel {ChannelId} to listen.", channel.Id);
            NoteFailedJoin(guild.Id, startedBy);
            await LeaveAsync(channel);
            return $"I couldn't join {channel.Mention} - try again in a moment.";
        }
        finally
        {
            _joining.TryRemove(guild.Id, out _);
        }

        voice.StreamCreated += (userId, stream) =>
        {
            Listen(session, userId, stream);
            return Task.CompletedTask;
        };
        voice.StreamDestroyed += userId =>
        {
            session.StopReading(userId);
            return Task.CompletedTask;
        };
        voice.Disconnected += _ =>
        {
            // Discord.Net reconnects after a blip, so only a connection that stays down ends it. (Being kicked
            // or moved is seen sooner, in OnVoiceStateUpdatedAsync.)
            Background.Run(_logger, async () =>
            {
                try
                {
                    await Task.Delay(ReconnectGrace, session.Stopping.Token);
                }
                catch (OperationCanceledException)
                {
                    return; // stopped listening anyway
                }

                if (voice.ConnectionState != ConnectionState.Connected)
                {
                    _logger.LogInformation("Lost the voice connection in guild {GuildId}.", guild.Id);
                    // Counts as a failed join, so a connection that keeps dropping isn't rejoined every few seconds.
                    NoteFailedJoin(guild.Id, startedBy: null);
                    await EndAsync(guild.Id, session);
                }
            }, LogLevel.Debug, "Couldn't check the voice connection in guild {GuildId}.", guild.Id);
            return Task.CompletedTask;
        };
        foreach (var member in channel.ConnectedUsers)
        {
            Listen(session, member.Id, member.AudioStream);
        }

        // Joined by itself, it stays while people are there: a time limit would only see it rejoin straight away.
        if (_options.MaxListenMinutes > 0 && startedBy is not null)
        {
            Background.Run(_logger, async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(_options.MaxListenMinutes), session.Stopping.Token);
                }
                catch (OperationCanceledException)
                {
                    return; // stopped before the time limit, the usual case
                }

                // Staying out, or with voice auto-join on it would rejoin at once and the limit would mean nothing.
                await EndAsync(guild.Id, session, stayOut: true);
            }, LogLevel.Debug, "Voice time limit failed in guild {GuildId}.", guild.Id);
        }

        _logger.LogInformation("Listening for voice commands in {Channel} ({ChannelId}), {StartedBy}.",
            channel.Name, channel.Id,
            startedBy is null ? "joined by itself" : $"started by {startedBy.Username} ({startedBy.Id})");

        // One short line, so whoever's in the channel knows it's listening. (No heads-up for each person who joins,
        // and nothing when it stops: those were just noise.)
        var name = guild.CurrentUser.DisplayName;
        try
        {
            await replyChannel.SendMessageAsync(ListeningLine(name, _options.LogRequests),
                allowedMentions: AllowedMentions.None);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Couldn't post the listening line in channel {ChannelId}.", replyChannel.Id);
        }

        await SetListeningStatusAsync(session);
        return null;
    }

    /// <summary>The one line it posts on joining: that it's listening, what happens to what's said, and how to stop
    /// it.</summary>
    /// <param name="logsRequests">Voice:LogRequests is on: what's asked is written to the bot's log, and people
    /// should know.</param>
    public static string ListeningLine(string name, bool logsRequests = false)
        => $"🎙️ Listening for \"hey {name}\". Speech is turned into text on the bot's own computer, only what's said " +
           $"after my name (or in reply, right after I answer) is used, and no audio is kept." +
           (logsRequests ? " What you ask me is written to the bot's log, to fix what I mishear." : "") +
           $" \"Hey {name}, stop listening\" sends me away.";

    /// <summary>The voice channel status while it listens: shown under the channel's name in the channel list, so
    /// anyone joining later sees it without a post or a ping.</summary>
    private static string ListeningStatus(string name) => $"🎙️ Listening for \"hey {name}\"";

    /// <summary>Sets the channel's status to <see cref="ListeningStatus"/>, keeping what it was to put back after.
    /// Needs Set Voice Channel Status; without it, it's just skipped.</summary>
    private async Task SetListeningStatusAsync(Session session)
    {
        var channel = session.Channel;
        if (!session.Guild.CurrentUser.GetPermissions(channel).SetVoiceChannelStatus)
        {
            return;
        }

        var ours = ListeningStatus(session.Guild.CurrentUser.DisplayName);
        // A status that's already ours was left by a bot that stopped without clearing it: nothing to put back.
        session.StatusBefore = channel.Status == ours ? "" : channel.Status ?? "";
        try
        {
            await channel.SetStatusAsync(ours);
            session.OurStatus = ours;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Couldn't set the status of voice channel {ChannelId}.", channel.Id);
        }
    }

    /// <summary>Puts the channel's status back to what it was before it started listening, unless someone changed it
    /// meanwhile (then theirs stays).</summary>
    private async Task RestoreStatusAsync(Session session)
    {
        var channel = session.Channel;
        // The cached status can lag behind Discord's, so ours, the old one or none all count as "not changed since".
        if (session.OurStatus is not { } ours ||
            channel.Status is { Length: > 0 } now && now != ours && now != session.StatusBefore)
        {
            return;
        }

        try
        {
            await channel.SetStatusAsync(session.StatusBefore);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Couldn't put back the status of voice channel {ChannelId}.", channel.Id);
        }
    }

    /// <summary>On connecting: clears a listening status left on a channel by a bot that stopped without clearing it
    /// (a crash, a power cut).</summary>
    private async Task ClearLeftoverStatusesAsync(SocketGuild guild)
    {
        if (_sessions.ContainsKey(guild.Id))
        {
            return; // reconnected mid-session: that status is current
        }

        var ours = ListeningStatus(guild.CurrentUser.DisplayName);
        foreach (var channel in guild.VoiceChannels.Where(c => c.Status == ours))
        {
            try
            {
                await channel.SetStatusAsync("");
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Couldn't clear the leftover status of voice channel {ChannelId}.", channel.Id);
            }
        }
    }

    /// <summary>Looks for people to join in <paramref name="guildId"/> after <paramref name="delay"/>, if the server
    /// has voice auto-join on. Several calls while one's waiting make just the one look.</summary>
    public void ScheduleAutoJoin(ulong guildId, TimeSpan delay)
    {
        // Same server and same wait: one look. A different wait gets its own, so a quick look (music done, a setting
        // changed) isn't swallowed by a slow one waiting (the retry after a failed join). Each look is cheap.
        var key = (guildId, delay);
        if (!_autoJoinPending.TryAdd(key, true))
        {
            return;
        }

        Background.Run(_logger, async () =>
        {
            try
            {
                await Task.Delay(delay);
            }
            finally
            {
                _autoJoinPending.TryRemove(key, out _);
            }

            await _startStop.RunAsync(guildId, () => AutoJoinHoldingLockAsync(guildId));
        }, LogLevel.Warning, "Voice auto-join failed in guild {GuildId}.", guildId);
    }

    /// <summary>Soon after a setting changes (an admin turning voice auto-join or voice commands on, or picking its
    /// channel): joins anyone already in voice.</summary>
    public void AutoJoinSoon(ulong guildId) => ScheduleAutoJoin(guildId, TimeSpan.FromSeconds(1));

    private async Task AutoJoinHoldingLockAsync(ulong guildId)
    {
        if (_client.GetGuild(guildId) is not { } guild || _sessions.ContainsKey(guildId) || SetupProblem is not null ||
            !_chatOptions.IsEnabled || MusicIsPlaying(guildId) || _musicStarting.ContainsKey(guildId))
        {
            return;
        }

        if (_failedJoins.TryGetValue(guildId, out var failed) && DateTimeOffset.UtcNow - failed < AfterFailedJoin)
        {
            _logger.LogDebug("Not joining voice by itself in guild {GuildId}: the last try failed {Ago} ago.", guildId,
                DateTimeOffset.UtcNow - failed);
            // Look again when the wait's over (this look may have taken the place of the one set for then).
            ScheduleAutoJoin(guildId, AfterFailedJoin - (DateTimeOffset.UtcNow - failed) + TimeSpan.FromSeconds(1));
            return;
        }

        var server = await _settings.GetAsync(guildId);
        if (!server.AutoJoinsVoice)
        {
            return;
        }

        var me = guild.CurrentUser;
        var candidates = guild.VoiceChannels
            .Where(c => c is not SocketStageChannel && c.Id != guild.AFKChannel?.Id && CanJoinByItself(me, c))
            .Select(c => new AutoJoinPicker.Channel(c.Id, c.ConnectedUsers.Count(u => !u.IsBot), c.Position));
        var stayOut = _stayOut.TryGetValue(guildId, out var outOf) ? outOf : (ulong?)null;
        if (stayOut is { } gone && guild.GetVoiceChannel(gone) is null)
        {
            _stayOut.TryRemove(new KeyValuePair<ulong, ulong>(guildId, gone)); // that channel was deleted
            stayOut = null;
        }
        if (AutoJoinPicker.Pick(candidates, server.AutoJoinChannel.ChannelId, stayOut) is not { } id ||
            guild.GetVoiceChannel(id) is not { } channel)
        {
            return;
        }

        // Replies go in the voice channel's own chat, where the people talking see them. Each request still needs its
        // speaker to be able to post there (ChatHandler).
        await JoinHoldingLockAsync(guild, channel, channel, startedBy: null);
    }

    /// <summary>A join by itself failed: it waits a while before trying again in that server. A failed join someone
    /// asked for doesn't count; they were told and can try again.</summary>
    private void NoteFailedJoin(ulong guildId, SocketGuildUser? startedBy)
    {
        if (startedBy is null)
        {
            _failedJoins[guildId] = DateTimeOffset.UtcNow;
            // Try again once the wait is over, even if nobody joins or leaves voice meanwhile.
            ScheduleAutoJoin(guildId, AfterFailedJoin + TimeSpan.FromSeconds(1));
        }
    }

    /// <summary>A channel it may join by itself: allowed in, not full, able to post in its chat (where the replies go)
    /// and to set its status (how people joining later see it's listening).</summary>
    private static bool CanJoinByItself(SocketGuildUser me, SocketVoiceChannel channel)
    {
        return ChannelAccess.CanJoinByItselfIn(me, channel) &&
               (channel.UserLimit is not > 0 || channel.ConnectedUsers.Count < channel.UserLimit ||
                me.GetPermissions(channel).MoveMembers);
    }

    /// <summary>
    /// Music is about to take the voice connection: stops any listening, and keeps voice auto-join off it until
    /// <see cref="MusicJoined"/>. Otherwise a slow music join could lose the connection to the bot rejoining to listen
    /// (its own leave sets auto-join looking, a few seconds later). Every call must be matched by one to
    /// <see cref="MusicJoined"/>, even if this throws.
    /// </summary>
    public async Task MakeWayForMusicAsync(ulong guildId)
    {
        _musicStarting.AddOrUpdate(guildId, 1, (_, n) => n + 1);
        await StopAsync(guildId);
    }

    /// <summary>A music join has finished, or failed. Once none are under way, auto-join can have the connection back
    /// (it does nothing while music has it).</summary>
    public void MusicJoined(ulong guildId)
    {
        if (_musicStarting.AddOrUpdate(guildId, 0, (_, n) => Math.Max(0, n - 1)) == 0)
        {
            _musicStarting.TryRemove(new KeyValuePair<ulong, int>(guildId, 0));
            ScheduleAutoJoin(guildId, AfterLeavingDelay);
        }
    }

    /// <summary>Listening because it joined by itself (not because someone asked).</summary>
    public bool IsAutoListening(ulong guildId) => _sessions.TryGetValue(guildId, out var s) && s.AutoJoined;

    /// <summary>Stops listening when <paramref name="user"/> asks: anyone in the channel, or a moderator. Returns what
    /// to tell them.</summary>
    public async Task<string> StopForAsync(SocketGuildUser user)
    {
        if (!_sessions.TryGetValue(user.Guild.Id, out var session))
        {
            return "I'm not listening anywhere right now.";
        }

        if (user.VoiceChannel?.Id != session.Channel.Id && !Moderators.IsModerator(user))
        {
            return $"Only people in {session.Channel.Mention} (or a moderator) can stop me listening there.";
        }

        // Staying out, or voice auto-join would bring it straight back.
        var autoJoins = (await _settings.GetAsync(user.Guild.Id)).AutoJoinsVoice;
        if (!await StopAsync(user.Guild.Id, stayOut: autoJoins))
        {
            return "I'm not listening anywhere right now.";
        }

        return "🎙️ Stopped listening." +
               (autoJoins ? " I won't join that channel by myself again until everyone's left it." : "");
    }

    /// <summary>Stops listening in the server, if it is, and leaves the voice channel. A join in progress finishes
    /// first (it holds the same lock), so music asking to stop never races one. With voice auto-join on, it comes back
    /// by itself once the music player has left.</summary>
    public Task StopAsync(ulong guildId) => StopAsync(guildId, stayOut: false);

    /// <summary>The same, and it keeps out of that channel until everyone's left it: for stops that voice auto-join
    /// mustn't undo straight away (someone told it to, or it can't post its replies there).</summary>
    public Task StopAndStayOutAsync(ulong guildId) => StopAsync(guildId, stayOut: true);

    private Task<bool> StopAsync(ulong guildId, bool stayOut)
        => _startStop.RunAsync(guildId, () => _sessions.TryGetValue(guildId, out var session)
            ? EndHoldingLockAsync(guildId, session, leave: true, stayOut)
            : Task.FromResult(false));

    private Task EndAsync(ulong guildId, Session session, bool leave = true, bool stayOut = false)
        => _startStop.RunAsync(guildId, () => EndHoldingLockAsync(guildId, session, leave, stayOut));

    /// <summary>Ends <paramref name="session"/> if it's still the current one. Returns whether it was.</summary>
    private async Task<bool> EndHoldingLockAsync(ulong guildId, Session session, bool leave, bool stayOut)
    {
        // Only the session that's still current: a late event from an old one mustn't end a new one.
        if (!_sessions.TryRemove(new KeyValuePair<ulong, Session>(guildId, session)))
        {
            return false;
        }

        // Set before leaving: the bot's own leave is what sets auto-join looking again.
        if (stayOut)
        {
            _stayOut[guildId] = session.Channel.Id;
        }

        session.Stopping.Cancel();
        await session.StopSoundAsync();
        await RestoreStatusAsync(session); // while still in the channel, which setting it needs
        if (leave)
        {
            await LeaveAsync(session.Channel);
        }

        _logger.LogInformation("Stopped listening for voice commands in {Channel} ({ChannelId}).",
            session.Channel.Name, session.Channel.Id);
        return true;
    }

    /// <summary>The bot kicked or moved out of the channel, or everyone (but bots) left it: stop. Someone joining voice,
    /// or the bot leaving it (music done): look for people to join by itself.</summary>
    private Task OnVoiceStateUpdatedAsync(SocketUser user, SocketVoiceState before, SocketVoiceState after)
    {
        if (user is not SocketGuildUser member)
        {
            return Task.CompletedTask;
        }

        var guildId = member.Guild.Id;
        var isMe = member.Id == _client.CurrentUser.Id;
        if (!isMe && !member.IsBot && before.VoiceChannel is { } left && left.Id != after.VoiceChannel?.Id &&
            _stayOut.TryGetValue(guildId, out var outOf) && outOf == left.Id && !left.ConnectedUsers.Any(u => !u.IsBot))
        {
            // Everyone's left the channel it was told to stay out of: next time, it's a new group.
            _stayOut.TryRemove(new KeyValuePair<ulong, ulong>(guildId, left.Id));
        }

        if (!_sessions.TryGetValue(guildId, out var session))
        {
            if (isMe ? after.VoiceChannel is null
                    : !member.IsBot && after.VoiceChannel is not null && after.VoiceChannel.Id != before.VoiceChannel?.Id)
            {
                ScheduleAutoJoin(guildId, isMe ? AfterLeavingDelay : AutoJoinDelay);
            }

            return Task.CompletedTask;
        }

        if (isMe)
        {
            // Its own leaving ends the session first, so this is someone else taking it out of the channel. It stays
            // out of there, or voice auto-join would put it straight back. Moved to another channel rather than
            // disconnected, it leaves that one too, instead of sitting there connected but not listening; voice
            // auto-join then picks where to be, which can be the new channel.
            if (after.VoiceChannel?.Id != session.Channel.Id)
            {
                var moved = after.VoiceChannel is not null;
                Background.Run(_logger, async () =>
                {
                    await EndAsync(member.Guild.Id, session, leave: moved, stayOut: true);
                    // Disconnected outright, there's no leave of its own to set auto-join looking: do it here, for
                    // people in other channels.
                    if (!moved)
                    {
                        ScheduleAutoJoin(guildId, AfterLeavingDelay);
                    }
                }, LogLevel.Debug, "Couldn't stop listening in guild {GuildId}.", member.Guild.Id);
            }

            return Task.CompletedTask;
        }

        if (before.VoiceChannel?.Id == session.Channel.Id && after.VoiceChannel?.Id != session.Channel.Id)
        {
            session.StopReading(member.Id);
            if (!session.Channel.ConnectedUsers.Any(u => !u.IsBot))
            {
                Background.Run(_logger,
                    () => EndAsync(member.Guild.Id, session),
                    LogLevel.Debug, "Couldn't stop listening in guild {GuildId}.", member.Guild.Id);
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>Reads one person's audio, sentence by sentence (split where they pause).</summary>
    private void Listen(Session session, ulong userId, AudioInStream? stream)
    {
        if (stream is null || session.Guild.GetUser(userId) is not { IsBot: false })
        {
            return;
        }

        // A new stream replaces the old one (Discord.Net makes new ones when the encryption changes over).
        var reading = session.StartReading(userId);
        var pause = TimeSpan.FromMilliseconds(Math.Clamp(_options.PauseMilliseconds, 200, 5000));
        var longest = (long)(VoiceAudio.DiscordBytesPerSecond * Math.Clamp(_options.MaxSpeechSeconds, 3, 30));
        Background.Run(_logger, async () =>
        {
            var speech = new MemoryStream();
            var lastHeard = DateTime.UtcNow;
            while (!reading.IsCancellationRequested)
            {
                // Audio only arrives while someone's talking, so a gap in it is a pause.
                if (stream.AvailableFrames > 0)
                {
                    var frame = await stream.ReadFrameAsync(reading);
                    // While the bot's talking, what comes in may be its own voice through someone's mic.
                    if (session.IsTalking)
                    {
                        speech.SetLength(0);
                        continue;
                    }

                    speech.Write(frame.Payload);
                    lastHeard = DateTime.UtcNow;
                    session.Conversation.Heard(DateTimeOffset.UtcNow); // don't end a conversation mid-sentence
                    if (speech.Length < longest)
                    {
                        continue;
                    }
                }
                else if (speech.Length == 0 || DateTime.UtcNow - lastHeard < pause)
                {
                    await Task.Delay(50, reading);
                    continue;
                }

                var said = speech.ToArray();
                speech.SetLength(0);
                if (VoiceAudio.DurationOf(said.Length) < ShortestSpeech)
                {
                    continue;
                }

                if (!session.TryQueueSpeech(userId, MaxWaitingSpeech, MaxWaitingSpeechEach))
                {
                    _logger.LogDebug("Dropped a sentence in guild {GuildId}: too many waiting.", session.Guild.Id);
                    continue;
                }

                Background.Run(_logger, async () =>
                {
                    try
                    {
                        await HandleSpeechAsync(session, userId, said);
                    }
                    finally
                    {
                        session.SpeechDone(userId);
                    }
                }, LogLevel.Warning, "Couldn't handle speech in guild {GuildId}.", session.Guild.Id);
            }
        }, LogLevel.Debug, "Stopped reading voice in guild {GuildId}.", session.Guild.Id);
    }

    /// <summary>Turns one sentence into text and, if it was said to the bot (or in a conversation with it), hands it
    /// on as a request.</summary>
    private async Task HandleSpeechAsync(Session session, ulong userId, byte[] said)
    {
        var name = session.Guild.CurrentUser.DisplayName;
        var timer = Stopwatch.StartNew();
        string text;
        try
        {
            text = await _speech.TranscribeAsync(VoiceAudio.ToWhisperSamples(said), SpeechHint(name),
                session.Stopping.Token);
        }
        catch (OperationCanceledException)
        {
            return; // stopped listening while it was being turned into text
        }

        var heardIn = timer.Elapsed;
        if (text.Length == 0 || session.Stopping.IsCancellationRequested || IsHintEcho(text))
        {
            return;
        }

        string[] names = [name, _client.CurrentUser.Username];
        var conversation = session.Conversation;
        var request = WakeWord.FindRequest(text, names);
        var inConversation = conversation.IsOpen;
        if (request is not null)
        {
            // Heard its name (not again for the request that follows a lone "hey ModBot", or mid-conversation, when
            // it's already listening). Only for someone whose requests can be answered: a request is only run where
            // its speaker could have typed it (ChatHandler).
            if (!inConversation && session.Guild.GetUser(userId) is { IsBot: false } caller &&
                ChannelAccess.CanPost(caller, session.ReplyChannel))
            {
                Chime(session);
                if (request.Length == 0)
                {
                    WarmUpModel(session); // the request follows in the next sentence: load the model meanwhile
                }
            }

            if (_options.LogRequests && request.Length == 0)
            {
                // Its name on its own: logged too, to find out what sets off a chime nobody meant.
                _logger.LogInformation("Heard my name from {UserId} in {Channel}, heard as: \"{Text}\"", userId,
                    session.Channel.Name, text);
            }
        }
        else if (inConversation)
        {
            // In a conversation, whatever's said is said to the bot, except its own voice coming back.
            request = WakeWord.Tidy(text);
            if (conversation.IsEcho(request))
            {
                return;
            }
        }
        else
        {
            // Not said to the bot, unless the sentence before was just its name.
            if (!session.TakeWakeWord(userId, WakeWordWait))
            {
                return;
            }

            request = WakeWord.Tidy(text);
        }

        if (request.Length == 0)
        {
            session.HeardWakeWord(userId);
            return;
        }

        if (session.Guild.GetUser(userId) is not { IsBot: false } speaker)
        {
            return;
        }

        // "Thanks", "that's all": the conversation's over, so no answer, just the closing chime.
        if (inConversation && VoiceConversation.IsGoodbye(request, names))
        {
            if (conversation.End())
            {
                ExitChime(session, "someone said goodbye");
            }

            return;
        }

        // Not what was said, unless Voice:LogRequests is on (to work out what it mishears; the join line says so).
        if (_options.LogRequests)
        {
            _logger.LogInformation("Voice request from {User} ({UserId}) in {Channel}, heard as: \"{Request}\"",
                speaker.Username, speaker.Id, session.Channel.Name, request);
        }
        else
        {
            _logger.LogInformation("Voice request from {User} ({UserId}) in {Channel}.", speaker.Username, speaker.Id,
                session.Channel.Name);
        }

        // Answered aloud when it can be, and then not posted too; otherwise in chat, as typed.
        var aloud = session.CanPlay && _voiceOut.Problem is null && CanSpeakIn(session.Channel) &&
                    EveryoneCanRead(session.Channel, session.ReplyChannel);
        // Only a spoken back-and-forth is a conversation: answered in chat, people can just type.
        var talking = aloud && ConversationLength > TimeSpan.Zero;
        conversation.Asking(DateTimeOffset.UtcNow);
        string? answered = null, spoken = null;
        try
        {
            timer.Restart();
            var answer = await _chat.Value.HandleVoiceRequestAsync(speaker, session.ReplyChannel, request, aloud,
                talking ? conversation.Earlier() : null);
            var answeredIn = timer.Elapsed;
            if (answer is not null && aloud)
            {
                answered = answer.Text;
                spoken = await SpeakAsync(session, answer.Text, heardIn, answeredIn, couldntSay: answer.InChat
                    ? null
                    : () => _chat.Value.PostUnspokenAnswerAsync(speaker, session.ReplyChannel, request, answer.Text));
            }
        }
        finally
        {
            var opened = talking && spoken is not null;
            conversation.Answered(DateTimeOffset.UtcNow, opened ? speaker.DisplayName : null, request,
                opened ? answered : null, spoken);
            if (opened)
            {
                WatchConversation(session);
            }
        }
    }

    /// <summary>Has the chat model loaded, without waiting (a local Ollama unloads it when idle, and loading it again
    /// would hold up the next answer by seconds).</summary>
    private void WarmUpModel(Session session)
    {
        if (_chatOptions.IsEnabled)
        {
            Background.Run(_logger, () => _model.WarmUpAsync(session.Stopping.Token), LogLevel.Debug,
                "Couldn't warm up the chat model for guild {GuildId}.", session.Guild.Id);
        }
    }

    /// <summary>How long a conversation stays open after the bot's last answer with nobody saying anything to it
    /// (Voice:ConversationSeconds, 0 = no conversations).</summary>
    private TimeSpan ConversationLength => TimeSpan.FromSeconds(Math.Clamp(_options.ConversationSeconds, 0, 60));

    /// <summary>Ends the session's conversation once it's gone quiet, with the closing chime. One watcher per session
    /// at a time; it stops when the conversation (or the session) ends.</summary>
    private void WatchConversation(Session session)
    {
        if (!session.TryStartWatching())
        {
            return;
        }

        Background.Run(_logger, async () =>
        {
            try
            {
                while (session.Conversation.IsOpen && !session.Stopping.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250), session.Stopping.Token);
                    if (session.Conversation.TryEndQuiet(DateTimeOffset.UtcNow, session.TalkingUntil))
                    {
                        ExitChime(session, "it went quiet");
                    }
                }
            }
            finally
            {
                session.StoppedWatching();
            }

            // A request answered just as this stopped may have opened a new conversation with nobody watching it.
            if (session.Conversation.IsOpen && !session.Stopping.IsCancellationRequested)
            {
                WatchConversation(session);
            }
        }, LogLevel.Debug, "Stopped watching a voice conversation in guild {GuildId}.", session.Guild.Id);
    }

    /// <summary>
    /// Speech-to-text, on noise, can come out with the words it was told to expect (<see cref="SpeechHint"/>)
    /// rather than what was said. Those words in a row are an echo of the hint, not speech.
    /// </summary>
    private static bool IsHintEcho(string text)
        => text.Contains(HintWords, StringComparison.OrdinalIgnoreCase);

    private const string HintWords = "Patch notes, LFG, loadout";

    /// <summary>
    /// The words speech-to-text is told to expect, so they come out spelled right: the bot's name, and gaming words it
    /// otherwise mishears ("patch notes" came out as "Passonets"). Speech-to-text can echo its hint on unclear audio,
    /// so the name comes last: an echo is only "hey ModBot", with nothing asked after it.
    /// </summary>
    public static string SpeechHint(string name) => $"{HintWords}, ranked, web search. Hey {name}.";

    /// <summary>Whether the bot can be heard in <paramref name="channel"/>: Speak, and not server-muted (checked on each
    /// request, not just on joining, or a bot muted since would talk to no one).</summary>
    private static bool CanSpeakIn(SocketVoiceChannel channel)
        => channel.Guild.CurrentUser is var bot && bot.GetPermissions(channel).Speak && !bot.IsMuted && !bot.IsSuppressed;

    /// <summary>
    /// Whether everyone in <paramref name="voice"/> can read <paramref name="replyChannel"/>. An answer can come from
    /// that channel's messages (the chatbot reads them), so said aloud it mustn't reach anyone who couldn't read it
    /// there: if someone can't, it's posted instead.
    /// </summary>
    private static bool EveryoneCanRead(SocketVoiceChannel voice, SocketTextChannel replyChannel)
        => voice.ConnectedUsers.Where(u => !u.IsBot)
            .All(u => u.GetPermissions(replyChannel) is { ViewChannel: true, ReadMessageHistory: true });

    /// <summary>Plays the chime in the channel, if this session can (another sound playing, it's skipped).</summary>
    private void Chime(Session session)
    {
        if (_options.Chime)
        {
            Play(session, _ => Task.FromResult<byte[]?>(ChimeSound.Value), speech: false, couldntPlay: null);
        }
    }

    /// <summary>Plays the closing chime: a conversation's over, and it's back to waiting for its name. Waits for an
    /// answer still playing, so it comes after it.</summary>
    private void ExitChime(Session session, string why)
    {
        _logger.LogInformation("Voice conversation in guild {GuildId} ended: {Why}.", session.Guild.Id, why);
        if (_options.Chime)
        {
            Background.Run(_logger, () => PlayAsync(session, _ => Task.FromResult<byte[]?>(ExitChimeSound.Value),
                speech: false, couldntPlay: null, wait: true), LogLevel.Warning,
                "Couldn't play the closing chime in guild {GuildId}.", session.Guild.Id);
        }
    }

    /// <summary>Reads <paramref name="answer"/> aloud (its first few sentences, as speech), after any sound already
    /// playing, and returns once it's been said. If it can't be said, <paramref name="couldntSay"/> runs (posting it
    /// in chat instead). Returns what was said, or null if nothing was.</summary>
    /// <param name="heardIn">How long speech-to-text took, and <paramref name="answeredIn"/> the answer: logged with
    /// how long speaking took, to see where the time goes.</param>
    private async Task<string?> SpeakAsync(Session session, string answer, TimeSpan heardIn, TimeSpan answeredIn,
        Func<Task>? couldntSay)
    {
        // Nothing to say aloud (only a link or emoji): then it's for reading.
        var max = Math.Clamp(_options.MaxSpokenCharacters, 50, 2000);
        if (SpokenText.From(answer, max, out var whole) is not { Length: > 0 } text)
        {
            await (couldntSay?.Invoke() ?? Task.CompletedTask);
            return null;
        }

        // Only how much, never the words: what the bot says stays out of the log, as what people say does.
        if (text.Length < whole)
        {
            _logger.LogInformation("Spoken answer in guild {GuildId} cut to {Said} of {Whole} characters " +
                                   "(Voice:MaxSpokenCharacters is {Max}); the rest wasn't said or posted.",
                session.Guild.Id, text.Length, whole, max);
        }

        var played = await PlayAsync(session, async token =>
        {
            var timer = Stopwatch.StartNew();
            var speech = await _voiceOut.SpeakAsync(text, token);
            _logger.LogInformation("Voice answer in guild {GuildId}: speech-to-text {Heard} ms, answer {Answered} ms, " +
                                   "text-to-speech {Voiced} ms.", session.Guild.Id, (int)heardIn.TotalMilliseconds,
                (int)answeredIn.TotalMilliseconds, (int)timer.ElapsedMilliseconds);
            return speech;
        }, speech: true, couldntSay);
        return played ? text : null;
    }

    /// <summary>Makes a sound and plays it in the channel, without waiting for it (see <see cref="PlayAsync"/>).</summary>
    private void Play(Session session, Func<CancellationToken, Task<byte[]?>> sound, bool speech,
        Func<Task>? couldntPlay)
        => Background.Run(_logger, () => PlayAsync(session, sound, speech, couldntPlay), LogLevel.Warning,
            "A sound or its fallback failed in guild {GuildId}.", session.Guild.Id);

    /// <summary>Makes a sound (<paramref name="sound"/>, in Discord's format) and plays it in the channel. A failure to
    /// play turns sound off for the rest of the session; <paramref name="couldntPlay"/> then runs, so a spoken answer
    /// still reaches chat. Returns whether it was played.</summary>
    /// <param name="wait">For a sound that isn't speech: wait for one already playing rather than skip it.</param>
    private async Task<bool> PlayAsync(Session session, Func<CancellationToken, Task<byte[]?>> sound, bool speech,
        Func<Task>? couldntPlay, bool wait = false)
    {
        var played = false;
        byte[]? pcm = null;
        try
        {
            // Making the sound failing only costs this one (its answer goes to chat, below).
            pcm = session.CanPlay ? await sound(session.Stopping.Token) : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Couldn't make a sound to play in guild {GuildId}.", session.Guild.Id);
        }
        catch (OperationCanceledException)
        {
            // Stopped listening meanwhile.
        }

        try
        {
            played = pcm is { Length: > 0 } && await session.PlayAsync(pcm, speech, EchoTail, wait);
        }
        catch (OperationCanceledException) when (session.Stopping.IsCancellationRequested)
        {
            // Stopped listening meanwhile (the answer still goes to chat, below).
        }
        catch (Exception ex) // including a send that timed out: the connection isn't taking sound
        {
            session.CanPlay = false;
            _logger.LogWarning(ex, "Couldn't play sound in guild {GuildId}; no more sounds until I rejoin.",
                session.Guild.Id);
        }

        if (!played && couldntPlay is not null)
        {
            await couldntPlay();
        }

        return played;
    }

    private bool MusicIsPlaying(ulong guildId) => MusicService.GetPlayer(_audio, guildId) is not null;

    private async Task LeaveAsync(SocketVoiceChannel channel)
    {
        try
        {
            await Task.WhenAny(channel.DisconnectAsync(), Task.Delay(TimeSpan.FromSeconds(5)));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Couldn't leave voice channel {ChannelId}.", channel.Id);
        }
    }

    /// <summary>Listening in one server's voice channel.</summary>
    private sealed class Session(SocketGuild guild, SocketVoiceChannel channel, SocketTextChannel replyChannel,
        bool autoJoined, bool canPlay, TimeSpan conversationLength)
    {
        private readonly ConcurrentDictionary<ulong, CancellationTokenSource> _readers = new();
        private readonly SemaphoreSlim _playing = new(1, 1);
        private AudioOutStream? _sound;
        private long _talkingUntil;

        /// <summary>Joined by itself (voice auto-join), not because someone asked.</summary>
        public bool AutoJoined { get; } = autoJoined;

        /// <summary>Set once connected.</summary>
        public IAudioClient? Voice { get; set; }

        /// <summary>The status it set on the channel (null if it didn't), and what the status was before.</summary>
        public string? OurStatus { get; set; }

        public string StatusBefore { get; set; } = "";

        /// <summary>How long a spoken answer waits for another sound (an earlier answer) to finish.</summary>
        private static readonly TimeSpan LongestWaitToSpeak = TimeSpan.FromSeconds(15);

        /// <summary>Whether it can make sounds here (the chime, spoken replies): Speak permission, and nothing's failed.</summary>
        public volatile bool CanPlay = canPlay;

        /// <summary>The bot is talking (or just was): what it hears now may be its own voice, through someone's mic.</summary>
        public bool IsTalking => DateTime.UtcNow.Ticks < Interlocked.Read(ref _talkingUntil);

        /// <summary>When the bot stops talking (with its echo tail), or stopped.</summary>
        public DateTimeOffset TalkingUntil => new(Interlocked.Read(ref _talkingUntil), TimeSpan.Zero);

        /// <summary>The back-and-forth after an answer, when anyone can say the next thing without the bot's name.</summary>
        public VoiceConversation Conversation { get; } = new(conversationLength);

        private int _watching;

        /// <summary>True if nothing's watching <see cref="Conversation"/> to end it yet, and now this caller is.</summary>
        public bool TryStartWatching() => Interlocked.CompareExchange(ref _watching, 1, 0) == 0;

        public void StoppedWatching() => Interlocked.Exchange(ref _watching, 0);

        /// <summary>
        /// Plays <paramref name="pcm"/> (Discord's format) in the channel, one sound at a time. Speech waits its turn,
        /// but only <see cref="LongestWaitToSpeak"/> (later than that, it's no longer an answer: the caller posts it
        /// instead); a chime with something else playing is skipped (it'd be late).
        /// </summary>
        /// <param name="speech">Spoken: <see cref="IsTalking"/> holds while it plays, and for
        /// <paramref name="echoTail"/> after.</param>
        /// <returns>Whether it was played (not skipped).</returns>
        /// <param name="wait">For a sound that isn't speech: wait its turn like speech, rather than be skipped.</param>
        public async Task<bool> PlayAsync(byte[] pcm, bool speech, TimeSpan echoTail, bool wait = false)
        {
            var length = VoiceAudio.DurationOf(pcm.Length);
            if (Voice is null ||
                !await _playing.WaitAsync(speech || wait ? LongestWaitToSpeak : TimeSpan.Zero, Stopping.Token))
            {
                return false;
            }

            try
            {
                if (!CanPlay || Stopping.IsCancellationRequested)
                {
                    return false; // the session ended while this one waited
                }

                if (speech)
                {
                    Interlocked.Exchange(ref _talkingUntil, (DateTime.UtcNow + length + echoTail).Ticks);
                }

                // One stream for the whole session: Discord.Net sends silence and the speaking flag around each sound.
                // A short buffer, because Discord.Net sends nothing until the buffer has filled once: at its default
                // (a second), a quarter-second chime would just sit there. And never waits much past the sound's own
                // length for it to go out.
                _sound ??= Voice.CreatePCMStream(AudioApplication.Voice, bufferMillis: 100);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Stopping.Token);
                timeout.CancelAfter(length + TimeSpan.FromSeconds(5));
                await _sound.WriteAsync(pcm, timeout.Token);
                await _sound.FlushAsync(timeout.Token);
                return true;
            }
            finally
            {
                if (speech)
                {
                    // Writing can finish before the last of it is heard: keep ignoring a moment longer.
                    Interlocked.Exchange(ref _talkingUntil,
                        Math.Max(Interlocked.Read(ref _talkingUntil), (DateTime.UtcNow + echoTail).Ticks));
                }

                _playing.Release();
            }
        }

        /// <summary>Ends the sound stream. Call after <see cref="Stopping"/> is cancelled, which cuts short a sound
        /// that's playing, so this only waits a moment for it.</summary>
        public async Task StopSoundAsync()
        {
            CanPlay = false;
            // A sound that's playing ends at once (Stopping is cancelled first). This runs holding the start/stop lock
            // (music waits on it), so it waits only briefly, then closes the stream anyway.
            var waited = await _playing.WaitAsync(TimeSpan.FromSeconds(1));
            try
            {
                _sound?.Dispose();
                _sound = null;
            }
            catch (Exception)
            {
                // Already gone with the connection.
            }
            finally
            {
                if (waited)
                {
                    _playing.Release();
                }
            }
        }
        private readonly ConcurrentDictionary<ulong, DateTimeOffset> _wakeWords = new();

        public SocketGuild Guild { get; } = guild;
        public SocketVoiceChannel Channel { get; } = channel;
        public SocketTextChannel ReplyChannel { get; } = replyChannel;
        public CancellationTokenSource Stopping { get; } = new();

        /// <summary>A token for a new reader of <paramref name="userId"/>'s audio, stopping any older one.</summary>
        public CancellationToken StartReading(ulong userId)
        {
            var reader = CancellationTokenSource.CreateLinkedTokenSource(Stopping.Token);
            if (_readers.TryGetValue(userId, out var old))
            {
                old.Cancel();
            }

            _readers[userId] = reader;
            return reader.Token;
        }

        public void StopReading(ulong userId)
        {
            if (_readers.TryRemove(userId, out var reader))
            {
                reader.Cancel();
            }
        }

        private readonly ConcurrentDictionary<ulong, int> _waitingByUser = new();
        private readonly Lock _waitingLock = new();
        private int _waitingSpeech;

        /// <summary>Counts one of <paramref name="userId"/>'s sentences in, unless <paramref name="max"/> are already
        /// waiting in all, or <paramref name="maxEach"/> of theirs: one noisy mic can't take every place and crowd out
        /// everyone else.</summary>
        public bool TryQueueSpeech(ulong userId, int max, int maxEach)
        {
            lock (_waitingLock)
            {
                var theirs = _waitingByUser.GetValueOrDefault(userId);
                if (_waitingSpeech >= max || theirs >= maxEach)
                {
                    return false;
                }

                _waitingSpeech++;
                _waitingByUser[userId] = theirs + 1;
                return true;
            }
        }

        public void SpeechDone(ulong userId)
        {
            lock (_waitingLock)
            {
                _waitingSpeech--;
                _waitingByUser[userId] = Math.Max(0, _waitingByUser.GetValueOrDefault(userId) - 1);
            }
        }

        public void HeardWakeWord(ulong userId) => _wakeWords[userId] = DateTimeOffset.UtcNow;

        /// <summary>True (once) if <paramref name="userId"/> said just the wake word within <paramref name="within"/>.</summary>
        public bool TakeWakeWord(ulong userId, TimeSpan within)
            => _wakeWords.TryRemove(userId, out var at) && DateTimeOffset.UtcNow - at <= within;
    }
}
