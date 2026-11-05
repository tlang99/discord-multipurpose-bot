using Discord;
using ModBot.Common;

namespace ModBot.Services.News;

public static class NewsEmbedFactory
{
    /// <param name="writeUp">The bot's own write-up, used as the description in place of the site's summary.</param>
    /// <param name="showOriginalSummary">With a write-up, also show the site's summary in a field, to check against.</param>
    public static Embed Build(NewsArticle article, string feedLabel, NewsWriteUp? writeUp = null,
        bool showOriginalSummary = true)
    {
        var source = article.SourceName is { Length: > 0 } name ? name : null;
        var builder = new EmbedBuilder()
            .WithTitle(TextTools.Truncate(article.Title, EmbedBuilder.MaxTitleLength))
            .WithColor(Colors.News);

        if (WebLinks.TryParse(article.Url, out var uri))
        {
            builder.WithUrl(uri.ToString());
        }

        if (writeUp is not null)
        {
            builder.WithDescription(writeUp.Text);
            if (showOriginalSummary && article.Summary is { Length: > 0 } original)
            {
                // The source name comes from the feed: an over-long one would make Discord.Net throw, and the article
                // would then fail on every check.
                builder.AddField(TextTools.Truncate($"From {source ?? "the article"}", EmbedFieldBuilder.MaxFieldNameLength),
                    TextTools.TruncateEscaped(original, 300));
            }

            // Says plainly that the description is the bot's take, not the outlet's words.
            builder.WithFooter(TextTools.Truncate($"{(source is null ? "" : source + " • ")}{feedLabel} • AI write-up by " +
                                        writeUp.WriterName, EmbedFooterBuilder.MaxFooterTextLength));
        }
        else
        {
            if (article.Summary is { Length: > 0 } summary)
            {
                // Summaries may be escaped already (search results are), so the cut mustn't split a "\*".
                builder.WithDescription(TextTools.TruncateEscaped(summary, 350));
            }

            builder.WithFooter(TextTools.Truncate(source is null ? feedLabel : $"{source} • {feedLabel}",
                EmbedFooterBuilder.MaxFooterTextLength));
        }

        if (WebLinks.TryParse(article.ImageUrl, out var imageUri, httpsOnly: true))
        {
            builder.WithImageUrl(imageUri.ToString());
        }

        if (article.PublishedAt is DateTimeOffset published)
        {
            builder.WithTimestamp(published);
        }

        return builder.Build();
    }
}
