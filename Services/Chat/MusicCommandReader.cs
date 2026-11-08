using System.Text.RegularExpressions;
using ModBot.Common;

namespace ModBot.Services.Chat;

/// <summary>A music control the chatbot can be asked for.</summary>
public enum MusicCommand
{
    Skip,
    Pause,
    Resume,
    Stop,
    NowPlaying,
    ShowQueue,
    LoopSong,
    LoopQueue,
    LoopOff,
    Remove,

    /// <summary>Take all the person's own songs out of the queue.</summary>
    RemoveMine,
    ClearQueue,

    /// <summary>Start playing the queue in the person's voice channel.</summary>
    Start,

    /// <summary>Point them to the music menu.</summary>
    ShowMenu,

    /// <summary>Make one of the person's own songs (at Position) the next of theirs to play.</summary>
    PlayNext,

    /// <summary>Shuffle the person's own waiting songs.</summary>
    ShuffleMine,

    /// <summary>Turn the music down a step, or up a step, or to Volume.</summary>
    VolumeDown,
    VolumeUp,
    SetVolume,

    /// <summary>Jump to At in the song that's playing; Restart jumps to the start.</summary>
    Seek,
    Restart,

    /// <summary>Go back to the song before this one.</summary>
    Previous,

    /// <summary>Add the song played most recently to the queue again.</summary>
    PlayAgain,

    /// <summary>Show the songs played lately ("what was that song?").</summary>
    ShowHistory,
}

/// <param name="Position">For remove and play next: the song's place in the queue.</param>
/// <param name="Title">For remove: words from the song's title, instead of its place.</param>
/// <param name="Volume">For set volume: the percentage.</param>
/// <param name="At">For seek: where in the song.</param>
public record MusicCommandRequest(MusicCommand Command, int? Position = null, string? Title = null, int? Volume = null,
    TimeSpan? At = null);

