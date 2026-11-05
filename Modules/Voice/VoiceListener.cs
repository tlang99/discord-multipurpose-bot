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
using ModBot.Services.Moderation;
using ModBot.Services.Voice;

namespace ModBot.Modules.Voice;

/// <summary>
/// Voice commands: sits in a voice channel (muted), turns what people say into text on this machine, and hands
/// anything said after its name ("hey ModBot, skip this song") to the chatbot as that person's request, so it's done
/// exactly like the same words typed as an @mention. Everything else is thrown away as soon as it's checked, and no
/// audio is ever kept.
/// </summary>
/// <remarks>
/// One voice connection per server is all Discord allows, and music (Lavalink) needs it to play, so the bot listens
/// only while no music is playing: starting the music stops the listening (<see cref="MusicService"/> calls
/// <see cref="StopAsync"/>), and it won't start listening over music.
/// </remarks>
public sealed class VoiceListener
{
    /// <summary>Said just the wake word, then paused: their next sentence counts as the request, within this long.</summary>
    private static readonly TimeSpan WakeWordWait = TimeSpan.FromSeconds(8);

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Shorter than this is a cough or a click, not a sentence.</summary>
    private static readonly TimeSpan ShortestSpeech = TimeSpan.FromMilliseconds(400);

    private readonly DiscordSocketClient _client;
    private readonly SpeechToText _speech;
    private readonly IAudioService _audio;
    private readonly Lazy<ChatHandler> _chat;
    private readonly VoiceOptions _options;
    private readonly ILogger<VoiceListener> _logger;
    private readonly ConcurrentDictionary<ulong, Session> _sessions = new();
    private readonly KeyedLock<ulong> _startStop = new();

    public VoiceListener(DiscordSocketClient client, SpeechToText speech, IAudioService audio, Lazy<ChatHandler> chat,
        IOptions<VoiceOptions> options, ILogger<VoiceListener> logger)
    {
        _client = client;
        _speech = speech;
        _audio = audio;
        _chat = chat;
        _options = options.Value;
        _logger = logger;
        _client.UserVoiceStateUpdated += OnVoiceStateUpdatedAsync;
    }

    /// <summary>Whether voice commands can work on this bot at all; null if they can, else why not.</summary>
    public string? SetupProblem => _speech.Problem;

    public bool IsListening(ulong guildId) => _sessions.ContainsKey(guildId);

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

            // Replies go in the voice channel's own chat, where the people talking will see them.
            var replyChannel = ChannelAccess.CanPost(guild.CurrentUser, channel) ? channel : askedIn;
            if (!ChannelAccess.CanPost(guild.CurrentUser, replyChannel))
            {
                return $"I can't post in {channel.Mention}'s chat or here, so I'd have nowhere to answer.";
            }

            IAudioClient voice;
            try
            {
                // Muted (it never talks), but not deafened: a deafened bot is sent no audio.
                var connecting = channel.ConnectAsync(selfDeaf: false, selfMute: true);
                if (await Task.WhenAny(connecting, Task.Delay(ConnectTimeout)) != connecting)
                {
                    await LeaveAsync(channel);
                    return $"Joining {channel.Mention} timed out - try again in a moment.";
                }

                voice = await connecting;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Couldn't join voice channel {ChannelId} to listen.", channel.Id);
                await LeaveAsync(channel);
                return $"I couldn't join {channel.Mention} - try again in a moment.";
            }

