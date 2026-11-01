using Discord;

namespace ModBot.Services.News;

public static class NewsEmbedFactory
{
    private static readonly Color NewsColor = new(0x2B, 0x8A, 0x3E);

    public static Embed Build(NewsArticle article, string feedLabel)
    {
        var builder = new EmbedBuilder()
            .WithTitle(Truncate(article.Title, EmbedBuilder.MaxTitleLength))
            .WithColor(NewsColor)
            .WithFooter(article.SourceName is { Length: > 0 } source ? $"{source} • {feedLabel}" : feedLabel);

        if (Uri.TryCreate(article.Url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            builder.WithUrl(uri.ToString());
        }

        if (article.Summary is { Length: > 0 } summary)
        {
            builder.WithDescription(Truncate(summary, 350));
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
