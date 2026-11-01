using System.Text.RegularExpressions;

namespace ModBot.Services.News;

/// <summary>
/// Decides whether an article is worth posting based on keyword lists. Keywords match whole words,
/// case-insensitively, with an optional trailing "s" (so "map" also matches "maps").
/// </summary>
public static class NewsKeywordFilter
{
    /// <param name="includeKeywords">If non-empty, the headline must contain at least one.</param>
    /// <param name="excludeKeywords">If the headline contains any of these, the article is skipped.</param>
    /// <remarks>Both lists only look at the headline. Summaries mention words like "maps", "weapons" or "review" in
    /// passing, which let reviews and opinion pieces through when includes were also matched against the summary.
    /// Real update news names the update in its headline ("Season 1 Brings New Maps", "Nerfs Controversial Perk").
    /// </remarks>
    public static bool Matches(NewsArticle article, IReadOnlyCollection<string> includeKeywords,
        IReadOnlyCollection<string> excludeKeywords)
    {
        if (excludeKeywords.Any(k => ContainsWord(article.Title, k)))
        {
            return false;
        }

        return includeKeywords.Count == 0 || includeKeywords.Any(k => ContainsWord(article.Title, k));
    }

    private static bool ContainsWord(string text, string keyword)
    {
        // "Not next to a letter/digit" rather than \b: \b needs a word character on one side, so a keyword starting
        // or ending with a symbol (e.g. "c++", "#1") could never match.
        var pattern = @"(?<!\w)" + Regex.Escape(keyword.Trim()).Replace(@"\ ", @"\s+") + @"s?(?!\w)";
        return Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(100));
    }
}
