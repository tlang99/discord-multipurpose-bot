namespace ModBot.Configuration;

public class NewsOptions
{
    public const string SectionName = "News";

    /// <summary>How often every subscribed feed is checked for new articles.</summary>
    public int PollIntervalMinutes { get; set; } = 15;

    /// <summary>Cap on how many new articles a single feed can post per check, so a burst of news (or a feed
    /// that reshuffles its item IDs) can't flood the channel.</summary>
    public int MaxPostsPerPoll { get; set; } = 5;

    /// <summary>Have the chatbot write each article up in its own words and personality (the server's
    /// /chat personality). Needs the chatbot set up; without it, or if a write-up fails, the article posts with the
    /// news site's own summary as before.</summary>
    public bool AiWriteUps { get; set; } = true;

    /// <summary>With AI write-ups, also show the news site's own summary under the bot's version, so readers have the
    /// source's wording to check against.</summary>
    public bool ShowOriginalSummary { get; set; } = true;
}
