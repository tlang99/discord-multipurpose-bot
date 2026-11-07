# Voice commands

The bot can sit in a voice channel and listen for its name: "hey ModBot, make an LFG for ranked", "hey ModBot, remind
me in 20 minutes to check the oven". Whatever is said after its name is handled exactly like the same words typed as
an @mention, by the same person, with the same rules. It answers out loud, without repeating the answer in chat;
anything the request makes is still posted (a poll, an LFG card), and a web search posts the same card `/search`
does, under "🎙️ @name searched: …". When it can't speak (no **Speak** permission, or text-to-speech isn't set up),
it posts what it heard in the voice channel's chat ("🎙️ @name: …", so everyone sees what it acted on, and anything it
misheard) and replies to that instead.

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
  off for every server with `Voice:Chime`). An action that worked (playing, skipping or stopping music, adding or
  removing a song, a reminder, an LFG or a poll) is answered with a three-note "done" chime (low, high, low) instead
  of words, so it doesn't talk over the music. A reminder is also posted in the voice channel's chat (with its time
  and how to cancel it), since nothing else would show it. If it couldn't do it, it says why. Questions (what's
  playing, the queue, a web search, chat) are answered aloud. With `Voice:Chime` off, it says what it did, as before.
- It reads the first few sentences of its answer aloud, without links, emoji or formatting (an answer with nothing to
  say aloud, like only a link, is posted instead). Typed requests are only answered in chat. While it's talking, and
  for a moment after, it only listens for a goodbye said to it by name ("thanks ModBot", "ModBot, stop"), which cuts
  it off; anything else is ignored, so its own voice coming back through someone's mic isn't taken for a request
  (wait for it to finish before asking again). An answer that would have to wait more than 15 seconds behind another
  is posted instead. Needs **Speak**; turn it off for every server with `Voice:SpokenReplies`. After installing Piper,
  restart the bot.
- **Conversations:** after an answer read aloud, whoever said "hey ModBot" can say the next thing without the name
  ("and what about tomorrow?"), and the bot remembers what was said and answered so far, in memory only. Anyone else
  still says the name, and then the conversation is with them. `/settings voice-follow-ups` lets anyone in the
  channel reply without the name instead (then everything said while it's open counts as a request, side chatter
  included). It ends after `Voice:ConversationSeconds` (10 by default) with nobody it's talking with saying anything
  once it's finished, or when they say "thanks", "goodbye", "that's all" or "stop" (anyone can, by name: "thanks
  ModBot"; even over the bot, or while it's still working on an answer: it stops talking, and answers on their way
  aren't said); then a closing chime (the chime falling) says it's back to waiting for its name, and the
  conversation is forgotten. Replies skip the chatbot's usual wait between questions (`Chat:CooldownSeconds`),
  but each person's are still at least `Voice:FollowUpCooldownSeconds` (5 by default) apart.
- An answer is only said aloud when everyone in the voice channel can read the channel the bot answers in, and its
  history (the chatbot reads that channel's recent messages, so an answer could repeat them). If anyone can't, it's
  posted there instead, as typed answers are.
- Speech is turned into text, and answers into speech, **on the machine running the bot** (Whisper and Piper).
  Nothing is sent to any other service. (A web search sends only the search words to Ollama, as typed searches do.)
- No audio is kept: each sentence is held in memory only until it's turned into text (about a second), checked for
  the bot's name, and thrown away. Only what's said after the name, or in a conversation with it (above), is acted
  on. The log notes that a voice request happened, never what was said, unless `Voice:LogRequests` is turned on to
  work out what it mishears: then each request is logged as heard (in a conversation open to anyone, that's
  everything said in the channel), and the line posted on joining says so. Nothing else is ever logged.
- Server settings (turning features on or off, news notifications) can't be changed by voice, only by typing: a
  friend near an admin's mic, a soundboard or a mishearing mustn't change them as that admin.
- It only listens while the chatbot is on, since every request goes through it.
- It stops when anyone in the channel says "hey ModBot, stop listening" or uses `/voice stop`, when everyone leaves,
  or when music starts (unless music has its own bot account, see [Music](#music)). Started by hand, it also stops
  after `Voice:MaxListenMinutes` (3 hours by default).
- Told to stop (or taken out of the channel by a moderator), it won't join that channel by itself again until
  everyone has left it. Asking it to listen brings it straight back.

## Music

Discord gives a bot one voice connection per server, and music (Lavalink) needs one to play. Two ways to run it:

- **One bot (the default):** the bot listens only while no music is playing. Starting music stops the listening,
  and it won't start listening while music plays. With voice auto-join on, it comes back once the music player
  leaves voice (`Music:IdleTimeoutSeconds` after the last song, or straight after `/stop`).
- **A second bot account for music:** music and listening run at once. The second account only joins voice and
  plays: `/play`, the music menu and "hey ModBot, play …" all stay on the main bot. While the bot reads an answer
  aloud, it turns the music down, and back up after.

To set up the second account:

1. In the [Discord Developer Portal](https://discord.com/developers/applications), make a new application (say,
   "ModBot Music"), and under **Bot**, reset and copy its token. It needs no privileged intents.
2. Give the token to the bot, like the main one:
   `dotnet user-secrets set "Bot:MusicToken" "<music-bot-token>"` (or the `MUSIC_BOT_TOKEN` environment variable),
   then restart.
3. Invite it to each server. Until it's there, `/play` replies with its invite link (it asks for View Channel,
   Connect and Speak only). The startup log lists the servers it's in ("Music plays from …").

Remove the token to go back to one bot.

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
| `en_US-kusal-medium.onnx` (+ `.onnx.json`) | Piper's "kusal" US English voice (male), ~63 MB | [piper-voices](https://huggingface.co/rhasspy/piper-voices/tree/main/en/en_US/kusal/medium) |

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
| `SpeakingVoicePath` | `voice/models/en_US-kusal-medium.onnx` | The voice it speaks with; any [Piper voice](https://huggingface.co/rhasspy/piper-voices) works (keep its `.onnx.json` next to it). |
| `MaxSpokenCharacters` | `300` | How much of an answer is read aloud, in whole sentences; the rest isn't said. |
| `ConversationSeconds` | `10` | How long after its last answer it can be replied to without its name (0-60; `0` = no conversations). |
| `FollowUpCooldownSeconds` | `5` | In a conversation, the least time between one person's requests (0-60). A reply said sooner is dropped. |
| `LogRequests` | `false` | Write each voice request, as heard, to the log, to work out why one wasn't understood. The line posted on joining tells people. |

## Patched Discord.Net

Discord.Net 3.20.2 can't decrypt voice in a channel that already has 2+ people in it
([issue #3281](https://github.com/discord-net/Discord.Net/issues/3281)), so the bot uses 3.20.2 with the unreleased
fix applied. `discord-net-patch/build.ps1` builds it once from Discord.Net's official source (checked against the
release commit) plus `pr3288.patch`, two small changes you can read. Nothing prebuilt is committed.