/// <summary>
/// Spots plainly worded music controls in a chat message ("skip this", "pause", "show the queue", "start the music")
/// without asking the model, which is slower and, when tested, mixed up some of them (it read "this song is awful,
/// get rid of it" as removing song 1, and "ok keep going" as a skip).
/// </summary>
/// <remarks>
/// The message is split into clauses ("this song sucks, skip it" has two), and a clause only counts if, after
/// fillers like "can you" and "please" are trimmed, the whole of it is the command. So "skip it" is a skip, but "I hate
/// it when people skip songs" is just chat. "Music session" wording still works, mapped onto the standing queue:
/// starting one starts the music, leaving one takes out your songs, ending one stops the music.
/// </remarks>
public static partial class MusicCommandReader
{
    private static readonly (Regex Pattern, Func<Match, MusicCommandRequest> Read)[] Rules =
    [
        // Loop rules come before skip/stop/resume, since "stop looping" and "repeat this" overlap with them.
        (Rx(@"(stop|turn off|disable|no more|cancel) (the )?(looping|repeating|loop|repeat)|(loop|repeat|looping) off|unloop|dont (loop|repeat)( it| anymore)*"),
            _ => new(MusicCommand.LoopOff)),
        (Rx(@"(loop|repeat) (the )?(whole |entire )?(queue|playlist|everything|all( the songs)?)"),
            _ => new(MusicCommand.LoopQueue)),
        (Rx(@"(loop|repeat)( this| the| current| that)?( song| track| one)?|put( this| the| that)?( song| track| one)? on (loop|repeat)"),
            _ => new(MusicCommand.LoopSong)),

        // Before skip: "play song 3 next" reorders your songs, it isn't "play the next one".
        (Rx(@"(play|put|move|bump)( song| track| number| no)? #?(?<n>\d+) (next|first|up next|to the front|to the top)( of my songs)?"),
            m => new(MusicCommand.PlayNext, Number(m))),
        (Rx(@"(shuffle|mix up|randomi[sz]e)( all)? my (songs|picks|tracks|stuff)( in the queue)?"),
            _ => new(MusicCommand.ShuffleMine)),

        // Volume. "Turn the music off" is a stop, below; these only go down, up, or to a number.
        (Rx(@"(turn|crank|bring)( it| this| that| the music| the volume| the song)? down( a (bit|little|notch))?|(turn|crank|bring) down( the)? (music|volume|song)|(make it |a (bit|little) )?quieter|volume down|lower( the)? (volume|music)|(its|thats) too loud"),
            _ => new(MusicCommand.VolumeDown)),
        (Rx(@"(turn|crank|bring)( it| this| that| the music| the volume| the song)? up( a (bit|little|notch))?|(turn|crank|bring) up( the)? (music|volume|song)|(make it |a (bit|little) )?louder|volume up|raise( the)? (volume|music)|(its|thats|i cant hear it,?) too quiet"),
            _ => new(MusicCommand.VolumeUp)),
        (Rx(@"(set |put |turn )?(the )?(music |song )?volume (to |at )?(?<n>\d+)( ?%| percent)?"),
            m => new(MusicCommand.SetVolume, Volume: Number(m))),

        // Before resume ("play again" there means unpause): these are the song played before, or once more.
        (Rx(@"(restart|replay)( this| the| current| that)?( song| track| one)?|start (this|the|that)( song| track| one)? (over|again|from the (start|beginning|top))|play (this|it|that)( song| one)? from the (start|beginning|top)|(go )?back to the (start|beginning)( of (this|the) song)?"),
            _ => new(MusicCommand.Restart)),
        // "Replay the last song" and "play the last song (again)" go back to it, so it plays now; "queue the last song
        // again" (below) adds it to the queue instead.
        (Rx(@"(go|skip) back( a song| one| to the (last|previous) (song|one|track))?|(play )?(the )?previous( song| track| one)?( again)?|back to the (last|previous) (song|one|track)|(re ?play|play)( the)? (last|previous) (song|one|track)( again)?|(re ?play|play) the (song|one|track) (before|before this( one)?)"),
            _ => new(MusicCommand.Previous)),
        (Rx(@"(play|queue|add) (that|this|it)( song| track| one)? again|(queue|add)( the)? (last|previous) (song|track|one) again|(play it|play that|play the song) one more time"),
            _ => new(MusicCommand.PlayAgain)),
        (Rx(@"(what|which) (was|were) (that|the last|the previous|the) (song|track|one|songs)( called| before| just now)?|(show|list|see|view)( me| us)?( the)? (history|song history|recent songs|recently played( songs)?|last (few )?songs)|whats? (been|was) (played|playing)( (lately|before|earlier))?|what did (we|you) (just )?(play|listen to)|recently played"),
            _ => new(MusicCommand.ShowHistory)),

        (Rx(@"(vote )?skip( to the next one| to the next song)?( (this|it|that|the|current))?( (song|track|one|tune))?|vote to skip( (this|it|that))?( (song|track|one))?|next( (song|track|one))?|play the next (song|track|one)"),
            _ => new(MusicCommand.Skip)),
        (Rx(@"pause( (the|this|it|that))?( (music|song|track))?( for a (sec|second|bit|minute|moment))?"),
            _ => new(MusicCommand.Pause)),
        (Rx(@"(resume|unpause|continue|keep going|keep playing|play again|start it again)( (the|it))?( (music|song|track|playing))?"),
            _ => new(MusicCommand.Resume)),

        // Your own songs, before the general remove/clear rules ("clear my songs" isn't clearing the queue).
        (Rx(@"(remove|delete|drop|clear|take out|pull)( all)? my (songs|picks|tracks|stuff)( out)?( from the queue| out of the queue)?|take( all)? my (songs|picks|tracks) out( of the queue)?|(leave|exit|quit)( the| this| our)?( music| song)? session|(take|get) me out of( the| this)?( music)? (session|queue)|remove me from( the| this)?( music)? (session|queue)"),
            _ => new(MusicCommand.RemoveMine)),

        (Rx(@"(show|post|bring up|pull up|open|give me|where is|wheres)( me| us)?( the| a)?( music)?( session| queue)? (menu|panel)|(show|post)( me| us)?( the)?( music)? session"),
            _ => new(MusicCommand.ShowMenu)),

        // "Stop the session" stops the music, not just a song: before plain stop only for clarity, both stop.
        (Rx(@"(end|close|stop|finish|wrap up|shut down|cancel)( the| our| this)?( music| song)? session"),
            _ => new(MusicCommand.Stop)),
        (Rx(@"(start|open|begin|make|create|kick off|set up|run|host|do|have)( up)?( a| the| us a| our| another| a new)?( new)? (music |song |listening )?session|(start|begin|kick off|play)( the| our)? (music|songs|playlist|queue)( now)?|start playing( (the|our) (songs|queue|playlist))?( now)?|(lets go|go ahead), start( it| the music)?"),
            _ => new(MusicCommand.Start)),

        (Rx(@"stop( (the|this|that))?( (music|song|songs|track|playing|player|tunes))?( and leave)?|stop playing( (this|that|it|the music|music|songs|the song|this song))?|(leave|disconnect from|disconnect|get out of)( the)?( voice( channel| chat)?| vc| call)|turn( the| this| that)? (music|song|it|this|that) off|turn off( the| this)? (music|song)|shut( the| this| that)? (music|song|it) off|shut off( the)? music|kill( the| this)? (music|song|tunes)|end( the| this)? music|no more music|enough music"),
            _ => new(MusicCommand.Stop)),

        (Rx(@"whats? (this|the) (song|track)( called)?|what (song|track) is (this|playing|on)( right now| now)?|whats? (playing|on)( right now| now)?|now playing|what are (we|you) (listening to|playing)( right now| now)?"),
            _ => new(MusicCommand.NowPlaying)),
        (Rx(@"(show|list|see|view|check)( me| us)?( the| whats in the)? (queue|playlist|song list|songs)|whats? (in|on) the (queue|playlist)|whats? (next|up next|coming up)|queue|whats? left in the queue"),
            _ => new(MusicCommand.ShowQueue)),

        (Rx(@"(remove|delete|take out|drop)( song| track| number| no| the song at( position)?)? #?(?<n>\d+)( out)?( from the queue| out of the queue)?|take( song| number)? #?(?<n>\d+) out( of the queue)?"),
            m => new(MusicCommand.Remove, Number(m))),
        // One song by name: "remove mr brightside from the queue". Needs "from the queue" (or "out of"), so "take out
        // the trash" is just chat; "my songs" / "me" are caught above.
        (Rx(@"(remove|delete|drop|take)( out)?( my| the)?( song)? (?<title>.+?)( out)? (from|out of|off) (the|my) (queue|playlist)"),
            m => new(MusicCommand.Remove, Title: m.Groups["title"].Value)),
        (Rx(@"(clear|empty|wipe|clean out|clear out)( out)?( the)? (queue|playlist)"),
            _ => new(MusicCommand.ClearQueue)),
    ];

