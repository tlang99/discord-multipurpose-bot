using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModBot.Configuration;

namespace ModBot.Services.ContentModeration;

/// <summary>
/// Calls Sightengine's check.json endpoint with the nudity-2.1 and offensive models.
/// The same endpoint transparently handles animated GIFs (it samples all frames), so no
/// separate code path is needed for static images vs. GIFs - see
/// https://sightengine.com/faq/process-animated-gif-images.
/// </summary>
public class SightengineImageModerationService : IImageModerationService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _httpClient;
    private readonly SightengineOptions _options;
    private readonly ILogger<SightengineImageModerationService> _logger;

    public SightengineImageModerationService(
        HttpClient httpClient,
        IOptions<SightengineOptions> options,
        ILogger<SightengineImageModerationService> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public bool IsConfigured => _options.IsConfigured;

    public async Task<ImageModerationReport> CheckImageAsync(string imageUrl, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            return ImageModerationReport.Failed("Sightengine API credentials are not configured.");
        }

        var query = "check.json"
            + $"?models=nudity-2.1,offensive"
            + $"&api_user={Uri.EscapeDataString(_options.ApiUser)}"
            + $"&api_secret={Uri.EscapeDataString(_options.ApiSecret)}"
            + $"&url={Uri.EscapeDataString(imageUrl)}";

        try
        {
            using var response = await _httpClient.GetAsync(query, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var payload = JsonSerializer.Deserialize<SightengineResponse>(body, JsonOptions);

            if (payload is null || payload.Status != "success")
            {
                var message = payload?.Error?.Message ?? $"Unexpected response (HTTP {(int)response.StatusCode}).";
                _logger.LogWarning("Sightengine check failed for {Url}: {Message}", imageUrl, message);
                return ImageModerationReport.Failed(message);
            }

            var explicitScore = Max(payload.Nudity?.SexualActivity, payload.Nudity?.SexualDisplay, payload.Nudity?.Erotica);
            var suggestiveScore = Max(payload.Nudity?.VerySuggestive, payload.Nudity?.Suggestive);
            var offensiveScore = payload.Offensive?.Prob ?? 0;

            return new ImageModerationReport(true, explicitScore, suggestiveScore, offensiveScore);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Sightengine request failed for {Url}", imageUrl);
            return ImageModerationReport.Failed(ex.Message);
        }
    }

    private static double Max(params double?[] values) => values.Select(v => v ?? 0).DefaultIfEmpty(0).Max();

    private class SightengineResponse
    {
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("error")] public SightengineError? Error { get; set; }
        [JsonPropertyName("nudity")] public NudityScores? Nudity { get; set; }
        [JsonPropertyName("offensive")] public OffensiveScores? Offensive { get; set; }
    }

    private class SightengineError
    {
        [JsonPropertyName("message")] public string? Message { get; set; }
    }

    private class NudityScores
    {
        [JsonPropertyName("sexual_activity")] public double? SexualActivity { get; set; }
        [JsonPropertyName("sexual_display")] public double? SexualDisplay { get; set; }
        [JsonPropertyName("erotica")] public double? Erotica { get; set; }
        [JsonPropertyName("very_suggestive")] public double? VerySuggestive { get; set; }
        [JsonPropertyName("suggestive")] public double? Suggestive { get; set; }
    }

    private class OffensiveScores
    {
        [JsonPropertyName("prob")] public double? Prob { get; set; }
    }
}
