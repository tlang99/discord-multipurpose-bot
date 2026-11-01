namespace ModBot.Configuration;

public class NewsOptions
{
    public const string SectionName = "News";

    /// <summary>How often every subscribed feed is checked for new articles.</summary>
    public int PollIntervalMinutes { get; set; } = 15;

    /// <summary>Cap on how many new articles a single feed can post per check, so a burst of news (or a feed
    /// that reshuffles its item IDs) can't flood the channel.</summary>
    public int MaxPostsPerPoll { get; set; } = 5;
}
