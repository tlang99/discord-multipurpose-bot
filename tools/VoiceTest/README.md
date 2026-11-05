# Voice test

A standalone check, separate from the bot, that the bot can hear a voice channel and turn speech into text on this
PC: it joins a voice channel, listens for a while, leaves, and prints what each person said and how long the
speech-to-text took. Nothing is saved: the audio is kept in memory only until the tool exits. Handy for checking voice
still works after a Discord.Net or libdave update, without the rest of the bot.

## Setup

The same as voice commands in the bot: `powershell -File voice/setup.ps1` (see [voice/README.md](../../voice/README.md)).
The tool uses the bot's Discord.Net build too (`discord-net-patch/`).

## Running it

From the repo folder (so it finds `appsettings.json` and the bot's token):

```
dotnet run --project tools/VoiceTest -- <server id> <voice channel id> [seconds]
```

- Join the voice channel first, and let the people in it know it's a test.
- Use a channel where the bot isn't playing music, or listening for voice commands: it's the same bot.
- The main bot can keep running. This logs in as a second session of the same bot.
