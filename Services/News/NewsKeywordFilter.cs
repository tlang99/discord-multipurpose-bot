using System.Text.RegularExpressions;

namespace ModBot.Services.News;

/// <summary>
/// Decides whether an article is worth posting based on keyword lists. Keywords match whole words,
/// case-insensitively, with an optional trailing "s" (so "map" also matches "maps").
/// </summary>
public static class NewsKeywordFilter
{
    /// <param name="includeKeywords">If non-empty, the headline or summary must contain at least one.</param>
    /// <param name="excludeKeywords">If the headline contains any of these, the article is skipped. Only the
    /// headline is checked, since summaries often mention words like "review" in passing.</param>
    public static bool Matches(NewsArticle article, IReadOnlyCollection<string> includeKeywords,
        IReadOnlyCollection<string> excludeKeywords)
    {
        if (excludeKeywords.Any(k => ContainsWord(article.Title, k)))
        {
            return false;
        }

        if (includeKeywords.Count == 0)
        {
            return true;
        }

        var text = article.Title + "\n" + article.Summary;
        return includeKeywords.Any(k => ContainsWord(text, k));
    }

    private static bool ContainsWord(string text, string keyword)
    {
        var pattern = @"\b" + Regex.Escape(keyword.Trim()).Replace(@"\ ", @"\s+") + @"s?\b";
        return Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
