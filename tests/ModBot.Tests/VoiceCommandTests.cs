using ModBot.Models;
using ModBot.Services.Settings;
using ModBot.Services.Voice;

namespace ModBot.Tests;

/// <summary>Finding "hey ModBot" in what speech-to-text heard, and what was asked after it.</summary>
public class WakeWordTests
{
    private static readonly string[] Names = ["ModBot"];

    [Theory]
    [InlineData("Hey ModBot, skip this song.", "skip this song")]
    [InlineData("hey modbot make an LFG for ranked", "make an LFG for ranked")]
    [InlineData("Hey Mod Bot, show the queue.", "show the queue")]
    [InlineData("Hey, mod-bot! Pause.", "Pause")]
    [InlineData("Okay ModBot, what's playing?", "what's playing?")]
    [InlineData("ModBot, remind me in 20 minutes to check the oven.", "remind me in 20 minutes to check the oven")]
    [InlineData("Um, so, hey ModBot, play Hotel California.", "play Hotel California")]
    // Exactly what speech-to-text heard in the voice test: two requests, and only the first is taken.
    [InlineData("Hey ModBot, make an LFG for three players. Hey ModBot, make an LFG for four players.",
        "make an LFG for three players")]
    [InlineData("ModBot, skip. ModBot, pause.", "skip")]
    [InlineData("That was a good game. ModBot, show the queue.", "show the queue")] // a new sentence
    public void Finds_the_request_after_the_name(string heard, string request)
        => Assert.Equal(request, WakeWord.FindRequest(heard, Names));

    [Fact]
    public void A_short_nickname_isnt_a_wake_word()
    {
        // "ok dj, that's enough" is just talk, so a two-letter nickname would go off all the time. The username still
        // works.
        string[] names = ["Dj", "ModBot"];

        Assert.Null(WakeWord.FindRequest("ok dj, that's enough", names));
        Assert.Equal("skip", WakeWord.FindRequest("hey ModBot, skip", names));
    }

    [Theory]
    [InlineData("Hey ModBot.")]
    [InlineData("hey mod bot")]
    public void Just_the_name_is_an_empty_request(string heard) => Assert.Equal("", WakeWord.FindRequest(heard, Names));

    [Theory]
    [InlineData("I was carrying the team.")]
    [InlineData("ModBot's so slow today.")] // talking about the bot, not to it
    [InlineData("I think the modbot hallucinated that.")]
    [InlineData("Ask modbot to skip it.")]
    [InlineData("")]
    public void Ignores_talk_that_isnt_to_the_bot(string heard) => Assert.Null(WakeWord.FindRequest(heard, Names));

    [Fact]
    public void Answers_to_its_nickname_and_its_username()
    {
        string[] names = ["Goo Bot", "ModBot"];

        Assert.Equal("skip", WakeWord.FindRequest("Hey Goo Bot, skip.", names));
        Assert.Equal("skip", WakeWord.FindRequest("Hey goobot skip", names));
        Assert.Equal("skip", WakeWord.FindRequest("Hey ModBot skip", names));
    }
}

/// <summary>"@bot listen in voice" / "stop listening", typed or said.</summary>
public class VoiceRequestReaderTests
{
    [Theory]
    [InlineData("listen in voice")]
    [InlineData("can you listen in the voice channel")]
    [InlineData("join voice")]
    [InlineData("join the vc and listen")]
    [InlineData("come listen")]
    [InlineData("start listening for voice commands please")]
    public void Reads_listen(string message) => Assert.Equal(VoiceRequest.Listen, VoiceRequestReader.Read(message));

    [Theory]
    [InlineData("stop listening")]
    [InlineData("stop listening.")]
    [InlineData("leave voice")]
    [InlineData("please leave the vc")]
    [InlineData("stop voice commands")]
    public void Reads_stop(string message) => Assert.Equal(VoiceRequest.StopListening, VoiceRequestReader.Read(message));

    [Theory]
    [InlineData("can you join voice later?")]
    [InlineData("do you listen to music")]
    [InlineData("stop the music")]
    [InlineData("I'll join voice in a bit")]
    [InlineData("play listen to the music")]
    public void Ignores_everything_else(string message) => Assert.Null(VoiceRequestReader.Read(message));
}

public class VoiceAudioTests
{
    [Fact]
    public void Discord_audio_becomes_a_third_as_many_mono_samples()
    {
        // Six 48 kHz stereo sample pairs: both channels at half volume, then both at full negative.
        var pcm = new List<byte>();
        foreach (var value in new short[] { 16384, 16384, 16384, -32768, -32768, -32768 })
        {
            pcm.AddRange(BitConverter.GetBytes(value)); // left
            pcm.AddRange(BitConverter.GetBytes(value)); // right
        }

        var samples = VoiceAudio.ToWhisperSamples(pcm.ToArray());

        Assert.Equal([0.5f, -1f], samples);
    }

