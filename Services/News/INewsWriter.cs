namespace ModBot.Services.News;

/// <summary>Writes news articles up in the chatbot's voice for the news channel (see <see cref="NewsWriter"/>).</summary>
public interface INewsWriter
{
    /// <summary>The write-up, or null when one shouldn't be used (turned off, model unreachable, word filter, a link
    /// in the output). Never throws for model problems: the caller then posts the site's own summary.</summary>
    /// <param name="botName">The bot's name in that server, for the prompt and the credit.</param>
    Task<NewsWriteUp?> WriteAsync(NewsArticle article, ulong guildId, string botName, string serverName,
        CancellationToken cancellationToken = default);
}