    /// <summary>What can end a message, or come before a trailing "thanks".</summary>
    private static readonly char[] EndPunctuation = ['.', ',', '!', '?', ' '];

    /// <summary>The music control <paramref name="message"/> plainly asks for, or null.</summary>
    public static MusicCommandRequest? Read(string message)
    {
        // Lowercase, no apostrophes ("what's" -> "whats"), and clauses split at punctuation and "and"/"then".
        var text = message.ToLowerInvariant().Replace("'", "").Replace("’", "");

        // A time has a colon in it ("skip to 1:30"), which splitting into clauses would cut in two: checked whole,
        // without the punctuation around fillers ("skip to 1:30, thanks" is "skip to 1:30" once "thanks" is off).
        var whole = Trim(text.TrimEnd(EndPunctuation)).TrimEnd(EndPunctuation);
        if (SeekRegex().Match(whole) is { Success: true } seek &&
            TextTools.ParseTime(seek.Groups["t"].Value) is { } at)
        {
            return new MusicCommandRequest(MusicCommand.Seek, At: at);
        }

        foreach (var rawClause in ClauseSplitRegex().Split(text))
        {
            var clause = Trim(rawClause);
            if (clause.Length == 0 || clause.Length > 80)
            {
                continue;
            }

            foreach (var (pattern, read) in Rules)
            {
                if (pattern.Match(clause) is { Success: true } match)
                {
                    var request = read(match);
                    // "Remove this song from the queue" names no song: that's for the model to work out.
                    if (request.Position is <= 0 or > 1000 || request.Volume is < 0 or > 1000 ||
                        request.Title is { } title && NoTitleRegex().IsMatch(title))
                    {
                        continue;
                    }

                    return request;
                }
            }
        }

        return null;
    }