    [Fact]
    public void A_second_of_discord_audio_lasts_a_second()
        => Assert.Equal(TimeSpan.FromSeconds(1), VoiceAudio.DurationOf(VoiceAudio.DiscordBytesPerSecond));

    [Fact]
    public void The_chime_is_short_soft_and_starts_and_ends_quietly()
    {
        var chime = VoiceAudio.Chime();
        var left = Enumerable.Range(0, chime.Length / 4).Select(i => BitConverter.ToInt16(chime, i * 4)).ToArray();
        var right = Enumerable.Range(0, chime.Length / 4).Select(i => BitConverter.ToInt16(chime, i * 4 + 2)).ToArray();

        Assert.Equal(0, chime.Length % VoiceAudio.FrameBytes); // whole 20 ms frames, so none of it is held back
        Assert.InRange(VoiceAudio.DurationOf(chime.Length).TotalSeconds, 0.3, 0.6);
        Assert.Equal(left, right);
        Assert.InRange(left.Max(s => Math.Abs((int)s)), 1000, VoiceAudio.ChimeVolume * short.MaxValue + 1);
        // Faded in and out, so it doesn't click.
        Assert.InRange(Math.Abs((int)left[0]), 0, 50);
        Assert.InRange(Math.Abs((int)left[^1]), 0, 50);
    }
}

/// <summary>Which voice channel the bot joins by itself.</summary>
public class AutoJoinPickerTests
{
    private static readonly AutoJoinPicker.Channel[] Channels =
    [
        new(1, People: 0, Position: 0),
        new(2, People: 2, Position: 1),
        new(3, People: 4, Position: 2),
        new(4, People: 4, Position: 3),
    ];

    [Fact]
    public void Joins_the_busiest_channel_and_the_higher_one_on_a_tie()
        => Assert.Equal(3UL, AutoJoinPicker.Pick(Channels, only: null, stayOut: null));

    [Fact]
    public void Keeps_out_of_a_channel_it_was_told_to_stop_listening_in()
        => Assert.Equal(4UL, AutoJoinPicker.Pick(Channels, only: null, stayOut: 3));

    [Fact]
    public void Only_joins_the_chosen_channel_when_the_server_picked_one()
    {
        Assert.Equal(2UL, AutoJoinPicker.Pick(Channels, only: 2, stayOut: null));
        Assert.Null(AutoJoinPicker.Pick(Channels, only: 1, stayOut: null)); // nobody there
        Assert.Null(AutoJoinPicker.Pick(Channels, only: 2, stayOut: 2));
        Assert.Null(AutoJoinPicker.Pick(Channels, only: 99, stayOut: null)); // deleted
    }

    [Fact]
    public void Joins_nothing_when_voice_is_empty()
        => Assert.Null(AutoJoinPicker.Pick([new(1, 0, 0)], only: null, stayOut: null));
}

/// <summary>Voice commands are the one feature that starts off: they mean the bot listening.</summary>
public class VoiceCommandsSwitchTests
{
    [Fact]
    public void Voice_commands_and_auto_join_start_off_and_everything_else_on()
    {
        var settings = new ServerSettings();
        ServerFeature[] voice = [ServerFeature.VoiceCommands, ServerFeature.VoiceAutoJoin];

        Assert.All(voice, f => Assert.False(settings.IsEnabled(f)));
        Assert.All(Enum.GetValues<ServerFeature>().Except(voice), f => Assert.True(settings.IsEnabled(f)));
    }

    [Fact]
    public void Turning_features_on_and_off_uses_the_right_list()
    {
        var settings = new ServerSettings();

        settings.Set(ServerFeature.VoiceCommands, true);
        settings.Set(ServerFeature.Polls, false);
        Assert.True(settings.IsEnabled(ServerFeature.VoiceCommands));
        Assert.False(settings.IsEnabled(ServerFeature.Polls));
        Assert.Equal(["VoiceCommands"], settings.EnabledFeatures);
        Assert.Equal(["Polls"], settings.DisabledFeatures);

        settings.Set(ServerFeature.VoiceCommands, false);
        settings.Set(ServerFeature.Polls, true);
        Assert.False(settings.IsEnabled(ServerFeature.VoiceCommands));
        Assert.True(settings.IsEnabled(ServerFeature.Polls));
        Assert.Empty(settings.EnabledFeatures);
        Assert.Empty(settings.DisabledFeatures);
    }

    [Fact]
    public void A_hand_edited_null_list_means_still_off()
        => Assert.False(new ServerSettings { EnabledFeatures = null! }.IsEnabled(ServerFeature.VoiceCommands));

