using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using ModBot.Common;

namespace ModBot.Services.Search;

/// <summary>
/// Finds the preview image a web page names for itself (<c>og:image</c> or <c>twitter:image</c>), for the picture on a
/// search answer, like the image on a news post. Opens the page from this machine, through
/// <see cref="ModBot.Services.News.PublicNetworkHttpHandler"/> (set up in Program.cs), so a search result can't point it
/// at the local network. Reads only the start of the page, where those tags live, and gives up quickly: the answer
/// is posted without a picture rather than late.
/// </summary>
public partial class PageImageFinder
{
    /// <summary>The tags are in the page's head, well within this.</summary>
    private const int MaxBytes = 256 * 1024;

    private readonly HttpClient _httpClient;
    private readonly ILogger<PageImageFinder> _logger;

    public PageImageFinder(HttpClient httpClient, ILogger<PageImageFinder> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    /// <summary>The page's preview image (https only), or null if it has none or can't be read.</summary>
    public async Task<string?> FindAsync(string pageUrl, CancellationToken cancellationToken = default)
    {
        if (!WebLinks.TryParse(pageUrl, out var page))
        {
            return null;
        }

        try
        {
            using var response = await _httpClient.GetAsync(page, HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (response.StatusCode != HttpStatusCode.OK ||
                response.Content.Headers.ContentType?.MediaType is { } type && !type.Contains("html"))
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var buffer = new byte[MaxBytes];
            var read = 0;
            int got;
            while (read < buffer.Length && (got = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken)) > 0)
            {
                read += got;
            }

            return FromHtml(Encoding.UTF8.GetString(buffer, 0, read), response.RequestMessage?.RequestUri ?? page);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or
                                   News.BlockedAddressException && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Couldn't read a search result's preview image.");
            return null;
        }
    }

    /// <summary>The preview image named in <paramref name="html"/>, made absolute against <paramref name="page"/>;
    /// https only, since Discord shows it straight from that address.</summary>
    public static string? FromHtml(string html, Uri page)
    {
        foreach (Match tag in MetaTagRegex().Matches(html))
        {
            var attributes = tag.Value;
            var name = AttributeRegex("property").Match(attributes) is { Success: true } p ? p.Groups[1].Value
                : AttributeRegex("name").Match(attributes) is { Success: true } n ? n.Groups[1].Value : "";
            if (!name.Equals("og:image", StringComparison.OrdinalIgnoreCase) &&
                !name.Equals("og:image:url", StringComparison.OrdinalIgnoreCase) &&
                !name.Equals("twitter:image", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (AttributeRegex("content").Match(attributes) is { Success: true } content &&
                Uri.TryCreate(page, WebUtility.HtmlDecode(content.Groups[1].Value.Trim()), out var image) &&
                WebLinks.TryParse(image.AbsoluteUri, out _, httpsOnly: true))
            {
                return image.AbsoluteUri;
            }
        }

        return null;
    }

    [GeneratedRegex(@"<meta\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex MetaTagRegex();

    private static Regex AttributeRegex(string attribute)
        => new($@"\b{attribute}\s*=\s*[""']([^""']*)[""']", RegexOptions.IgnoreCase);
}
