using System.Text;

namespace ModBot.Services.Moderation;

/// <summary>
/// Normalizes text for word-filter matching so simple evasions slip past less easily -
/// mixed case, inserted spaces, and punctuation between letters are all stripped, so
/// "FUCK", "f u c k", and "f.u.c.k" all normalize to the same thing as the plain word "fuck".
///
/// Trade-off: this also means a banned multi-word phrase can match text where the words
/// happen to be adjacent after separators are stripped (e.g. "bad. word" normalizes next
/// to "badword"). Fine for the single-word slurs this is mainly meant for; worth knowing if
/// you add longer banned phrases.
/// </summary>
public static class TextNormalizer
{
    public static string Normalize(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(char.ToLowerInvariant(c));
            }
        }

        return builder.ToString();
    }
}
