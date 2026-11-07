using System.Text.RegularExpressions;
using ModBot.Services.Chat;

namespace ModBot.Services.Voice;

/// <summary>
/// A back-and-forth with the bot in voice: after it answers someone who said its name, they (or, if the server chose
/// it, anyone in the channel) can say the next thing without the name, until nobody's said anything to it for a while
/// (or someone says "thanks"). Whoever said the name last is the one it's talking with. Holds
/// what was said and answered meanwhile, in memory only, so a follow-up ("and what about tomorrow?") makes sense;
/// it's forgotten when the conversation ends. Safe to use from several threads.
/// </summary>
/// <param name="quiet">How long after the bot last spoke (with nobody asking anything) the conversation ends.</param>
public sealed partial class VoiceConversation(TimeSpan quiet)
{
    /// <summary>Earlier questions and answers kept for follow-ups, at most (each question and answer counts one).</summary>
    public const int MaxTurns = 8;

    private readonly Lock _lock = new();
    private readonly List<ChatTurn> _turns = [];
    private volatile bool _open;
    private int _busy;
    private int _epoch;
    private ulong _with;
    private bool _anyone;
    private DateTimeOffset _lastActivity;
    private string _lastSaid = "";

    public bool IsOpen
    {
        get
        {
            lock (_lock)
            {
                return _open;
            }
        }
    }

    /// <summary>Open, or a request is being answered that may open one: a goodbye counts now.</summary>
    public bool IsActive
    {
        get
        {
            lock (_lock)
            {
                return _open || _busy > 0;
            }
        }
    }

    /// <summary>A request from <paramref name="asker"/> is being answered: the conversation stays open at least until
    /// <see cref="Answered"/>, and it's with them now. Returns a mark to pass to <see cref="Answered"/> and
    /// <see cref="Still"/>.</summary>
    /// <param name="anyone">Whether anyone in the channel can follow up without the name, not just the asker (the
    /// server's choice, /settings voice-follow-ups).</param>
    public int Asking(DateTimeOffset now, ulong asker, bool anyone = false)
    {
        lock (_lock)
        {
            _busy++;
            _lastActivity = now;
            _with = asker;
            _anyone = anyone;
            return _epoch;
        }
    }

    /// <summary>Whether <paramref name="userId"/> can talk to the bot without its name now: a conversation is open (or
    /// its first answer is on its way), and it's with them, or with anyone.</summary>
    public bool Hears(ulong userId)
    {
        lock (_lock)
        {
            return (_open || _busy > 0) && (_anyone || _with == userId);
        }
    }

    /// <summary>Whether the conversation a request was asked in (<paramref name="mark"/>, from <see cref="Asking"/>)
    /// hasn't been ended since: after a goodbye, answers still on their way are dropped.</summary>
    public bool Still(int mark)
    {
        lock (_lock)
        {
            return mark == _epoch;
        }
    }

    /// <summary>The request from <see cref="Asking"/> is done. With an answer, the conversation opens (or carries on),
    /// and remembers it: <paramref name="said"/> is what was read aloud, for telling the bot's own voice from a
    /// reply.</summary>
    public void Answered(int mark, DateTimeOffset now, string? asker, string? request, string? answer, string? said)
    {
        lock (_lock)
        {
            _busy = Math.Max(0, _busy - 1);
            _lastActivity = now;
            if (asker is null || request is null || answer is null || mark != _epoch)
            {
                return; // nothing was answered (or it was said goodbye to since): no conversation starts
            }

            _open = true;
            _lastSaid = said ?? "";
            _turns.Add(new ChatTurn(ChatRole.User, $"{asker} said: {request}"));
            _turns.Add(new ChatTurn(ChatRole.Assistant, answer));
            if (_turns.Count > MaxTurns)
            {
                _turns.RemoveRange(0, _turns.Count - MaxTurns);
            }
        }
    }

    /// <summary><paramref name="userId"/> is talking: if it's someone the bot <see cref="Hears"/>, it keeps an open
    /// conversation from ending mid-sentence (others chatting meanwhile don't keep it open).</summary>
    public void Heard(DateTimeOffset now, ulong userId)
    {
        if (!_open)
        {
            return; // the usual case, checked without the lock: this runs for every bit of audio
        }

        lock (_lock)
        {
            if (_open && (_anyone || _with == userId) && now > _lastActivity)
            {
                _lastActivity = now;
            }
        }
    }

    /// <summary>What was said and answered so far, oldest first; empty when no conversation is open.</summary>
    public IReadOnlyList<ChatTurn> Earlier()
    {
        lock (_lock)
        {
            return _open ? _turns.ToList() : [];
        }
    }

