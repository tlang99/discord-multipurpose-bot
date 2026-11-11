# Lavalink (music server)

The bot's music commands don't stream audio themselves. They control [Lavalink](https://github.com/lavalink-devs/Lavalink),
a separate Java program that fetches songs from YouTube and sends the audio to Discord. Lavalink needs to
be running on the same machine as the bot for `/play` to work. Everything else in the bot works without it.

## One-time setup

1. **Install Java 17 or newer.** For example [Eclipse Temurin 21](https://adoptium.net/temurin/releases/?version=21)
   (choose the Windows x64 `.msi`, and tick "Set JAVA_HOME" in the installer). Check it with `java -version`.
2. **Download Lavalink.** From the [Lavalink releases](https://github.com/lavalink-devs/Lavalink/releases), download
   `Lavalink.jar` (4.2.2 or newer) into this `lavalink/` folder. It's gitignored.

The YouTube plugin listed in `application.yml` is downloaded automatically into `lavalink/plugins/` on first start.

3. **Sign Lavalink in to YouTube with a throwaway Google account.** Without this, YouTube answers most requests
   with "Sign in to confirm you're not a bot". **Don't use your real account**: the plugin warns the account can get
   terminated.
   1. Create a new Google account (2FA is fine; it's only used during this sign-in).
   2. Start Lavalink. Its log shows `go to https://www.google.com/device and enter code XXX-XXX-XXX`.
   3. Open that page, sign in with the throwaway account, enter the code and approve.
   4. Lavalink then logs `Token retrieved successfully ... (<refresh token>)`. Create `lavalink/secrets.yml`
      (gitignored) with:
      ```yaml
      plugins:
        youtube:
          oauth:
            refreshToken: "<refresh token>"
      ```
   From then on, Lavalink starts with `YouTube access token refreshed successfully` and doesn't ask again. Repeat
   these steps if that account's password changes or its access is revoked. The token works like a password, so
   don't share it or commit it.

## Running it

Start Lavalink before (or alongside) the bot, from this folder:

```powershell
cd lavalink
java -jar Lavalink.jar
```

It's ready when the log shows `Lavalink is ready to accept connections.` Leave the window open while the bot runs.
If the bot starts first, it keeps retrying the connection in the background.

**In Docker** (`docker-compose.yml`), Lavalink runs in the official `ghcr.io/lavalink-devs/lavalink` image of the
same version, with this folder's `application.yml` and `secrets.yml` mounted in, so the YouTube sign-in carries over
as it is. No Java or jar is needed on the server. Compose makes it listen on the container network instead of
`127.0.0.1`, sets its password from `LAVALINK_PASSWORD` in `.env`, and keeps the downloaded plugin in a volume.

## Changing the password

Lavalink only listens on `127.0.0.1`, so the default password is fine on a home PC. If you do change it, set it on
both sides:

```powershell
$env:LAVALINK_SERVER_PASSWORD = "<new password>"   # before java -jar Lavalink.jar
$env:LAVALINK_PASSWORD = "<new password>"          # before dotnet run (or dotnet user-secrets set "Music:LavalinkPassword" ...)
```

## When YouTube stops working

YouTube regularly changes things in ways that break playback. When `/play` finds songs but they won't play (the bot
posts "YouTube wouldn't let me play ..."):

1. Check `lavalink/logs/spring.log` for `All clients failed to load the item`: it lists why each YouTube client
   failed ("Sign in to confirm you're not a bot" means the sign-in above is missing or expired).
2. Check the [youtube-source releases](https://github.com/lavalink-devs/youtube-source/releases) for a newer
   version, put its number in `application.yml` (`dev.lavalink.youtube:youtube-plugin:<version>`, `snapshot:
   false`), and restart Lavalink. The old plugin file is replaced automatically.
3. If there's no new release yet, use an unreleased build: take the full commit ID of the newest commit on the
   plugin's [main branch](https://github.com/lavalink-devs/youtube-source/commits/main) as the version, with
   `snapshot: true`. **This is what `application.yml` currently does**, because release 1.18.2 can't decode YouTube's
   current player script. Switch back to a normal release once one newer than 1.18.2 is out.
4. If none of that works, the plugin's README has further workarounds (`clients`, `poToken`, and a remote cipher
   server, [yt-cipher](https://github.com/kikkia/yt-cipher)).

SoundCloud is turned off (`soundcloud: false` in `application.yml`) because it's broken: SoundCloud only serves a
format Lavalink's audio library can't play yet ([lavaplayer#202](https://github.com/lavalink-devs/lavaplayer/pull/202)).
To bring it back once a Lavalink release includes that fix: turn it on again in `application.yml`, then add
`soundcloud.com` to the allowed link hosts in `Modules/Music/MusicModule.cs` and remove the SoundCloud message in
`/play`.
