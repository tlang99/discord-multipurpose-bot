# ModBot

A Discord moderation bot built on [Discord.Net](https://github.com/discord-net/Discord.Net), using slash commands
(`Discord.Net.Interactions`) and the .NET Generic Host for configuration/DI. The architecture is intentionally
modular so new feature areas can be added later without touching moderation code.

## Features

**Moderation commands** (all permission-gated, see below): `/kick`, `/ban`, `/unban`, `/timeout`, `/untimeout`,
`/warn`, `/warnings`, `/clearwarnings`, `/purge`.

**Server configuration**: `/modlog set|disable|show` (where moderation actions get logged) and `/automod`, which
controls every aspect of passive content filtering — see the table below.

**Auto-moderation** runs on every incoming message (members with `Manage Messages` are exempt) and checks, in
order:
1. **Invite links** — optional, off by default (`/automod block-invites`).
2. **Custom banned words** — your own list, managed with `/automod word-add|word-remove|word-list`.
3. **Built-in profanity filter** — a curated list of common English profanity, on by default
   (`/automod profanity-filter`).
4. **Image/GIF content moderation** — directly-uploaded image and GIF attachments, plus raw image/GIF URLs pasted
   in a message, are sent to [Sightengine](https://sightengine.com) for classification. Independently togglable:
   explicit content, suggestive-but-not-explicit content, and offensive content (hate symbols, obscene gestures)
   — see `/automod image-moderation`, `block-explicit-images`, `block-suggestive-images`, `block-offensive-images`,
   and `scan-nsfw-channels`. Requires Sightengine API credentials (see Setup step 3) — without them this check is
   skipped entirely.
   > **Known gap:** pasting a Tenor/Giphy link gets re-encoded by Discord into a video (mp4) embed, which this
   > endpoint can't read, so those aren't scanned yet. Covering that would mean hooking the `MessageUpdated` event
   > (embeds populate after the initial send) and calling a video-capable moderation endpoint instead.

Any auto-moderation removal is logged to the mod-log channel just like a manual action, with the reason noted.

**Gaming news** (`/news`) — follow a game and new articles get posted to a channel of your choice as embeds:
- `/news follow-game game:<game> channel:#channel` — built-in game feeds (currently Call of Duty: Modern Warfare 4,
  via a Bing News search, which includes each article's summary snippet and thumbnail in the embed). Run it again
  with a different channel to move it.
- `/news follow-feed name url channel` — any other RSS/Atom feed (`https://` only).
- `/news list`, `/news unfollow name`, `/news latest name [count]` (preview the newest articles, only visible to you).
- `/news filter-add|filter-remove|filter-show name` — keyword filter per feed. *Include* keywords: only articles
  with one in the headline get posted (none = post everything). *Exclude* keywords: articles with one in the
  headline are skipped. Only headlines are checked, since summaries mention update words in passing. Built-in
  games start with a filter tuned for content-update news (seasons, battle pass, maps, weapons, patches, events)
  that skips reviews, guides, loadouts, and business/politics stories.

Feeds are checked every 15 minutes (`News:PollIntervalMinutes` in `appsettings.json`). Articles already in a feed
when you follow it are skipped, so only genuinely new ones get posted; at most `News:MaxPostsPerPoll` (default 5)
are posted per feed per check so a burst of news can't flood the channel. To add another built-in game, add an
entry to `Services/News/NewsPresets.cs`.

**Music** — plays YouTube audio in a voice channel, through a [Lavalink](lavalink/README.md) server that runs
alongside the bot (and needs a one-time sign-in with a throwaway Google account, see its README):
- `/play query` — a song name (searches YouTube), or a YouTube song or playlist link. Joins your voice channel, or
  adds to the end of the queue if something's already playing.
- `/pause`, `/resume`, `/skip [count]`, `/stop` (clears the queue and leaves), `/nowplaying`, `/queue [page]`.
- `/remove position`, `/move from to`, `/shuffle`, `/loop off|song|queue`, `/clearqueue`.

Anyone can use these, but commands that change playback only work from the bot's voice channel, except for
moderators (Move Members or Manage Messages), who can control it from anywhere. `/play` only accepts YouTube links. Each new song is
announced in the text channel where `/play` was first used. The bot leaves after `Music:IdleTimeoutSeconds`
(default 2 minutes) with nobody listening or nothing playing, and the queue is capped at `Music:MaxQueueLength`
(default 200). If Lavalink isn't running, the music commands say so and everything else keeps working.

> YouTube's terms of service don't allow streaming its audio outside YouTube, and YouTube changes often enough that
> playback breaks from time to time — see [lavalink/README.md](lavalink/README.md#when-youtube-stops-working).
> SoundCloud is currently unsupported: Lavalink can't play its audio format yet, so `/play` explains that for
> SoundCloud links.

**Looking for group** — `/lfg` posts a Multiplayer LFG card, e.g. `/lfg players:4 playlist:Ranked Play rank:Gold+
mic:True`. Every option is optional:
- `players` — squad size 2–6 including you (default 4).
- `playlist` — Ranked Play, Hardpoint, Search & Destroy, Team Deathmatch, Domination, Kill Confirmed, Hardcore, or
  any. `rank` — minimum Ranked Play division (picking one implies Ranked Play).
- `mic` — whether a mic is required. `platform` — crossplay (default), PC only, or console only.
- `note` — free text, up to 100 characters.

The chosen details show as tags on the card (e.g. "🏆 Gold+ · 🎧 Mic required · 🖥️ PC only"). `/lfg` only works
in the channel named by `Lfg:ChannelName` (default `lfg-mw4`; empty allows any channel). Elsewhere it replies
privately with a link to that channel. The card has buttons:
- **Join** / **Leave** — anyone. The squad list on the card updates as people come and go.
- **Start now** — host only: close early with whoever has joined (needs at least one other player).
- **Cancel** — the host, or a moderator (Manage Messages or Move Members) to remove anyone's post.

When the last spot fills, or the host starts early, the bot pings everyone in the squad. Posts that don't fill up
expire after `Lfg:ExpiryMinutes` (default 60). Each person can have one open post per server. Posts are kept in
memory only, so after the bot restarts, old posts' buttons just say the post is no longer active.

**LFG pings** — run `/lfg-setup` (Manage Server) in the LFG channel once. It creates the `@LFG` role
(`Lfg:PingRoleName`) if needed, and posts and pins a panel with **Get LFG pings** / **Stop LFG pings** buttons.
New `/lfg` posts then ping that role, at most once every `Lfg:PingCooldownMinutes` (default 10) per person, so
posting and cancelling repeatedly can't spam everyone. The bot needs **Manage Roles**, and its own role must be
above `@LFG` in Server Settings → Roles. Because members give themselves `@LFG`, it must stay a plain role. If it
gains any permission beyond `@everyone`'s, or a channel override that grants something, the bot refuses to hand
it out (in `/lfg-setup` and the panel) until that's fixed. Removing the role always works.

## Project layout

```
Configuration/     Strongly-typed options bound from appsettings/env/secrets
Models/            Plain data types (GuildSettings, Warning, ModerationResult)
Services/
  Storage/         IGuildSettingsStore / IWarningStore + a JSON-file implementation
  Moderation/      IModerationService - moderation actions, independent of Discord.Net modules
  ContentModeration/ IImageModerationService - image/GIF classification, implemented against Sightengine
  News/            INewsFeedReader (RSS/Atom parsing), built-in game feed presets, news embed formatting
  Lfg/             ILfgService - LFG post rules (join/leave/start/cancel/expiry), in memory, no Discord types
EventHandlers/     Gateway event handlers (e.g. AutoModerationHandler for MessageReceived)
Modules/           Slash-command modules, grouped by feature folder (auto-discovered at startup)
  Music/           Music commands plus the Lavalink player that announces songs (Lavalink4NET does the rest)
  Lfg/             /lfg command, its button handlers, and the LFG card's embed/buttons
lavalink/          Config for the Lavalink music server (the server itself is downloaded, not committed)
Hosting/           DiscordBotService - owns the client lifecycle and wires events to handlers
                   NewsPollingService - background timer that posts new articles for /news subscriptions
                   LfgExpiryService - closes LFG posts that didn't fill up in time
```

### Adding a new feature later

1. Add a new subfolder under `Modules/` with an `InteractionModuleBase<SocketInteractionContext>` class — it's
   picked up automatically via assembly scanning (`AddModulesAsync`), no registration needed.
2. If it needs state, add a model under `Models/` and a store under `Services/Storage/`, and register it in
   `Program.cs`.
3. If it needs business logic independent of Discord (so it's testable/reusable), put that in `Services/<Feature>/`
   rather than directly in the module, following the pattern `IModerationService` uses.
4. If it needs a gateway event (message edits, reactions, member joins, etc.), add a handler class under
   `EventHandlers/` and wire the event in `DiscordBotService`.

Storage is JSON-file-based for simplicity (`data/guild-settings.json`, `data/warnings.json`). To move to a real
database later, implement `IGuildSettingsStore`/`IWarningStore` against it and swap the registration in
`Program.cs` — nothing else needs to change.

Likewise, `IImageModerationService` is a thin seam over the image/GIF moderation backend (currently Sightengine).
Swapping providers later means writing a new implementation of that interface and changing one line in
`Program.cs` — the auto-moderation handler and slash commands don't know which backend is behind it.

## Setup

### 1. Create the Discord application

1. Go to the [Discord Developer Portal](https://discord.com/developers/applications) and create a new application.
2. Under **Bot**, create a bot user and copy its token (keep this secret).
3. Under **Bot → Privileged Gateway Intents**, enable **Server Members Intent** and **Message Content Intent**.
   Both are required (member lookups for moderation commands, and message scanning for auto-mod).
4. Under **OAuth2 → URL Generator**, select the `bot` and `applications.commands` scopes, and under bot
   permissions select at least: `Kick Members`, `Ban Members`, `Moderate Members`, `Manage Messages`,
   `View Channels`, `Send Messages`, `Read Message History`, `Embed Links` (for `/news` and music), `Connect` and
   `Speak` (for music). Use the generated URL to invite the bot to your server. If the bot is already in your
   server, give its role `Connect` and `Speak` instead of re-inviting it.

### 2. Configure the token

Never put the real token in `appsettings.json` (it's committed to source control). Use one of:

```bash
# .NET user-secrets (recommended for local dev)
dotnet user-secrets set "Bot:Token" "<your-token>"

# or an environment variable
set BOT_TOKEN=<your-token>          # Windows cmd
$env:BOT_TOKEN = "<your-token>"     # PowerShell
```

For faster iteration while developing, also set a test guild so slash commands register instantly instead of
waiting up to an hour for global propagation:

```bash
dotnet user-secrets set "Bot:TestGuildId" "<your-server-id>"
```

Remove `Bot:TestGuildId` (or leave it unset) once you're ready to register commands globally.

### 3. (Optional) Configure image/GIF moderation

Image and GIF scanning (`/automod image-moderation`) needs a [Sightengine](https://sightengine.com) account:

1. Sign up at sightengine.com and grab your **API User** and **API Secret** from the dashboard (a free tier is
   available; usage beyond it is billed per check, and animated GIFs cost proportionally more since every frame
   counts as an operation).
2. Set the credentials the same way as the bot token:

```bash
dotnet user-secrets set "Sightengine:ApiUser" "<api-user>"
dotnet user-secrets set "Sightengine:ApiSecret" "<api-secret>"

# or environment variables
$env:SIGHTENGINE_API_USER = "<api-user>"     # PowerShell
$env:SIGHTENGINE_API_SECRET = "<api-secret>"
```

If these aren't set, image/GIF moderation silently does nothing (text filtering and moderation commands still
work fine) — `/automod image-moderation enable` will tell you if credentials are missing.

The sensitivity thresholds (0–1, Sightengine's own recommended range for balancing false positives/negatives) can
be tuned in `appsettings.json` under `Sightengine:ExplicitThreshold` / `SuggestiveThreshold` / `OffensiveThreshold`
— these apply bot-wide, while which categories to actually enforce (explicit/suggestive/offensive/NSFW-channel
exemption) is controlled per-server via `/automod`.

### 4. (Optional) Set up music

Music needs Java and the Lavalink server. Follow [lavalink/README.md](lavalink/README.md) once, then start Lavalink
(`java -jar Lavalink.jar` in the `lavalink/` folder) whenever you run the bot.

### 5. Run it

```bash
dotnet run
```

## Permissions reference

| Command | Required user permission | Required bot permission |
|---|---|---|
| `/kick` | Kick Members | Kick Members |
| `/ban`, `/unban` | Ban Members | Ban Members |
| `/timeout`, `/untimeout` | Moderate Members | Moderate Members |
| `/warn`, `/warnings`, `/clearwarnings` | Moderate Members | — |
| `/purge` | Manage Messages | Manage Messages |
| `/modlog`, `/automod` | Manage Server | — |
| `/news` | Manage Server | View Channel, Send Messages, Embed Links (in the news channel) |
| `/play` | — (must be in a voice channel) | View Channel, Connect, Speak (in the voice channel) |
| Other music commands | — (must be in the bot's voice channel unless you have Move Members or Manage Messages; `/queue` and `/nowplaying` work from anywhere) | — |
| `/lfg` | — (cancelling someone else's post: Manage Messages or Move Members) | View Channel, Send Messages, Embed Links (in that channel) |
| `/lfg-setup` | Manage Server | Manage Roles (and a role above `@LFG`), Send Messages, Embed Links; Manage Messages to pin the panel |