    /// <summary>Ends the conversation if it's gone quiet: nothing being answered, and <c>quiet</c> since anyone last
    /// spoke, the bot included (<paramref name="talkingUntil"/>). True once, when it ends.</summary>
    public bool TryEndQuiet(DateTimeOffset now, DateTimeOffset talkingUntil)
    {
        lock (_lock)
        {
            var last = talkingUntil > _lastActivity ? talkingUntil : _lastActivity;
            return _open && _busy == 0 && now - last >= quiet && EndLocked();
        }
    }

    /// <summary>Ends it now ("thanks"), along with any answers still on their way; true if it was active (see
    /// <see cref="IsActive"/>).</summary>
    public bool End()
    {
        lock (_lock)
        {
            var active = _open || _busy > 0;
            EndLocked();
            return active;
        }
    }

    private bool EndLocked()
    {
        var was = _open;
        _epoch++;
        _open = false;
        _turns.Clear();
        _lastSaid = "";
        return was;
    }

    /// <summary>
    /// Whether <paramref name="heard"/> is the bot's own last answer coming back through someone's mic rather than a
    /// reply: most of its words are in what the bot just said. Only checked in a conversation, where anything heard
    /// counts as a request.
    /// </summary>
    public bool IsEcho(string heard)
    {
        string said;
        lock (_lock)
        {
            said = _lastSaid;
        }

        var words = Words(heard);
        if (words.Count < 3 || said.Length == 0)
        {
            return false;
        }

        var saidWords = Words(said).ToHashSet();
        return words.Count(saidWords.Contains) >= words.Count * 0.7;
    }

    /// <summary>"Thanks", "that's all", "goodbye", "stop": the whole of what was said is a goodbye, so the conversation
    /// ends (and the bot stops talking) instead of answering it. The bot's name in it ("thanks, ModBot", "ModBot,
    /// bye") doesn't count.</summary>
    public static bool IsGoodbye(string request, IEnumerable<string> names) => IsGoodbye(request, names, out _);

    /// <inheritdoc cref="IsGoodbye(string, IEnumerable{string})"/>
    /// <param name="named">Whether the bot's name was in it: over the bot, only a goodbye said to it by name counts,
    /// since speech-to-text turns unclear audio into "Thank you." all too often.</param>
    public static bool IsGoodbye(string request, IEnumerable<string> names, out bool named)
    {
        named = false;
        var words = request.Replace('’', '\'').Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList();
        var squashed = names.Select(WakeWord.Squash).Where(n => n.Length > 0).ToHashSet();
        // The name, wherever it is, split over up to three words ("Mod Bot").
        for (var at = 0; at < words.Count; at++)
        {
            for (var take = Math.Min(3, words.Count - at); take > 0; take--)
            {
                if (squashed.Contains(string.Concat(words.Skip(at).Take(take).Select(WakeWord.Squash))))
                {
                    words.RemoveRange(at, take);
                    named = true;
                    at--;
                    break;
                }
            }
        }

        return GoodbyeRegex().IsMatch(string.Join(' ', words));
    }

    /// <summary>Whether <paramref name="heard"/> is in what the bot's saying (<paramref name="saying"/>): heard over the
    /// bot, "bye!" is likely its own "bye!" coming back through a mic, not someone saying it.</summary>
    public static bool Repeats(string saying, string heard)
    {
        var words = string.Concat(Words(heard));
        return words.Length > 0 && string.Concat(Words(saying)).Contains(words, StringComparison.Ordinal);
    }

    private static List<string> Words(string text)
        => WordRegex().Matches(text.ToLowerInvariant()).Select(m => m.Value).ToList();

    [GeneratedRegex(
        @"^\W*(?:(?:ok(?:ay)?|alright|all\s+right|cool|great|perfect|awesome|nice|got\s+it|sounds\s+good|no|nope)[\s,.!]+)*" +
        @"(?:thanks?(?:\s+you)?|thank\s+you|thx|cheers|that'?s\s+(?:all|it|everything)|never\s*mind|forget\s+it|" +
        @"(?:we'?re|i'?m)\s+(?:done|good|all\s+set)|nothing(?:\s+else)?|good\s*bye|bye(?:[\s-]+bye)?|see\s+ya|" +
        @"stop(?:\s+talking)?|shut\s+up|be\s+quiet|that'?s\s+enough)" +
        @"(?:[\s,.!]+(?:so\s+much|a\s+lot|man|dude|bro|buddy|for\s+(?:that|the\s+help)|that'?s\s+(?:all|it)|bye|goodbye))*\W*$",
        RegexOptions.IgnoreCase)]
    private static partial Regex GoodbyeRegex();

    [GeneratedRegex(@"[a-z0-9']+")]
    private static partial Regex WordRegex();
}
