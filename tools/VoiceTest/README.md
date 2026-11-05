# Voice test

A proof of concept for voice commands, separate from the bot. It answers one question: can the bot hear people in a
voice channel and turn what they say into text, on this PC? It joins a voice channel, listens for a while, leaves,
and prints what each person said. Nothing is saved: the audio is kept in memory only until the tool exits.

## Setup (once)

Discord.Net needs three native libraries next to the exe. Put them in `tools/VoiceTest/natives/` (not committed):

| File | Where from |
| --- | --- |
| `libdave.dll` | [discord/libdave releases](https://github.com/discord/libdave/releases): `libdave-Windows-X64-boringssl.zip`, file `bin/libdave.dll`. Discord's own build of its voice encryption (DAVE), which Discord requires for voice. |
| `libsodium.dll` | [libsodium on NuGet](https://www.nuget.org/packages/libsodium): the `.nupkg` is a zip, file `runtimes/win-x64/native/libsodium.dll`. |
| `opus.dll` | [OpusSharp.Natives](https://github.com/AvionBlock/OpusSharp/tree/master/OpusSharp.Natives/runtimes/win-x64/native): the audio codec. (The same sources Discord.Net's own audio sample uses.) |

The speech-to-text model (Whisper `base.en`, ~140 MB) downloads itself into `tools/VoiceTest/models/` on first run.

### Patched Discord.Net

Discord.Net 3.20.2 can't decrypt voice when the bot joins a channel that already has 2+ people in it: it rejects
Discord's encryption-group invite, hears only garbage, and is disconnected after a few seconds
([issue #3281](https://github.com/discord-net/Discord.Net/issues/3281)). The fix,
[PR #3288](https://github.com/discord-net/Discord.Net/pull/3288), isn't released yet, so this tool uses 3.20.2 with
that fix applied (`discord-net-pr3288.patch`), built locally as `3.20.2-dave.1`:

```
powershell -File tools/VoiceTest/build-discord-net.ps1
```

Only this tool uses it; the bot itself stays on the official release. Once a Discord.Net release includes the fix,
switch back to the official package and delete the script, the patch and `nuget.config`.

## Running it

From the repo folder (so it finds `appsettings.json` and the bot's token):

```
dotnet run --project tools/VoiceTest -- <server id> <voice channel id> [seconds]
```

- Join the voice channel first, then run it, and talk while it says it's listening (20 seconds by default).
- Use a server where the bot isn't playing music: it's the same bot, and joining would take over Lavalink's connection.
- The main bot can keep running. This logs in as a second session of the same bot.