            var session = new Session(guild, channel, replyChannel);
            _sessions[guild.Id] = session;
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
                // Kicked, moved, or the connection dropped: nothing more to hear.
                Background.Run(_logger, () => EndAsync(guild.Id, session, null, leave: false), LogLevel.Debug,
                    "Couldn't clean up after voice dropped in guild {GuildId}.", guild.Id);
                return Task.CompletedTask;
            };
            foreach (var member in channel.ConnectedUsers)
            {
                Listen(session, member.Id, member.AudioStream);
            }

            if (_options.MaxListenMinutes > 0)
            {
                Background.Run(_logger, async () =>
                {
                    await Task.Delay(TimeSpan.FromMinutes(_options.MaxListenMinutes), session.Stopping.Token);
                    await EndAsync(guild.Id, session, $"🎙️ Stopped listening in {channel.Mention} after " +
                                                      $"{_options.MaxListenMinutes} minutes. Ask me again to keep going.");
                }, LogLevel.Debug, "Voice time limit failed in guild {GuildId}.", guild.Id);
            }

            _logger.LogInformation("Listening for voice commands in {Channel} ({ChannelId}), started by {User} ({UserId}).",
                channel.Name, channel.Id, user.Username, user.Id);

            var name = guild.CurrentUser.DisplayName;
            var notice = $"🎙️ **Listening for voice commands** in {channel.Mention}, started by {user.Mention}. Say " +
                         $"\"hey {name}\" and then what you want, like \"hey {name}, make an LFG for ranked\", and " +
                         "I'll do it as if you'd typed it. Speech is turned into text on the bot's own computer, only " +
                         $"what's said after my name is acted on, and no audio is kept. To stop: \"hey {name}, stop " +
                         "listening\", `/voice stop`, or everyone leaving.";
            await PostAsync(replyChannel, notice);
            return replyChannel.Id == askedIn.Id
                ? ""
                : $"🎙️ Listening in {channel.Mention} - my replies go in its chat.";
        });

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

        await StopAsync(user.Guild.Id, $"🎙️ Stopped listening in {session.Channel.Mention} ({user.Mention} asked).");
        // The notice says so already when it goes where they asked.
        return session.ReplyChannel.Id == askedIn.Id ? "" : "🎙️ Stopped listening.";
    }

    /// <summary>Stops listening in the server, if it is, and leaves the voice channel. Posts
    /// <paramref name="notice"/> where the replies were going, if given.</summary>
    public async Task StopAsync(ulong guildId, string? notice)
    {
        if (_sessions.TryGetValue(guildId, out var session))
        {
            await EndAsync(guildId, session, notice);
        }
    }

    private Task EndAsync(ulong guildId, Session session, string? notice, bool leave = true)
        => _startStop.RunAsync(guildId, async () =>
        {
            // Only the session that's still current: a late event from an old one mustn't end a new one.
            if (!_sessions.TryRemove(new KeyValuePair<ulong, Session>(guildId, session)))
            {
                return;
            }

            session.Stopping.Cancel();
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
        });

    /// <summary>Everyone (but bots) left the channel: stop, nobody's there to listen to.</summary>
    private Task OnVoiceStateUpdatedAsync(SocketUser user, SocketVoiceState before, SocketVoiceState after)
    {
        if (user is SocketGuildUser member && _sessions.TryGetValue(member.Guild.Id, out var session) &&
            before.VoiceChannel?.Id == session.Channel.Id && after.VoiceChannel?.Id != session.Channel.Id)
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
                if (VoiceAudio.DurationOf(said.Length) >= ShortestSpeech)
                {
                    Background.Run(_logger, () => HandleSpeechAsync(session, userId, said), LogLevel.Warning,
                        "Couldn't handle speech in guild {GuildId}.", session.Guild.Id);
                }
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
        if (request is null)
        {
            // Not said to the bot, unless the sentence before was just its name.
            if (!session.TakeWakeWord(userId, WakeWordWait))
            {
                return;
            }

            request = text.TrimEnd('.', ' ');
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

    private bool MusicIsPlaying(ulong guildId)
        => _audio.Players.TryGetPlayer(guildId, out var player) && player is not null &&
           player.State != PlayerState.Destroyed;

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
    private sealed class Session(SocketGuild guild, SocketVoiceChannel channel, SocketTextChannel replyChannel)
    {
        private readonly ConcurrentDictionary<ulong, CancellationTokenSource> _readers = new();
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

        public void HeardWakeWord(ulong userId) => _wakeWords[userId] = DateTimeOffset.UtcNow;

        /// <summary>True (once) if <paramref name="userId"/> said just the wake word within <paramref name="within"/>.</summary>
        public bool TakeWakeWord(ulong userId, TimeSpan within)
            => _wakeWords.TryRemove(userId, out var at) && DateTimeOffset.UtcNow - at <= within;
    }
}
