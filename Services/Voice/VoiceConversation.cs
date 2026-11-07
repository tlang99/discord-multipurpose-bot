using System.Text.RegularExpressions;
using ModBot.Services.Chat;

namespace ModBot.Services.Voice;

/// <summary>
/// A back-and-forth with the bot in voice: after it answers someone who said its name, anyone in the channel can say
/// the next thing without the name, until nobody's said anything to it for a while (or someone says "thanks"). Holds
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

    /// <summary>A request is being answered: the conversation stays open at least until <see cref="Answered"/>.</summary>
    public void Asking(DateTimeOffset now)
    {
        lock (_lock)
        {
            _busy++;
            _lastActivity = now;
        }
    }

    /// <summary>The request from <see cref="Asking"/> is done. With an answer, the conversation opens (or carries on),
    /// and remembers it: <paramref name="said"/> is what was read aloud, for telling the bot's own voice from a
    /// reply.</summary>
    public void Answered(DateTimeOffset now, string? asker, string? request, string? answer, string? said)
    {
        lock (_lock)
        {
            _busy = Math.Max(0, _busy - 1);
            _lastActivity = now;
            if (asker is null || request is null || answer is null)
            {
                return; // nothing was answered: an open conversation carries on, but none starts
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

    /// <summary>Someone's talking (it keeps an open conversation from ending mid-sentence).</summary>
    public void Heard(DateTimeOffset now)
    {
        if (!_open)
        {
            return; // the usual case, checked without the lock: this runs for every bit of audio
        }

        lock (_lock)
        {
            if (_open && now > _lastActivity)
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

    /// <summary>Ends it now ("thanks"); true if one was open.</summary>
    public bool End()
    {
        lock (_lock)
        {
            return EndLocked();
        }
    }

    private bool EndLocked()
    {
        var was = _open;
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

    /// <summary>"Thanks", "that's all", "never mind": the whole of what was said is a goodbye, so the conversation
    /// ends instead of the bot answering it. The bot's name at the end ("thanks, ModBot") doesn't count.</summary>
    public static bool IsGoodbye(string request, IEnumerable<string> names)
    {
        var words = request.Replace('’', '\'').Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList();
        var squashed = names.Select(WakeWord.Squash).Where(n => n.Length > 0).ToHashSet();
        // The name, split over up to three words ("Mod Bot").
        for (var take = Math.Min(3, words.Count); take > 0; take--)
        {
            if (squashed.Contains(string.Concat(words.TakeLast(take).Select(WakeWord.Squash))))
            {
                words.RemoveRange(words.Count - take, take);
                break;
            }
        }

        return GoodbyeRegex().IsMatch(string.Join(' ', words));
    }

    private static List<string> Words(string text)
        => WordRegex().Matches(text.ToLowerInvariant()).Select(m => m.Value).ToList();

    [GeneratedRegex(
        @"^\W*(?:(?:ok(?:ay)?|alright|all\s+right|cool|great|perfect|awesome|nice|got\s+it|sounds\s+good|no|nope)[\s,.!]+)*" +
        @"(?:thanks?(?:\s+you)?|thank\s+you|thx|cheers|that'?s\s+(?:all|it|everything)|never\s*mind|forget\s+it|" +
        @"(?:we'?re|i'?m)\s+(?:done|good|all\s+set)|nothing(?:\s+else)?|bye|goodbye|see\s+ya|later)" +
        @"(?:[\s,]+(?:so\s+much|a\s+lot|man|dude|bro|buddy|for\s+(?:that|the\s+help)|that'?s\s+(?:all|it)))*\W*$",
        RegexOptions.IgnoreCase)]
    private static partial Regex GoodbyeRegex();

    [GeneratedRegex(@"[a-z0-9']+")]
    private static partial Regex WordRegex();
}
