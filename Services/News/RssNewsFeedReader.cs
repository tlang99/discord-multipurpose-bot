using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using ModBot.Common;

namespace ModBot.Services.News;

/// <summary>
/// Reads RSS 2.0 and Atom feeds. Hand-rolled over <see cref="XDocument"/> rather than using
/// System.ServiceModel.Syndication, because that library throws on the slightly-off date formats lots of
/// real-world feeds use; here a bad date just means "no date" instead of a failed feed.
/// </summary>
public partial class RssNewsFeedReader : INewsFeedReader
{
    private const int MaxFeedBytes = 5 * 1024 * 1024;
    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";

    private readonly HttpClient _httpClient;

    public RssNewsFeedReader(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<IReadOnlyList<NewsArticle>> ReadAsync(string feedUrl, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync(feedUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength > MaxFeedBytes)
        {
            throw new InvalidDataException($"Feed is larger than the {MaxFeedBytes / 1024 / 1024} MB limit.");
        }

        // Buffer with a hard cap in case the server lied about (or omitted) Content-Length.
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > MaxFeedBytes)
            {
                throw new InvalidDataException($"Feed is larger than the {MaxFeedBytes / 1024 / 1024} MB limit.");
            }

            buffer.Write(chunk, 0, read);
        }

        buffer.Position = 0;
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, Async = true };
        using var reader = XmlReader.Create(buffer, settings);
        var document = await XDocument.LoadAsync(reader, LoadOptions.None, cancellationToken);

        var articles = document.Root?.Name == Atom + "feed"
            ? ParseAtom(document.Root)
            : ParseRss(document);

        return articles
            .Where(a => !string.IsNullOrWhiteSpace(a.Id) && !string.IsNullOrWhiteSpace(a.Title))
            .OrderByDescending(a => a.PublishedAt ?? DateTimeOffset.MinValue)
            .ToList();
    }

    private static IEnumerable<NewsArticle> ParseRss(XDocument document)
    {
        foreach (var item in document.Descendants("item"))
        {
            var link = UnwrapRedirect(item.Element("link")?.Value.Trim() ?? string.Empty);
            var id = item.Element("guid")?.Value.Trim();

            // <source> is Google News's; Bing News puts it (and a thumbnail) in its own "News:" namespace.
            var source = (item.Element("source") ?? ElementByLocalName(item, "Source"))?.Value.Trim();
            var title = CleanTitle(item.Element("title")?.Value, source);

            yield return new NewsArticle(
                Id: string.IsNullOrEmpty(id) ? link : id,
                Title: title,
                Url: link,
                Summary: CleanSummary(item.Element("description")?.Value, title),
                SourceName: string.IsNullOrEmpty(source) ? null : source,
                PublishedAt: ParseDate(item.Element("pubDate")?.Value),
                ImageUrl: ParseRssImage(item));
        }
    }

    private static IEnumerable<NewsArticle> ParseAtom(XElement feed)
    {
        foreach (var entry in feed.Elements(Atom + "entry"))
        {
            var link = (entry.Elements(Atom + "link")
                    .FirstOrDefault(l => (string?)l.Attribute("rel") is null or "alternate")
                ?? entry.Element(Atom + "link"))
                ?.Attribute("href")?.Value.Trim() ?? string.Empty;
            var id = entry.Element(Atom + "id")?.Value.Trim();
            var title = CleanTitle(entry.Element(Atom + "title")?.Value, null);
            var summary = entry.Element(Atom + "summary")?.Value ?? entry.Element(Atom + "content")?.Value;

            yield return new NewsArticle(
                Id: string.IsNullOrEmpty(id) ? link : id,
                Title: title,
                Url: link,
                Summary: CleanSummary(summary, title),
                SourceName: entry.Element(Atom + "author")?.Element(Atom + "name")?.Value.Trim(),
                PublishedAt: ParseDate(entry.Element(Atom + "published")?.Value ?? entry.Element(Atom + "updated")?.Value),
                ImageUrl: null);
        }
    }

    private static XElement? ElementByLocalName(XElement parent, string localName)
        => parent.Elements().FirstOrDefault(e => e.Name.LocalName == localName && e.Name.Namespace != XNamespace.None);

    /// <summary>
    /// Bing News links go through a click-tracking redirect (<c>bing.com/news/apiclick.aspx?...&amp;url=...</c>)
    /// whose tracking params change between requests. Use the real article URL inside it, so links are clean and
    /// the article's ID stays stable across polls (otherwise every article would look "new" every time).
    /// </summary>
    private static string UnwrapRedirect(string link)
    {
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri)
            || !uri.Host.EndsWith("bing.com", StringComparison.OrdinalIgnoreCase)
            || !uri.AbsolutePath.Contains("apiclick", StringComparison.OrdinalIgnoreCase))
        {
            return link;
        }

        foreach (var pair in uri.Query.TrimStart('?').Split('&'))
        {
            if (pair.StartsWith("url=", StringComparison.OrdinalIgnoreCase))
            {
                var target = Uri.UnescapeDataString(pair[4..]);
                if (WebLinks.TryParse(target, out var targetUri))
                {
                    return targetUri.ToString();
                }
            }
        }

        return link;
    }

    /// <summary>Thumbnail from Bing's <c>News:Image</c> or a standard Media RSS <c>media:content</c>/<c>media:thumbnail</c>.</summary>
    private static string? ParseRssImage(XElement item)
    {
        var bingImage = ElementByLocalName(item, "Image")?.Value.Trim();
        if (!string.IsNullOrEmpty(bingImage))
        {
            // Bing serves a tiny default thumbnail; it supports asking for a bigger size via w/h params.
            var url = bingImage.Replace("http://", "https://", StringComparison.OrdinalIgnoreCase);
            return url + (url.Contains('?') ? "&" : "?") + "w=800&h=450&c=14";
        }

        var media = item.Elements().FirstOrDefault(e => e.Name.LocalName is "content" or "thumbnail"
            && e.Name.NamespaceName == "http://search.yahoo.com/mrss/"
            && (string?)e.Attribute("medium") is null or "image");
        return media?.Attribute("url")?.Value;
    }

    /// <summary>Google News appends " - {Source}" to every title; drop it since the source is shown separately.</summary>
    private static string CleanTitle(string? title, string? source)
    {
        title = StripHtml(title ?? string.Empty);
        if (!string.IsNullOrEmpty(source) && title.EndsWith(" - " + source, StringComparison.Ordinal))
        {
            title = title[..^(source.Length + 3)];
        }

        return title;
    }

    private static string? CleanSummary(string? summary, string title)
    {
        if (string.IsNullOrWhiteSpace(summary))
        {
            return null;
        }

        var text = StripHtml(summary);

        // Some aggregators (Google News included) just repeat the title as the summary - not worth showing.
        return text.Length == 0 || text.StartsWith(title, StringComparison.OrdinalIgnoreCase) ? null : text;
    }

    private static string StripHtml(string html)
    {
        var text = HtmlTagRegex().Replace(html, " ");
        text = WebUtility.HtmlDecode(text);
        return WhitespaceRegex().Replace(text, " ").Trim();
    }

    private static DateTimeOffset? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        value = value.Trim();

        // RFC 1123 ("Sat, 31 Oct 2026 22:15:25 GMT") and ISO 8601 cover nearly every feed.
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            return parsed;
        }

        // RFC 822 with a named zone like "EST" that TryParse doesn't understand - drop the zone and assume UTC.
        var lastSpace = value.LastIndexOf(' ');
        if (lastSpace > 0 && DateTimeOffset.TryParse(value[..lastSpace], CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out parsed))
        {
            return parsed;
        }

        return null;
    }

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
