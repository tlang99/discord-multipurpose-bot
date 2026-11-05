# Voice commands

The bot can sit in a voice channel and listen for its name: "hey ModBot, make an LFG for ranked", "hey ModBot, remind
me in 20 minutes to check the oven". Whatever is said after its name is handled exactly like the same words typed as
an @mention, by the same person, with the same rules. It posts what it heard in the voice channel's chat (so everyone
sees what it acted on, and anything it misheard) and replies to that.

## Privacy

- Each server has to turn it on: `/settings feature` → Voice commands (off by default). Then someone in a voice
  channel starts it with `/voice listen`, or by asking the chatbot ("@ModBot listen in voice").
- When it starts, it posts a notice in the voice channel's chat saying it's listening and how to stop it.
- Speech is turned into text **on the machine running the bot** (Whisper). Nothing is sent to any other service.
- No audio is kept: each sentence is turned into text, checked for the bot's name, and thrown away. Only what's said
  after the name is acted on and posted. The log notes that a voice request happened, never what was said.
- It stops when anyone in the channel says "hey ModBot, stop listening" or uses `/voice stop`, when everyone leaves,
  after `Voice:MaxListenMinutes` (3 hours by default), or when music starts.

## Music

Discord gives a bot one voice connection per server, and music (Lavalink) needs it to play. So the bot listens only
while no music is playing: starting music stops the listening, and it won't start listening while music plays.

## Setup (Windows)

```
powershell -ExecutionPolicy Bypass -File voice/setup.ps1
```

This downloads, into `voice/natives/` and `voice/models/` (neither is committed):

| File | What | From |
| --- | --- | --- |
| `libdave.dll` | Discord's voice encryption (DAVE), required for voice | [discord/libdave releases](https://github.com/discord/libdave/releases) |
| `libsodium.dll` | The voice connection's encryption | [libsodium on NuGet](https://www.nuget.org/packages/libsodium) |
| `opus.dll` | The Opus audio codec | [OpusSharp.Natives](https://github.com/AvionBlock/OpusSharp/tree/master/OpusSharp.Natives/runtimes/win-x64/native) (the build Discord.Net's own audio sample uses) |
| `ggml-base.en.bin` | Whisper's small English speech-to-text model, ~140 MB | [whisper.cpp models](https://huggingface.co/ggerganov/whisper.cpp) |

Then rebuild or restart the bot (`dotnet run` copies the libraries next to it). The log says at startup if anything is
missing ("Voice commands aren't available: ..."), and so does `/voice listen`.

Speech-to-text takes about a second of CPU per sentence; no graphics card is needed.

## Settings (`Voice` in appsettings.json)

| Key | Default | |
| --- | --- | --- |
| `Enabled` | `true` | `false` turns voice commands off for every server. |
| `ModelPath` | `voice/models/ggml-base.en.bin` | The Whisper model. A bigger one (e.g. `ggml-small.en.bin`) hears better but is slower. |
| `PauseMilliseconds` | `800` | How long a pause ends a sentence. |
| `MaxSpeechSeconds` | `15` | Longest stretch turned into text at once. |
| `MaxListenMinutes` | `180` | Stop listening after this long (`0` = never). |

## Patched Discord.Net

Discord.Net 3.20.2 can't decrypt voice in a channel that already has 2+ people in it
([issue #3281](https://github.com/discord-net/Discord.Net/issues/3281)), so the bot uses 3.20.2 with the unreleased
fix applied. `discord-net-patch/build.ps1` builds it once from Discord.Net's official source (checked against the
release commit) plus `pr3288.patch`, two small changes you can read. Nothing prebuilt is committed.
