# Voice commands

The bot can sit in a voice channel and listen for its name: "hey ModBot, make an LFG for ranked", "hey ModBot, remind
me in 20 minutes to check the oven". Whatever is said after its name is handled exactly like the same words typed as
an @mention, by the same person, with the same rules. It posts what it heard in the voice channel's chat (so everyone
sees what it acted on, and anything it misheard) and replies to that.

## Privacy

- Each server has to turn it on: `/settings feature` → Voice commands (off by default).
- Then someone starts it by hand with `/voice listen`, or by asking the chatbot ("@ModBot listen in voice").
- **Voice auto-join**, also off until a server turns it on (`/settings feature` → Voice auto-join, or "@ModBot enable
  voice auto-join"): the bot joins a voice channel by itself a few seconds after someone does, so "hey ModBot" is all
  it takes. With several channels in use, it joins the busiest one (one voice connection per server).
  `/settings voice-channel` limits it to one channel.
- Each time it joins, it posts one line in the voice channel's chat: that it's listening for "hey ModBot", that speech
  is turned into text on the bot's own computer and no audio is kept, and how to send it away. While it's there, the
  channel's status (shown under its name in the channel list) reads "🎙️ Listening for "hey ModBot"", so anyone
  joining later sees it too; the old status comes back when it leaves. No "stopped" notices.
- **Voice auto-join needs Set Voice Channel Status**, since the status is how people joining later know it's
  listening: without it, the bot doesn't join by itself (`/voice listen` still works, without the status).
- When it hears its name, it plays a short chime, so you know it caught it (needs **Speak** in the channel; turn it
  off for every server with `Voice:Chime`).
- It reads the first few sentences of its answer aloud (the whole answer is in chat too), without links, emoji or
  formatting. Typed requests are only answered in chat. While it's talking, and for a moment after, it ignores what it
  hears, so its own voice coming back through someone's mic isn't taken for a request. Needs **Speak**; turn it off
  for every server with `Voice:SpokenReplies`.
- Speech is turned into text, and answers into speech, **on the machine running the bot** (Whisper and Piper).
  Nothing is sent to any other service. (A web search sends only the search words to Ollama, as typed searches do.)
- No audio is kept: each sentence is held in memory only until it's turned into text (about a second), checked for
  the bot's name, and thrown away. Only what's said after the name is acted on and posted. The log notes that a voice
  request happened, never what was said.
- Server settings (turning features on or off, news notifications) can't be changed by voice, only by typing: a
  friend near an admin's mic, a soundboard or a mishearing mustn't change them as that admin.
- It only listens while the chatbot is on, since every request goes through it.
- It stops when anyone in the channel says "hey ModBot, stop listening" or uses `/voice stop`, when everyone leaves,
  or when music starts. Started by hand, it also stops after `Voice:MaxListenMinutes` (3 hours by default).
- Told to stop (or taken out of the channel by a moderator), it won't join that channel by itself again until
  everyone has left it. Asking it to listen brings it straight back.

## Music

Discord gives a bot one voice connection per server, and music (Lavalink) needs it to play. So the bot listens only
while no music is playing: starting music stops the listening, and it won't start listening while music plays. With
voice auto-join on, it comes back once the music player leaves voice (`Music:IdleTimeoutSeconds` after the last song,
or straight after `/stop`).

## Setup (Windows)

```
powershell -ExecutionPolicy Bypass -File voice/setup.ps1
```

This downloads, into `voice/natives/`, `voice/models/` and `voice/piper/` (none of them committed), each pinned to
an exact version and checked against its SHA-256:

| File | What | From |
| --- | --- | --- |
| `libdave.dll` | Discord's voice encryption (DAVE), required for voice | [discord/libdave releases](https://github.com/discord/libdave/releases) |
| `libsodium.dll` | The voice connection's encryption | [libsodium on NuGet](https://www.nuget.org/packages/libsodium) |
| `opus.dll` | The Opus audio codec | [OpusSharp.Natives](https://github.com/AvionBlock/OpusSharp/tree/master/OpusSharp.Natives/runtimes/win-x64/native) (the build Discord.Net's own audio sample uses) |
| `ggml-base.en.bin` | Whisper's small English speech-to-text model, ~140 MB | [whisper.cpp models](https://huggingface.co/ggerganov/whisper.cpp) |
| `piper/` | Piper, the text-to-speech program for spoken replies, ~22 MB | [rhasspy/piper releases](https://github.com/rhasspy/piper/releases/tag/2023.11.14-2) |
| `en_US-lessac-medium.onnx` (+ `.onnx.json`) | Piper's "lessac" US English voice, ~63 MB | [piper-voices](https://huggingface.co/rhasspy/piper-voices/tree/main/en/en_US/lessac/medium) |

Then rebuild or restart the bot (`dotnet run` copies the libraries next to it). The log says at startup if anything is
missing ("Voice commands aren't available: ..."), and so does `/voice listen`.

Speech-to-text takes about a second of CPU per sentence, and speaking an answer about as long; no graphics card is
needed. Without Piper, voice commands still work and answer in chat only (the startup log says "Spoken replies aren't
available").

## Settings (`Voice` in appsettings.json)

| Key | Default | |
| --- | --- | --- |
| `Enabled` | `true` | `false` turns voice commands off for every server. |
| `ModelPath` | `voice/models/ggml-base.en.bin` | The Whisper model. A bigger one (e.g. `ggml-small.en.bin`) hears better but is slower. |
| `PauseMilliseconds` | `800` | How long a pause ends a sentence. |
| `MaxSpeechSeconds` | `15` | Longest stretch turned into text at once. |
| `MaxListenMinutes` | `180` | Stop listening after this long (`0` = never). Only when started by hand: joined by itself, it stays until everyone's left. |
| `Chime` | `true` | Play a short chime on hearing the bot's name. Needs **Speak**; the bot shows as unmuted while it can make sounds. |
| `SpokenReplies` | `true` | Read answers to spoken requests aloud. Needs **Speak** and Piper. |
| `PiperPath` | `voice/piper/piper.exe` | Piper's program. |
| `SpeakingVoicePath` | `voice/models/en_US-lessac-medium.onnx` | The voice it speaks with; any [Piper voice](https://huggingface.co/rhasspy/piper-voices) works (keep its `.onnx.json` next to it). |
| `MaxSpokenCharacters` | `300` | How much of an answer is read aloud, in whole sentences; the rest is only in chat. |

## Patched Discord.Net

Discord.Net 3.20.2 can't decrypt voice in a channel that already has 2+ people in it
([issue #3281](https://github.com/discord-net/Discord.Net/issues/3281)), so the bot uses 3.20.2 with the unreleased
fix applied. `discord-net-patch/build.ps1` builds it once from Discord.Net's official source (checked against the
release commit) plus `pr3288.patch`, two small changes you can read. Nothing prebuilt is committed.
