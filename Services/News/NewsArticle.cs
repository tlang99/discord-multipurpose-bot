namespace ModBot.Services.News;

/// <summary>A single feed item, normalized across RSS 2.0 and Atom.</summary>
public record NewsArticle(
    string Id,
    string Title,
    string Url,
    string? Summary,
    string? SourceName,
    DateTimeOffset? PublishedAt,
    string? ImageUrl);
