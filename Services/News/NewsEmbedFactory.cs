using Discord;

namespace ModBot.Services.News;

public static class NewsEmbedFactory
{
    private static readonly Color NewsColor = new(0x2B, 0x8A, 0x3E);

    /// <param name="writeUp">The bot's own write-up, used as the description in place of the site's summary.</param>
    /// <param name="showOriginalSummary">With a write-up, also show the site's summary in a field, to check against.</param>
    public static Embed Build(NewsArticle article, string feedLabel, NewsWriteUp? writeUp = null,
        bool showOriginalSummary = true)
    {
        var source = article.SourceName is { Length: > 0 } name ? name : null;
        var builder = new EmbedBuilder()
            .WithTitle(Truncate(article.Title, EmbedBuilder.MaxTitleLength))
            .WithColor(NewsColor);

        if (Uri.TryCreate(article.Url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            builder.WithUrl(uri.ToString());
        }

        if (writeUp is not null)
        {
            builder.WithDescription(writeUp.Text);
            if (showOriginalSummary && article.Summary is { Length: > 0 } original)
            {
                builder.AddField($"From {source ?? "the article"}", Truncate(original, 300));
            }

            // Says plainly that the description is the bot's take, not the outlet's words.
            builder.WithFooter($"{(source is null ? "" : source + " • ")}{feedLabel} • AI write-up by {writeUp.WriterName}");
        }
        else
        {
            if (article.Summary is { Length: > 0 } summary)
            {
                builder.WithDescription(Truncate(summary, 350));
            }

            builder.WithFooter(source is null ? feedLabel : $"{source} • {feedLabel}");
        }

        if (Uri.TryCreate(article.ImageUrl, UriKind.Absolute, out var imageUri) && imageUri.Scheme == "https")
        {
            builder.WithImageUrl(imageUri.ToString());
        }

        if (article.PublishedAt is DateTimeOffset published)
        {
            builder.WithTimestamp(published);
        }

        return builder.Build();
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..(max - 1)].TrimEnd() + "…";
}
