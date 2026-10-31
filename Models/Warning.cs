namespace ModBot.Models;

public class Warning
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public ulong GuildId { get; set; }

    public ulong UserId { get; set; }

    public ulong ModeratorId { get; set; }

    public string Reason { get; set; } = "No reason provided";

    public DateTimeOffset TimestampUtc { get; set; } = DateTimeOffset.UtcNow;
}
