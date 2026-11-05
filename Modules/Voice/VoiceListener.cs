using System.Collections.Concurrent;
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
using ModBot.Services.Moderation;
using ModBot.Services.Settings;
using ModBot.Services.Voice;

namespace ModBot.Modules.Voice;

/// <summary>
/// Voice commands: sits in a voice channel (muted), turns what people say into text on this machine, and hands
/// anything said after its name ("hey ModBot, skip this song") to the chatbot as that person's request, so it's done
/// exactly like the same words typed as an @mention. Everything else is thrown away as soon as it's checked, and no
/// audio is ever kept.
/// </summary>
/// <remarks>
/// <para>One voice connection per server is all Discord allows, and music (Lavalink) needs it to play, so the bot
/// listens only while no music is playing: starting the music stops the listening (<see cref="MusicService"/> calls
/// <see cref="StopForMusicAsync"/>), and it won't start listening over music.</para>
/// <para>Voice auto-join (on by default once a server turns voice commands on): the bot joins by itself when people
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

    private static readonly byte[] ChimeSound = VoiceAudio.Chime();

    private readonly DiscordSocketClient _client;
    private readonly SpeechToText _speech;
    private readonly IAudioService _audio;
    private readonly Lazy<ChatHandler> _chat;
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

    /// <summary>Per server, the channel it was listening in when music took over, for a short "I'm back" notice.</summary>
    private readonly ConcurrentDictionary<ulong, ulong> _backAfterMusic = new();

    private readonly ConcurrentDictionary<ulong, bool> _autoJoinPending = new();
    private readonly ConcurrentDictionary<ulong, DateTimeOffset> _failedJoins = new();

    public VoiceListener(DiscordSocketClient client, SpeechToText speech, IAudioService audio, Lazy<ChatHandler> chat,
        ServerSettingsService settings, IOptions<ChatOptions> chatOptions, IOptions<VoiceOptions> options,
        ILogger<VoiceListener> logger)
    {
        _client = client;
        _speech = speech;
        _audio = audio;
        _chat = chat;
        _settings = settings;
        _chatOptions = chatOptions.Value;
        _options = options.Value;
        _logger = logger;
        _client.UserVoiceStateUpdated += OnVoiceStateUpdatedAsync;
        // On connecting (and reconnecting), join anyone already sitting in voice.
        _client.GuildAvailable += guild =>
        {
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
    /// Joins <paramref name="user"/>'s voice channel and starts listening, posting a notice that says so where the
    /// people in the channel will see it. Returns what to tell the person who asked.
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

            var permissions = guild.CurrentUser.GetPermissions(channel);
            if (!permissions.ViewChannel || !permissions.Connect)
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
            return replyChannel.Id == askedIn.Id
                ? ""
                : $"🎙️ Listening in {channel.Mention} - my replies go in its chat.";
        });

    /// <summary>
    /// Joins <paramref name="channel"/> and starts listening: the part shared by being asked to and joining by itself
    /// (<paramref name="startedBy"/> null). Call holding <see cref="_startStop"/>. Returns why it couldn't, or null.
    /// </summary>
    private async Task<string?> JoinHoldingLockAsync(SocketGuild guild, SocketVoiceChannel channel,
        SocketTextChannel replyChannel, SocketGuildUser? startedBy, bool backAfterMusic = false)
    {
        IAudioClient voice;
        // The chime needs the Speak permission, and an unmuted bot: muted, it sends no sound at all.
        var canChime = _options.Chime && guild.CurrentUser.GetPermissions(channel).Speak;
        var session = new Session(guild, channel, replyChannel, autoJoined: startedBy is null, canChime);
        _joining[guild.Id] = true;
        try
        {
            // Muted unless it chimes (it never talks), but not deafened: a deafened bot is sent no audio.
            var connecting = channel.ConnectAsync(selfDeaf: false, selfMute: !canChime);
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
                _failedJoins[guild.Id] = DateTimeOffset.UtcNow;
                return $"Joining {channel.Mention} timed out - try again in a moment.";
            }

            voice = await connecting;
            session.Voice = voice;
            _sessions[guild.Id] = session;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Couldn't join voice channel {ChannelId} to listen.", channel.Id);
            _failedJoins[guild.Id] = DateTimeOffset.UtcNow;
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
                    await EndAsync(guild.Id, session,
                        $"🎙️ Stopped listening in {channel.Mention} - I lost the voice connection.");
                }
            }, LogLevel.Debug, "Couldn't check the voice connection in guild {GuildId}.", guild.Id);
            return Task.CompletedTask;
        };
        foreach (var member in channel.ConnectedUsers)
        {
            session.TryMarkToldListening(member.Id); // the notice below tells everyone already here
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

                await EndAsync(guild.Id, session, $"🎙️ Stopped listening in {channel.Mention} after " +
                                                  $"{_options.MaxListenMinutes} minutes. Ask me again to keep going.");
            }, LogLevel.Debug, "Voice time limit failed in guild {GuildId}.", guild.Id);
        }

        _logger.LogInformation("Listening for voice commands in {Channel} ({ChannelId}), {StartedBy}.",
            channel.Name, channel.Id,
            startedBy is null ? "joined by itself" : $"started by {startedBy.Username} ({startedBy.Id})");

        await PostAsync(replyChannel, ListeningNotice(guild.CurrentUser.DisplayName, channel, startedBy,
            canChime, backAfterMusic));
        return null;
    }

    /// <summary>What it posts on starting to listen, so nobody in the channel is listened to unawares.</summary>
    private static string ListeningNotice(string name, SocketVoiceChannel channel, SocketGuildUser? startedBy,
        bool chimes, bool backAfterMusic)
    {
        if (backAfterMusic)
        {
            return $"🎙️ Music's done, so I'm listening in {channel.Mention} again for \"hey {name}\" requests. " +
                   $"\"Hey {name}, stop listening\" or `/voice stop` to stop.";
        }

        var how = startedBy is null
            ? " (I join by myself when people are in voice, since voice commands are on here)"
            : $", started by {startedBy.Mention}";
        return $"🎙️ **Listening for voice commands** in {channel.Mention}{how}. Say \"hey {name}\" and then what you " +
               $"want, like \"hey {name}, make an LFG for ranked\", and I'll do it as if you'd typed it." +
               (chimes ? " I chime when I hear my name." : "") +
               " Speech is turned into text on the bot's own computer, only what's said after my name is acted on, " +
               $"and no audio is kept. To stop: \"hey {name}, stop listening\" or `/voice stop`" +
               (startedBy is null ? ", and I'll stay out of this channel until everyone's left it." : ", or everyone leaving.");
    }

    /// <summary>Looks for people to join in <paramref name="guildId"/> after <paramref name="delay"/>, if the server
    /// has voice auto-join on. Several calls while one's waiting make just the one look.</summary>
    public void ScheduleAutoJoin(ulong guildId, TimeSpan delay)
    {
        if (!_autoJoinPending.TryAdd(guildId, true))
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
                _autoJoinPending.TryRemove(guildId, out _);
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
            !_chatOptions.IsEnabled || MusicIsPlaying(guildId) ||
            _failedJoins.TryGetValue(guildId, out var failed) && DateTimeOffset.UtcNow - failed < AfterFailedJoin)
        {
            return;
        }

        var server = await _settings.GetAsync(guildId);
        if (!AutoJoins(server))
        {
            return;
        }

        var me = guild.CurrentUser;
        var candidates = guild.VoiceChannels
            .Where(c => c is not SocketStageChannel && c.Id != guild.AFKChannel?.Id && CanJoinByItself(me, c))
            .Select(c => new AutoJoinPicker.Channel(c.Id, c.ConnectedUsers.Count(u => !u.IsBot), c.Position));
        var stayOut = _stayOut.TryGetValue(guildId, out var outOf) ? outOf : (ulong?)null;
        if (AutoJoinPicker.Pick(candidates, server.AutoJoinChannel.ChannelId, stayOut) is not { } id ||
            guild.GetVoiceChannel(id) is not { } channel)
        {
            return;
        }

        var back = _backAfterMusic.TryRemove(guildId, out var wasIn) && wasIn == id;
        // Replies go in the voice channel's own chat, where the people talking see them. Each request still needs its
        // speaker to be able to post there (ChatHandler).
        await JoinHoldingLockAsync(guild, channel, channel, startedBy: null, back);
    }

    private static bool AutoJoins(ServerSettings server)
        => server.IsEnabled(ServerFeature.Chat) && server.IsEnabled(ServerFeature.VoiceCommands) &&
           server.IsEnabled(ServerFeature.VoiceAutoJoin);

    /// <summary>A channel it may join by itself: allowed in, not full, and able to post in its chat (where the
    /// listening notice and the replies go).</summary>
    private static bool CanJoinByItself(SocketGuildUser me, SocketVoiceChannel channel)
    {
        var permissions = me.GetPermissions(channel);
        return permissions.ViewChannel && permissions.Connect && ChannelAccess.CanPost(me, channel) &&
               (channel.UserLimit is not > 0 || channel.ConnectedUsers.Count < channel.UserLimit ||
                permissions.MoveMembers);
    }

    /// <summary>Listening because it joined by itself (not because someone asked).</summary>
    public bool IsAutoListening(ulong guildId) => _sessions.TryGetValue(guildId, out var s) && s.AutoJoined;

    /// <summary>Music is about to take the voice connection: stops listening, and with voice auto-join on, notes to
    /// come back to the same channel when the music's done.</summary>
    public async Task StopForMusicAsync(ulong guildId)
    {
        var autoJoins = AutoJoins(await _settings.GetAsync(guildId));
        if (autoJoins && _sessions.TryGetValue(guildId, out var session))
        {
            _backAfterMusic[guildId] = session.Channel.Id;
        }

        await StopAsync(guildId, autoJoins
            ? "🎶 Music's starting, so I've stopped listening for voice commands - I can't do both at once. I'll be " +
              "back listening when the music's done."
            : "🎶 Music's starting, so I've stopped listening for voice commands - I can't do both at once. Ask me " +
              "to listen again once the music's done.");
    }

    /// <summary>Stops listening when <paramref name="user"/> asks: anyone in the channel, or a moderator. Returns what
    /// to tell them (empty when the stop notice already went in <paramref name="askedIn"/>).</summary>
    public async Task<string> StopForAsync(SocketGuildUser user, IChannel askedIn)
    {
        if (!_sessions.TryGetValue(user.Guild.Id, out var session))
        {
            return "I'm not listening anywhere right now.";
        }

        if (user.VoiceChannel?.Id != session.Channel.Id && !Moderators.IsModerator(user))
        {
            return $"Only people in {session.Channel.Mention} (or a moderator) can stop me listening there.";
        }

        // Or voice auto-join would bring it straight back.
        _stayOut[user.Guild.Id] = session.Channel.Id;
        var autoJoins = AutoJoins(await _settings.GetAsync(user.Guild.Id));
        await StopAsync(user.Guild.Id, $"🎙️ Stopped listening in {session.Channel.Mention} ({user.Mention} asked)." +
                                       (autoJoins
                                           ? " I won't join it by myself again until everyone's left it."
                                           : ""));
        // The notice says so already when it goes where they asked.
        return session.ReplyChannel.Id == askedIn.Id ? "" : "🎙️ Stopped listening.";
    }

    /// <summary>Stops listening in the server, if it is, and leaves the voice channel. Posts
    /// <paramref name="notice"/> where the replies were going, if given. A join in progress finishes first (it holds
    /// the same lock), so music asking to stop never races one.</summary>
    public Task StopAsync(ulong guildId, string? notice)
        => _startStop.RunAsync(guildId, () => _sessions.TryGetValue(guildId, out var session)
            ? EndHoldingLockAsync(guildId, session, notice, leave: true)
            : Task.CompletedTask);

    private Task EndAsync(ulong guildId, Session session, string? notice, bool leave = true)
        => _startStop.RunAsync(guildId, () => EndHoldingLockAsync(guildId, session, notice, leave));

    private async Task EndHoldingLockAsync(ulong guildId, Session session, string? notice, bool leave)
    {
        // Only the session that's still current: a late event from an old one mustn't end a new one.
        if (!_sessions.TryRemove(new KeyValuePair<ulong, Session>(guildId, session)))
        {
            return;
        }

        session.Stopping.Cancel();
        session.StopChiming();
        if (leave)
        {
            await LeaveAsync(session.Channel);
        }

        _logger.LogInformation("Stopped listening for voice commands in {Channel} ({ChannelId}).",
            session.Channel.Name, session.Channel.Id);
        if (notice is not null)
        {
            await PostAsync(session.ReplyChannel, notice);
        }
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
            // out of there, or voice auto-join would put it straight back.
            if (after.VoiceChannel?.Id != session.Channel.Id)
            {
                _stayOut[guildId] = session.Channel.Id;
                Background.Run(_logger,
                    () => EndAsync(member.Guild.Id, session,
                        $"🎙️ Stopped listening in {session.Channel.Mention} - I was taken out of the channel.",
                        leave: false),
                    LogLevel.Debug, "Couldn't stop listening in guild {GuildId}.", member.Guild.Id);
            }

            return Task.CompletedTask;
        }

        // Someone joining after the notice: tell them too (once per session), so nobody's listened to unawares.
        if (after.VoiceChannel?.Id == session.Channel.Id && before.VoiceChannel?.Id != session.Channel.Id &&
            !member.IsBot && session.TryMarkToldListening(member.Id))
        {
            var name = member.Guild.CurrentUser.DisplayName;
            Background.Run(_logger, () => PostAsync(session.ReplyChannel,
                    $"🎙️ {member.Mention}, heads up: I'm listening in {session.Channel.Mention} for \"hey {name}\" " +
                    "requests. Speech is turned into text on the bot's own computer and no audio is kept. " +
                    $"\"Hey {name}, stop listening\" or `/voice stop` to stop."),
                LogLevel.Debug, "Couldn't post a listening reminder in guild {GuildId}.", member.Guild.Id);
        }

        if (before.VoiceChannel?.Id == session.Channel.Id && after.VoiceChannel?.Id != session.Channel.Id)
        {
            session.StopReading(member.Id);
            if (!session.Channel.ConnectedUsers.Any(u => !u.IsBot))
            {
                Background.Run(_logger,
                    () => EndAsync(member.Guild.Id, session,
                        $"🎙️ Stopped listening in {session.Channel.Mention} - everyone left."),
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
                    speech.Write(frame.Payload);
                    lastHeard = DateTime.UtcNow;
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

    /// <summary>Turns one sentence into text and, if it was said to the bot, hands it on as a request.</summary>
    private async Task HandleSpeechAsync(Session session, ulong userId, byte[] said)
    {
        var name = session.Guild.CurrentUser.DisplayName;
        string text;
        try
        {
            text = await _speech.TranscribeAsync(VoiceAudio.ToWhisperSamples(said), $"Hey {name}.",
                session.Stopping.Token);
        }
        catch (OperationCanceledException)
        {
            return; // stopped listening while it was being turned into text
        }

        if (text.Length == 0 || session.Stopping.IsCancellationRequested)
        {
            return;
        }

        var request = WakeWord.FindRequest(text, [name, _client.CurrentUser.Username]);
        if (request is not null)
        {
            Chime(session); // heard its name (not again for the request that follows a lone "hey ModBot")
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

        // Not what was said: the chatbot posts that in the channel, and the log stays free of it.
        _logger.LogInformation("Voice request from {User} ({UserId}) in {Channel}.", speaker.Username, speaker.Id,
            session.Channel.Name);
        await _chat.Value.HandleVoiceRequestAsync(speaker, session.ReplyChannel, request);
    }

    /// <summary>Plays the chime in the channel, if this session can. Doesn't wait for it, and a failure just turns the
    /// chime off for the rest of the session: it's a nicety, never a reason for a request to go unanswered.</summary>
    private void Chime(Session session)
    {
        if (!session.CanChime)
        {
            return;
        }

        Background.Run(_logger, async () =>
        {
            try
            {
                await session.ChimeAsync(ChimeSound);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                session.CanChime = false;
                _logger.LogWarning(ex, "Couldn't play the chime in guild {GuildId}; no more chimes until I rejoin.",
                    session.Guild.Id);
            }
        }, LogLevel.Debug, "The chime stopped in guild {GuildId}.", session.Guild.Id);
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

    private async Task PostAsync(SocketTextChannel channel, string text)
    {
        try
        {
            await channel.SendMessageAsync(text, allowedMentions: AllowedMentions.None);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Couldn't post a voice notice in channel {ChannelId}.", channel.Id);
        }
    }

    /// <summary>Listening in one server's voice channel.</summary>
    private sealed class Session(SocketGuild guild, SocketVoiceChannel channel, SocketTextChannel replyChannel,
        bool autoJoined, bool canChime)
    {
        private readonly ConcurrentDictionary<ulong, CancellationTokenSource> _readers = new();
        private readonly SemaphoreSlim _chiming = new(1, 1);
        private AudioOutStream? _sound;

        /// <summary>Joined by itself (voice auto-join), not because someone asked.</summary>
        public bool AutoJoined { get; } = autoJoined;

        /// <summary>Set once connected.</summary>
        public IAudioClient? Voice { get; set; }

        public volatile bool CanChime = canChime;

        /// <summary>Plays <paramref name="pcm"/> (Discord's format) in the channel. One chime at a time: another heard
        /// meanwhile is skipped rather than queued behind it.</summary>
        public async Task ChimeAsync(byte[] pcm)
        {
            if (Voice is null || !await _chiming.WaitAsync(0))
            {
                return;
            }

            try
            {
                // One stream for the whole session: Discord.Net sends silence and the speaking flag around each sound.
                _sound ??= Voice.CreatePCMStream(AudioApplication.Voice);
                await _sound.WriteAsync(pcm, Stopping.Token);
                await _sound.FlushAsync(Stopping.Token);
            }
            finally
            {
                _chiming.Release();
            }
        }

        public void StopChiming()
        {
            CanChime = false;
            try
            {
                _sound?.Dispose();
            }
            catch (Exception)
            {
                // Already gone with the connection.
            }
        }
        private readonly ConcurrentDictionary<ulong, DateTimeOffset> _wakeWords = new();
        private readonly ConcurrentDictionary<ulong, bool> _toldListening = new();

        /// <summary>True the first time for each person: they haven't been told yet that it's listening.</summary>
        public bool TryMarkToldListening(ulong userId) => _toldListening.TryAdd(userId, true);

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
