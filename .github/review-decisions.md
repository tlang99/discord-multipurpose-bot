# Decided on purpose

Choices the owner made deliberately. The review agents (`claude-review.yml`, `full-review.yml`,
`polish-review.yml`) read this file first and **don't raise anything listed here**, as a finding, a suggestion or
a feature idea, unless a change in front of them breaks it, or it causes a real bug the entry doesn't cover.
Add to this list when the owner turns down a suggestion so it stops coming back.

## Voice
- **No status posts beyond the one join line.** When it joins (by hand or auto-join) it posts one line, including
  the privacy note, every time. No "stopped listening" or "left" posts and no DMs, even when it leaves on its own
  (the time limit, a lost connection, being moved or kicked). The wake chime is the signal: if "hey `@ModBot`" gets
  no chime, it isn't listening. The one exception (approved 2026-11-07): in one-bot mode, when someone starts music
  and listening has to stop, it says why aloud once ("Starting the music. I can't listen while it's on…") before
  leaving. That's spoken, not posted, and it's because someone asked for music, not because it left on its own.
- **Answers are spoken only, not posted.** A spoken answer longer than `Voice:MaxSpokenCharacters` is cut, and the
  cut is logged. The full answer isn't posted in chat. (It falls back to chat only when it can't speak.)
- **The join line's privacy wording stays in full**, on every join, not shortened on later auto-joins.
- **The closing chime plays at the end of every conversation**, even after a single question. That's by design:
  don't suggest skipping it, or a setting to turn it off.
- **Two people saying "hey ModBot" within 5 seconds:** both are heard and answered (each by name). The first gets
  the chime, the second a softer one-note "heard you too" after it, never both at once.
- **In a conversation, everyone who's asked something can follow up without the name**, not just the last asker.
- **Auto-join is opt-in** (its own feature) and needs Set Voice Channel Status.
- **Anyone may start voice listening**, not just moderators.

## Music
- **The music channel stays quiet.** No new public posts there: no "now playing" posts in servers with a menu, no
  public "song added" confirmations, no "left because idle" or "skipped a song" posts. That news goes on the pinned
  menu instead (its "Latest" line and "left voice" time). Don't use `SuppressNotification` as a workaround either.
- **`/add` and `/play` do the same thing** (as do "add X" and "play X" in chat, and the menu's Add song): the song
  joins the queue, and the music starts if nothing's playing and the person is in voice.
- **Moderator rule for music:** Move Members **or** Manage Messages.
- **Skip votes don't name the voter.**
- **A music action said by voice that worked is answered with the done chime, not words** ("Added Mr Brightside,
  you're third up"), so it doesn't talk over the music. Failures are still said. Don't suggest speaking them.
- **Seek, restart and go back (Back, `/previous`) are for anyone in the bot's voice channel, with no vote**, like
  pause. Going back cuts off the current song, but puts it back at the front of its picker's songs, so it isn't the
  same as skipping it.
- **Volume is for anyone in the bot's voice channel** (or anyone while nothing's playing), saved per server.
- **Stopping takes a vote with 3 or more in the voice channel** (more than half); with fewer, for whoever started
  the music, or a moderator, it's at once. No undo button instead.
- **Music code stays in `Modules/Music/`**, not moved into `Services/` behind interfaces. The same goes for
  `PollPoster`. This is an exception to the README's "business logic in Services/ behind interfaces".

## Settings
- **New servers start with auto-mod off and no default channel names**, and existing servers aren't seeded with
  LFG games (COD): blank on purpose.

## Review workflows
- **Actions stay unpinned** (`@v1`, not commit SHAs).
- **A secret found in a review blocks the post** instead of being redacted.
- **Defusing `@` in posted reviews also changes emails and code like C# attributes.** It's a safety net, and that
  side effect is accepted.
- **Two runs of the same review area can cancel a waiting duplicate.** That's fine: the newest run wins and each
  run overwrites the same issue. Don't suggest `cancel-in-progress: true`, which would kill a review mid-run.