    /// <summary>Removes the polite and filler words around a command: "hey can you please skip this song for me"
    /// becomes "skip this song".</summary>
    private static string Trim(string clause)
    {
        clause = SpacesRegex().Replace(clause, " ").Trim();
        string previous;
        do
        {
            previous = clause;
            clause = LeadingFillerRegex().Replace(clause, "").Trim();
            clause = TrailingFillerRegex().Replace(clause, "").Trim();
        }
        while (clause != previous);

        return clause;
    }

    /// <summary>The number a rule matched ("n"), or -1 if it's too big to be one ("play 99999999999 next"), which
    /// Read then passes over like any place that isn't in the queue (or volume no one could mean).</summary>
    private static int Number(Match match) => int.TryParse(match.Groups["n"].Value, out var n) ? n : -1;

    private static Regex Rx(string pattern)
        => new($"^(?:{pattern})$", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, TimeSpan.FromMilliseconds(100));

    [GeneratedRegex(@"[.,!?;:]+|\s+(?:and|then|and then)\s+")]
    private static partial Regex ClauseSplitRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex SpacesRegex();

    /// <summary>"this song", "it", "the current one", "everything": pointing at songs rather than naming one.</summary>
    [GeneratedRegex(@"^(?:(?:this|that|the|it|current|playing|last|next|one|ones|song|songs|track|tracks|tune|tunes|thing|things|stuff|plug|all|everything|them|those|these|every|of)\s*)+$")]
    private static partial Regex NoTitleRegex();

    [GeneratedRegex(@"^(?:hey|hi|yo|ok|okay|alright|aight|lol|lmao|um+|uh+|so|now|please|pls|plz|can you|could you|would you|will you|can u|could u|would u|you can|go ahead and|just|kindly|bot|mate|bro|dude|i want you to|id like you to|i want to|i wanna|we want to|we wanna|lets|let us)\b\s*")]
    private static partial Regex LeadingFillerRegex();

    [GeneratedRegex(@"\s*\b(?:please|pls|plz|for me|for us|now|thanks|thank you|thx|ty|already|real quick|quick|rn|right now|mate|bro|dude)$")]
    private static partial Regex TrailingFillerRegex();

    /// <summary>"Skip to 1:30", "jump ahead to 2:05", "seek to 90", "play it from 45": a time ("t") in the song that's
    /// playing. "Skip to", "jump to" and "go to" need minutes and seconds ("skip to 2" may mean song 2); plain seconds
    /// only go with "seek to" or "from".</summary>
    [GeneratedRegex(@"^(?:skip|jump|go|fast forward|rewind|scrub)(?: ahead| forward| back)? to (?:the )?(?<t>\d{1,5}(?::\d{1,2}){1,2})(?: in (?:this|the) (?:song|track))?$|^seek to (?:the )?(?<t>\d{1,5}(?::\d{1,2}){0,2})(?: in (?:this|the) (?:song|track))?$|^(?:start|play)(?: it| this| that| the song)? (?:from|at) (?<t>\d{1,5}(?::\d{1,2}){0,2})$", RegexOptions.ExplicitCapture)]
    private static partial Regex SeekRegex();
}