    [Theory]
    [InlineData("enable voice commands", true)]
    [InlineData("disable voice commands", false)]
    [InlineData("turn voice commands on for this server", true)]
    public void Admins_can_switch_it_from_chat(string message, bool on)
        => Assert.Equal(new FeatureSwitchRequest(ServerFeature.VoiceCommands, on), SettingsRequestReader.Read(message));

    [Fact]
    public void Auto_join_has_to_be_turned_on_and_needs_voice_commands_and_the_chatbot()
    {
        var settings = new ServerSettings();
        Assert.True(ServerSettings.IsOptIn(ServerFeature.VoiceAutoJoin));
        Assert.Null(settings.AutoJoinChannel.ChannelId);
        Assert.False(settings.AutoJoinsVoice);

        settings.Set(ServerFeature.VoiceAutoJoin, true);
        Assert.Equal(["VoiceAutoJoin"], settings.EnabledFeatures);
        Assert.False(settings.AutoJoinsVoice); // voice commands still off

        settings.Set(ServerFeature.VoiceCommands, true);
        Assert.True(settings.AutoJoinsVoice);

        settings.Set(ServerFeature.Chat, false);
        Assert.False(settings.AutoJoinsVoice); // every voice request goes through the chatbot
    }

    [Theory]
    [InlineData("disable voice auto-join", false)]
    [InlineData("disable auto join", false)]
    [InlineData("enable auto-joining", true)]
    [InlineData("turn off voice auto join for this server", false)]
    [InlineData("turn auto-join voice on here", true)]
    [InlineData("enable auto joining voice", true)]
    public void Admins_can_switch_auto_join_from_chat(string message, bool on)
        => Assert.Equal(new FeatureSwitchRequest(ServerFeature.VoiceAutoJoin, on), SettingsRequestReader.Read(message));
}

/// <summary>The bot's "🎙️ @name: what they said" posts, read back as that person talking when they're in the
/// history the chatbot sees.</summary>
public class HeardTextTests
{
    private sealed class Speaker(ulong id) : Discord.IUser
    {
        public ulong Id { get; } = id;
        public string Mention => $"<@{Id}>";
        public string AvatarId => "";
        public string Discriminator => "0000";
        public ushort DiscriminatorValue => 0;
        public bool IsBot => false;
        public bool IsWebhook => false;
        public string Username => "speaker";
        public Discord.UserProperties? PublicFlags => null;
        public string GlobalName => "Speaker";
        public string AvatarDecorationHash => "";
        public ulong? AvatarDecorationSkuId => null;
        public Discord.PrimaryGuild? PrimaryGuild => null;
        public DateTimeOffset CreatedAt => default;
        public Discord.UserStatus Status => Discord.UserStatus.Online;
        public IReadOnlyCollection<Discord.ClientType> ActiveClients => [];
        public IReadOnlyCollection<Discord.IActivity> Activities => [];
        public string GetAvatarUrl(Discord.ImageFormat format = Discord.ImageFormat.Auto, ushort size = 128) => "";
        public string GetDefaultAvatarUrl() => "";
        public string GetDisplayAvatarUrl(Discord.ImageFormat format = Discord.ImageFormat.Auto, ushort size = 128) => "";
        public string GetAvatarDecorationUrl() => "";
        public Task<Discord.IDMChannel> CreateDMChannelAsync(Discord.RequestOptions? options = null)
            => throw new NotSupportedException();
    }

    [Theory]
    [InlineData("make an LFG for ranked")]
    [InlineData("play *NSYNC [live] - Bye Bye Bye")] // markdown and link brackets, escaped in the post
    [InlineData("what's 2 > 1? #facts")]
    public void Reads_back_who_said_what(string said)
    {
        var post = EventHandlers.ChatHandler.HeardText(new Speaker(42), said);

        Assert.Equal((42UL, said), EventHandlers.ChatHandler.ReadHeardText(post));
    }

    [Theory]
    [InlineData("Hey <@42>: not one of mine")]
    [InlineData("🎙️ Listening for voice commands in <#1>")]
    public void Ignores_the_bots_other_posts(string content) => Assert.Null(EventHandlers.ChatHandler.ReadHeardText(content));
}

/// <summary>The one line the bot posts on joining voice.</summary>
public class ListeningLineTests
{
    [Fact]
    public void Says_its_listening_what_happens_to_speech_and_how_to_stop_it()
    {
        var line = Modules.Voice.VoiceListener.ListeningLine("ModBot");

        Assert.Contains("hey ModBot", line);
        Assert.Contains("no audio is kept", line);
        Assert.Contains("stop listening", line);
        Assert.True(line.Length <= Discord.DiscordConfig.MaxMessageSize);
    }
}
