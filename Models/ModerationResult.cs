namespace ModBot.Models;

public record ModerationResult(bool Success, string Message)
{
    public static ModerationResult Ok(string message) => new(true, message);
    public static ModerationResult Fail(string message) => new(false, message);
}
