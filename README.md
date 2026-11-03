# ModBot

A Discord moderation bot built on [Discord.Net](https://github.com/discord-net/Discord.Net), using slash commands
(`Discord.Net.Interactions`) and the .NET Generic Host for configuration/DI. The architecture is intentionally
modular so new feature areas can be added later without touching moderation code.

## Features

**Moderation commands** (all permission-gated, see below): `/kick`, `/ban`, `/unban`, `/timeout`, `/untimeout`,
`/warn`, `/warnings`, `/clearwarnings`, `/purge`.

**Server configuration**: `/modlog set|disable|show` (where moderation actions get logged) and `/automod`, which
controls every aspect of passive content filtering — see the table below.

**Auto-moderation** is off in a new server until it runs `/automod enable`. Once on, it runs on every incoming message (members with `Manage Messages` are exempt) and checks, in
order:
1. **Invite links** — optional, off by default (`/automod block-invites`).
2. **Custom banned words** — your own list, managed with `/automod word-add|word-remove|word-list`.
3. **Built-in profanity filter** — a curated list of common English profanity, off by default
   (`/automod profanity-filter`). Text filters ignore case, spaces and punctuation so "f u c k" is caught, which
   also means a filtered word can be found across or inside other words (e.g. "this hit"), so expect some false
   positives.
4. **Image/GIF content moderation** — directly-uploaded image and GIF attachments, plus raw image/GIF URLs pasted
   in a message, are sent to [Sightengine](https://sightengine.com) for classification. Independently togglable:
   explicit content, suggestive-but-not-explicit content, and offensive content (hate symbols, obscene gestures)
   — see `/automod image-moderation`, `block-explicit-images`, `block-suggestive-images`, `block-offensive-images`,
   and `scan-nsfw-channels`. Requires Sightengine API credentials (see Setup step 4) — without them this check is
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

**AI write-ups:** with the chatbot set up (see AI chat below), each article is written up by the bot in its own
words, using the server's chatbot personality (`/chat personality`). The embed still has the headline linking to the
article, the image and the date:
- **Description:** the bot's write-up.
- **"From <site>" field:** the news site's own summary, so readers can check the facts. Turn it off with
  `News:ShowOriginalSummary: false`.
- **Footer:** marks the description as an AI write-up by the bot.

The model only sees the headline and the feed's short summary, not the full article. It's told to stick to those
facts, but small models still embellish now and then, which is why the site's summary is shown by default.
- **Word filter:** while auto-mod is on (`/automod enable`), write-ups go through the server's word filter, like
  chatbot replies.
- **Links:** write-ups containing anything shaped like a link (a URL, a bare domain, a markdown link) or
  `@everyone`/`@here` are always discarded, since the feed text going into the prompt is untrusted.
- **Fallback:** if Ollama is unreachable, or a write-up breaks the filter or contains a link, that article posts with the site's summary,
  as before.
- **Preview:** `/news latest` shows write-ups too, so it doubles as a preview.
- **Turning it off:** `News:AiWriteUps: false`.

**Music** — plays YouTube audio in a voice channel, through a [Lavalink](lavalink/README.md) server that runs
alongside the bot (and needs a one-time sign-in with a throwaway Google account, see its README). Each server has
**one standing queue** that anyone can add to at any time, even when nothing's playing:
- **It's kept:** the queue is saved in `data/music-queue.json`, so songs wait through the bot leaving voice, `/stop`
  and restarts. Songs come off it as they play.
- **Turns:** songs play one from each person, in the order they first added one, round and round, so someone who
  adds ten songs doesn't hold everyone else up. Someone who adds later gets the next free turn. Each person can have
  `Music:SongsPerPerson` songs waiting (default 10), and the queue holds `Music:MaxQueueLength` (default 200).
- **Vote skip:** a song is skipped once more than half the people in the voice channel vote. Whoever picked it, and
  moderators, skip it outright.
- **The menu:** a server admin posts it once with `/music-menu` (Manage Server) in the music channel, and the bot
  pins it (if it has Pin Messages). It always shows what's playing and whose songs are up next, with buttons:
  **Add song** (a pop-up), **Play** (or **Vote skip**, **Pause**/**Resume** and **Stop** while playing) and
  **Remove mine**. The buttons keep working across restarts. Running `/music-menu` again replaces it.
- **It stays in view:** when messages build up under the menu, the bot moves it back to the bottom of the channel
  (reposts and re-pins it, deleting the old one) once the channel's been quiet for a few seconds, at most once a
  minute and within two minutes in a busy chat. With Manage Messages it also deletes Discord's "pinned a message"
  notice. Servers with a menu don't get a "Now playing" post for each song either, since the menu shows it.
  `Music:KeepMenuAtBottom: false` turns the reposting off.

Commands:
- `/add query` — a song name (searches YouTube), or a YouTube song or playlist link. Just queues it.
- `/play query` — the same, and if nothing's playing and you're in a voice channel, starts the music there.
- `/start` — plays the queue in your voice channel.
- `/skip` (a vote), `/pause`, `/resume`, `/stop` (leaves voice; the queue's kept), `/nowplaying`, `/queue [page]`.
- `/remove position` (your own songs; moderators: anyone's), `/remove-mine`, `/clearqueue` (moderators),
  `/loop off|song|queue` (looping the queue puts finished songs back in for their picker; a skip still moves on).

Adding works from anywhere. Controlling playback (pause, stop, loop, voting) only works from the bot's voice
channel, except for moderators (Move Members or Manage Messages), who can control it from anywhere. Only YouTube
links are accepted. Once a server picks a music channel with `/settings channel`, music commands only work there, so replies
(and "Now playing" posts, in servers without a menu) stay out of the main chat. Until then they work in any channel.
Elsewhere the bot replies privately pointing to that channel. The bot leaves voice after `Music:IdleTimeoutSeconds`
(default 2 minutes) with nobody listening or nothing playing; the queue stays. If Lavalink isn't running, the music
commands say so and everything else keeps working. A saved song that YouTube won't play any more is skipped with a
note.

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

The chosen details show as tags on the card (e.g. "🏆 Gold+ · 🎧 Mic required · 🖥️ PC only").
Once a server picks an LFG channel with `/settings channel`, `/lfg` only works there (until then, anywhere). Elsewhere it replies
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

**Reminders** — `/remind when:<time> what:<text>` pings you in that channel when it's due:
- **Times:** `when` takes relative times (`10m`, `2h30m`, `in 3 days`, `half an hour`) or clock times (`at 5pm`,
  `tomorrow 9am`, `tonight at 8`). A clock time already past today means tomorrow, and `at 5` with no am/pm means
  whichever comes next.
- **Time zone:** clock times are read in `Reminders:TimeZone` (an IANA or Windows name; empty uses the bot machine's
  zone). Replies use Discord timestamps, so everyone sees the time in their own zone.
- **Managing:** `/reminders list` shows yours with a short code each, and `/reminders cancel id:<code>` cancels one.
- **Limits:** between a minute and `Reminders:MaxDays` (365) away, and up to `Reminders:MaxPerUser` (25) pending per
  person per server.
- **Safety:** the text goes through the word filter when set (auto-mod on, moderators exempt), and the reminder only
  pings its owner.
- **Persistence:** reminders are saved in `data/reminders.json`. Ones that came due while the bot was offline are
  delivered once it's back and ready, marked late. If the channel can't be used any more, the bot DMs the person
  instead.
- **Retries:** if neither works (say Discord is having a moment), it retries with growing gaps of 2, 4, 8… minutes, up
  to an hour. It gives up after 10 attempts or a day.
- **Duplicates over losses:** a reminder is only removed after it's sent. If the bot stops at exactly that moment, the
  reminder is sent again on restart rather than lost.

**Polls** — `/poll question:<text> answers:<a, b, c>` posts one of Discord's native polls in that channel:
- **Answers:** up to 10, separated by commas, or by `|` if an answer has a comma in it (`Yes, tonight | No`). Leave
  `answers` out for a Yes/No poll. Repeated answers are dropped.
- **Options:** `hours` sets how long it runs (default `Polls:DefaultHours`, 24; up to 768, Discord's 32-day limit),
  and `multiple:True` lets people pick more than one answer.
- **Discord does the rest:** voting, live counts, closing on time and posting the result. The bot stores nothing.
- **Ending early:** right-click the poll → **Apps** → **End poll**. It works for whoever asked for the poll and for
  moderators (Move Members or Manage Messages). Discord only lets a poll's author end it, and that's the bot, so it
  reads who asked from the "📊 Poll by @name" line above the poll (which doesn't ping).
- **Rules:** the person needs **Send Messages** and **Create Polls** in the channel, the question and answers go
  through the word filter (auto-mod on, moderators exempt), and each person can post one poll every
  `Polls:CooldownSeconds` (60).

**Command list** — `/help` shows members the commands they can use (only the features the server has on, with links to the channels
they work in), visible only to them. `/help-post` (Manage Server) posts the same list in the current channel and
pins it, e.g. in `#rules` or `#bot-commands`. Moderator and admin commands are left out on purpose. If the commands
change, delete the old post and run `/help-post` again.

**Per-server settings** — every server that adds the bot can set things up for itself with `/settings` (Manage
Server). appsettings.json only holds the defaults for servers that haven't changed anything. Each server's choices are
saved in `data/server-settings.json`:
- `/settings show` — everything at a glance: which features are on, the music and LFG channels, the LFG ping role and
  whether auto-mod is on.
- `/settings feature` — turn the chatbot, chatbot actions, music, LFG, polls or reminders on or off. Turning music off
  stops what's playing. Things already underway still finish: LFG posts can be cancelled, polls ended, and reminders
  are delivered.
- `/settings channel` — pick the channel music or LFG works in, set `anywhere`, or leave both out for the default.
  Channels are saved by ID, so renaming one doesn't break it.
- `/settings lfg-pings` — pick the LFG ping role (it must be a plain role, since members give it to themselves), turn
  pings off, or leave both out for the default.
- Moderation is per server too: `/automod` (auto-mod is **off** in a new server until it runs `/automod enable`) and `/modlog`. So is the
  chatbot's personality (`/chat personality`). Auto-mod on or off only decides whether members' messages are
  scanned and removed: what the bot itself posts (chatbot replies, news write-ups, polls, reminders, song titles) always
  follows the server's banned words and profanity setting.
- Admins can also switch features from chat: "@bot disable polls", "@bot disable music for this server" (music needs
  "for this server", since "disable music" might just mean stop the song). Plain "turn
  off the music" still just stops the song.

**Logs** — shown in the console and also written to `logs/modbot-<date>.log`, one file per day, appended to. Files
older than `Logging:File:RetainDays` days (default 5) are deleted at startup and each new day. Set `Logging:File:Folder`
to empty to turn the files off. Each entry is one line, so a file can be searched by server name or ID:
- **Servers:** when a server adds or removes the bot (name, ID, owner, member count), and at startup every server the
  bot is in.
- **Use:** every command, button and pop-up that works (who, which channel, how long it took) or fails (and why), each
  chatbot answer and chat action, auto-mod removals, and settings changes with who made them. What people say to the
  chatbot isn't logged, only that it answered or did something. Log files hold usernames and user IDs for as many
  days as they're kept.
- **The server tag:** every line from a server ends with `=> Server name (ID)`.
- **Less noise:** web-request and Lavalink chatter only shows at warning level and up.

**AI chat** — mention the bot in a message (or reply to one of its messages) and it answers, using a model served
by [Ollama](https://ollama.com): on the same machine by default, or Ollama's cloud (see Setup step 6).
- It sends the last `Chat:HistoryMessages` (default 10) messages in the channel along for context, so it can follow
  a conversation. With a local Ollama they never leave the machine; with Ollama's cloud they're sent there. Set it
  to 0 to send only the question.
- Questions that break the server's word filter are ignored (auto-mod removes them), and replies are checked
  against the same filter before posting.
- Replies never ping anyone but the person who asked, and are cut to Discord's 2000-character limit.
- Each person can ask once every `Chat:CooldownSeconds` (default 10); too soon and the bot reacts with ⏳. At most
  `Chat:MaxConcurrentRequests` (default 1) questions go to Ollama at once; the rest wait their turn.
- **Personality:** `Chat:Personality` in `appsettings.json` is the default (tone, character, interests). Each server
  can replace it without a restart:
  - `/chat personality set` (Manage Server) opens a pop-up filled in with the current personality to edit, up to
    1000 characters. Leaving it empty goes back to the default.
  - `/chat personality show` shows the current one, and `/chat personality reset` goes back to the default.
  - Server personalities are saved in `data/chat-settings.json`.
  - The bot's ground rules always apply on top of any personality: short replies, reply only as itself, no pings,
    point to `/help` for commands.
- **Asking it to do things:** "@bot play never gonna give you up" or "@bot make an lfg for ranked, need 3 more, mic
  required" plays the song or posts the LFG, as if the person had used `/play` or `/lfg` themselves:
  - **Same rules as the commands:** they must be in a voice channel to play music, they get one open LFG at a time,
    and the ping cooldown applies.
  - **Right channels, wherever it's asked:** music announcements go to the music channel and LFG cards to the LFG
    channel, and the bot replies with where to find them. Posting an LFG for someone also needs them to be able to
    post in the LFG channel.
  - **How it decides:** each mention first gets a quick, fixed-answer check by the model: play music, make an LFG, or
    neither. That check only reads the asker's own message, so nothing else in the channel can trigger an action.
  - **Details:** the model turns requests like "put on some lofi beats" into a search, and YouTube links are used as
    written. LFG details (squad size, mode, rank, mic, PC/console only) are read with fixed keyword rules, because the
    small model invented details. Notes aren't read from chat; use `/lfg` for those.
  - **Reminders:** "@bot remind me in 20 minutes to check the oven" sets a reminder like `/remind`, with the same
    limits, delivered as a reply to that message. It's recognised without the model: a reminder phrase ("remind me",
    "set a reminder", "ping me") plus a time the reminder parser understands. Without a time ("remind me to eat") it's
    just chat, and the bot explains how to ask. Adding reminders to the model's check made it worse at the others.
  - **Polls:** "@bot make a poll: what should we play? ranked, casual or zombies" posts the poll as a reply, like
    `/poll`, with the same rules. "for 2 days" sets the length and "multiple choice" allows several answers. Like
    reminders, it's recognised without the model, by a poll phrase ("make a poll", "start a vote", "poll everyone",
    or a message starting "poll:"). Questions such as "how do I make a poll?" are just chat. When the question and
    answers are laid out plainly (`question? a, b or c`, or `question options: a, b`), they're read with fixed rules.
    Otherwise the model pulls them out, and only answers the person actually wrote are kept, because small models
    add their own. With no answers it's a Yes/No poll. "@bot end my poll" ends their newest open poll in the
    channel, or the one they replied to.
  - **Music:** "@bot add mr brightside" (or "queue up…", "put … in the queue") just queues a song, like `/add`;
    "play …" also starts the music, like `/play`. "Skip this", "pause", "what's playing?", "show the queue", "loop
    this song", "remove song 3", "remove my songs", "start the music", "where's the music menu?" and the rest of
    the music commands work by asking too, through the same code as the slash commands (so a skip is a vote).
    "Music session" wording works as well: starting one starts the music, leaving one takes out your songs.
    Plainly worded controls are read with fixed rules, without the model: a clause of the message has to be the
    command itself, so "this song sucks, skip it" skips but "I hate it when people skip songs" doesn't. Looser
    wording goes to the model's check (as a fourth answer, "music control"), then a second call picks which control.
    That pick was sometimes wrong in testing, so controls that can't be undone (stop, clearing the queue, removing
    your songs) are never done on the model's say-so: the bot asks the person to say it plainly instead.
  - **Turning it off:** `Chat:Actions: false`. Each mention then skips the extra check and just chats.
- Turn the feature off with `Chat:Enabled: false`.

## Project layout

```
Common/            Small helpers every feature shares: finding a configured channel by name, permission checks
                   before posting, pinning, per-server locks, background tasks and the timer loop
Configuration/     Strongly-typed options bound from appsettings/env/secrets
Models/            Plain data types (GuildSettings, Warning, ModerationResult)
Services/
  Storage/         IGuildSettingsStore / IWarningStore + a JSON-file implementation
  Moderation/      IModerationService - moderation actions, independent of Discord.Net modules
  ContentModeration/ IImageModerationService - image/GIF classification, implemented against Sightengine
  News/            INewsFeedReader (RSS/Atom parsing), built-in game feed presets, news embed formatting
  Lfg/             ILfgService - LFG post rules (join/leave/start/cancel/expiry), in memory, no Discord types
  Reminders/       ReminderService (set/list/cancel rules) and ReminderTimeParser ("in 2h", "tomorrow 9am")
  Polls/           PollRules (Discord's poll limits) and PollRequestReader (poll requests in chat)
  Settings/        ServerSettingsService (each server's /settings, with the defaults) and SettingsRequestReader
  Chat/            IChatService - gets a chatbot reply from a model, implemented against Ollama's chat API
EventHandlers/     Gateway event handlers (AutoModerationHandler and ChatHandler, both on MessageReceived)
Modules/           Slash-command modules, grouped by feature folder (auto-discovered at startup)
  Music/           Music commands, the standing queue (turns, vote skip, the pinned menu) and the Lavalink player that
                   announces songs (Lavalink4NET does the rest)
  Lfg/             /lfg command, its button handlers, and the LFG card's embed/buttons
lavalink/          Config for the Lavalink music server (the server itself is downloaded, not committed)
tests/            ModBot.Tests (xUnit): text readers, reminder rules, news write-up checks
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
5. Check `Common/` before writing Discord plumbing: a feature tied to a channel name, a pinned panel, a permission
   check before posting or a timed background job already has a helper there.

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

### 2. Create your settings file

Settings live in `appsettings.json`, which is **not** committed, so your own values (chatbot personality, channel
names, model) stay on your machine. The committed template is `appsettings.example.json`:

```powershell
Copy-Item appsettings.example.json appsettings.json
```

Edit `appsettings.json` from then on. When a new setting is added to the template, copy it across. Without the
file the bot runs on built-in defaults and logs a warning at startup.

### 3. Configure the token

Even though `appsettings.json` isn't committed, keep the token out of it (it's easy to share by accident). Use one
of:

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

### 4. (Optional) Configure image/GIF moderation

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

### 5. (Optional) Set up music

Music needs Java and the Lavalink server. Follow [lavalink/README.md](lavalink/README.md) once, then start Lavalink
(`java -jar Lavalink.jar` in the `lavalink/` folder) whenever you run the bot.

### 6. (Optional) Set up AI chat

By default the chatbot uses Ollama running on the same machine as the bot. No account or key is needed:

1. Install Ollama from [ollama.com/download](https://ollama.com/download). On Windows it runs in the background
   (tray icon) and starts with Windows.
2. Download the model: `ollama pull llama3.2` (about 2 GB; a 3B model that runs fine on a CPU, faster with a GPU).
   To use a different model, pull it and set `Chat:Model` in `appsettings.json` to its name (`ollama list` shows
   what you have).

If Ollama isn't running, the bot replies that it couldn't come up with an answer and logs why; everything else keeps
working. The first question after Ollama has been idle for a while is slower, since it loads the model into memory
first (`Chat:TimeoutSeconds`, default 120, allows for that).

**Using Ollama's cloud instead:** set `Chat:OllamaUrl` to `https://ollama.com`, set `Chat:Model` to a name from
[ollama.com/api/tags](https://ollama.com/api/tags), and create an API key at
[ollama.com/settings/keys](https://ollama.com/settings/keys). Set the key the same way as the bot token, with
`dotnet user-secrets set "Chat:ApiKey" "<api-key>"` or the `OLLAMA_API_KEY` environment variable. Without a key the
bot logs a warning at startup.

### 7. Run it

```bash
dotnet run
```

### 8. Servers' saved data

Each server that adds the bot starts on the defaults: auto-mod **off** (`/automod enable` turns it on), every
feature on, and music and LFG working in any channel until an admin picks one with `/settings channel`. What servers
change is saved in `data/`, one file per kind of data with each server's entry under its server ID:
`server-settings.json`, `guild-settings.json`, `chat-settings.json`, `warnings.json`, `reminders.json`,
`news-subscriptions.json` and `music-queue.json`. `data/` is git-ignored. Back it up, since losing it resets every
server.

> **To do: removing a server's data when it leaves.** When a server removes the bot, its entries are kept (the log
> says "Removed from server Name (ID)"), so its settings are still there if it adds the bot back. Nothing deletes them
> yet. Until that's automated, clean up by hand: stop the bot, then remove that server's entries from each file in
> `data/`. The entries are keyed by its server ID (from that log line). Reminders, warnings and news subscriptions
> carry a `GuildId` field instead of being keyed by it. Then start the bot again. A later change should do this
> automatically, for example a while after the server leaves, in case it was removed by mistake.

**Upgrading from before per-server settings:** `Music:ChannelName` and `Lfg:ChannelName` are gone. If your own
server relied on `#music` or `#lfg-mw4`, run `/settings channel` there once for each. The old keys in your
`appsettings.json` are just ignored.

### Tests

The tests in `tests/ModBot.Tests` cover the logic that's easy to get subtly wrong: reading times, reminder requests
and LFG details from text, the reminder rules, and the news write-up checks. Run them with:

```bash
dotnet test tests/ModBot.Tests
```

While the bot is running its build output is locked; add `--artifacts-path ../modbot-test-build` to build the tests
somewhere else.

## Permissions reference

| Command | Required user permission | Required bot permission |
|---|---|---|
| `/kick` | Kick Members | Kick Members |
| `/ban`, `/unban` | Ban Members | Ban Members |
| `/timeout`, `/untimeout` | Moderate Members | Moderate Members |
| `/warn`, `/warnings`, `/clearwarnings` | Moderate Members | — |
| `/purge` | Manage Messages | Manage Messages |
| `/modlog`, `/automod`, `/chat personality` | Manage Server | — |
| `/news` | Manage Server | View Channel, Send Messages, Embed Links (in the news channel) |
| `/add`, `/play`, `/start` | — (`/play` starts the music, and `/start` plays the queue, in your voice channel; adding works from anywhere) | View Channel, Connect, Speak (in the voice channel) |
| Other music commands | — (controlling playback needs you in the bot's voice channel unless you have Move Members or Manage Messages; `/queue`, `/nowplaying`, `/remove` and `/remove-mine` work from anywhere; all music commands must be used in the music channel, once the server picks one with `/settings channel`) | — |
| `/music-menu` | Manage Server, plus Send Messages and Pin Messages in that channel | Send Messages, Embed Links; Pin Messages to pin the menu; Manage Messages to delete the pin notices (optional) |
| `/clearqueue`, removing others' songs | Move Members or Manage Messages | — |
| `/lfg` | — (cancelling someone else's post: Manage Messages or Move Members) | View Channel, Send Messages, Embed Links (in that channel) |
| `/lfg-setup` | Manage Server, plus Send Messages and Pin Messages in that channel | Manage Roles (and a role above `@LFG`), Send Messages, Embed Links; Pin Messages to pin the panel |
| `/help` | — | — |
| `/help-post` | Manage Server, plus Send Messages and Pin Messages in that channel | Send Messages, Embed Links; Pin Messages to pin the list |
| AI chat (mention the bot) | — | Send Messages; Read Message History for context; Add Reactions for the ⏳ cooldown hint |
| `/remind`, `/reminders list`, `/reminders cancel` | — (you can only see and cancel your own) | Send Messages in the channel it's set in |
| `/settings` | Manage Server | Embed Links in a channel picked with `/settings channel` |
| `/poll`, **End poll** | Send Messages and Create Polls in the channel (ending someone else's poll: Move Members or Manage Messages) | Send Messages, Create Polls; Read Message History for "end my poll" in chat |

`/kick`, `/ban`, `/timeout` and `/untimeout` also follow Discord's role order: you can only use them on members whose
highest role is below yours (the server owner can act on anyone), and the bot's own role has to be above theirs too.
