using System.Buffers;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using StbImageSharp;
using DiscordColor = Discord.Color;

namespace ModBot.Modules.Music;


// Grab the album art from the song in order to display it on the music menu embedding
public partial class AlbumArt
{
    private const int MaxBytes = 2 * 1024 * 1024;
    
    private const int MaxRemembered = 256;
    
    private const int SampleSize = 40;

    private readonly HttpClient _httpClient;
    private readonly ILogger<AlbumArt> _logger;
    private readonly ConcurrentDictionary<string, Lazy<Task<DiscordColor?>>> _accents = new();

    public AlbumArt(HttpClient httpClient, ILogger<AlbumArt> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }
    
    public static string? DisplayUrl(Uri? artwork)
    {
        if (artwork is not { Scheme: "https" })
        {
            return null;
        }

        if (artwork.Host == "i.ytimg.com" && YouTubeThumbnail().Match(artwork.AbsolutePath) is { Success: true } match)
        {
            return $"https://i.ytimg.com/vi/{match.Groups["id"].Value}/mqdefault.jpg";
        }

        return artwork.AbsoluteUri;
    }
    
    public Task<DiscordColor?> AccentAsync(string? url)
    {
        if (url is null)
        {
            return Task.FromResult<DiscordColor?>(null);
        }

        if (_accents.Count >= MaxRemembered)
        {
            _accents.Clear();
        }

        return _accents.GetOrAdd(url, u => new Lazy<Task<DiscordColor?>>(() => FetchAccentAsync(u))).Value;
    }

    private async Task<DiscordColor?> FetchAccentAsync(string url)
    {
        try
        {
            using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode ||
                response.Content.Headers.ContentType?.MediaType?.StartsWith("image/") != true ||
                response.Content.Headers.ContentLength > MaxBytes)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync();
            var buffer = ArrayPool<byte>.Shared.Rent(MaxBytes + 1);
            try
            {
                int read = 0, got;
                while (read <= MaxBytes && (got = await stream.ReadAsync(buffer.AsMemory(read, MaxBytes + 1 - read))) > 0)
                {
                    read += got;
                }

                if (read > MaxBytes)
                {
                    return null;
                }

                var image = ImageResult.FromMemory(buffer.AsSpan(0, read).ToArray(), ColorComponents.RedGreenBlue);
                return PickAccent(Sample(image.Data, image.Width, image.Height));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        catch (Exception ex)
        {
            // Not a picture it can read, a timeout, a network error: the menu just keeps its usual color.
            // Remembered as "no color", so a cover that can't be read isn't fetched on every redraw.
            _logger.LogDebug(ex, "Couldn't read the album art at {Url}.", url);
            return null;
        }
    }
    
    private static Rgb[] Sample(byte[] data, int width, int height)
    {
        var pixels = new Rgb[SampleSize * SampleSize];
        for (var y = 0; y < SampleSize; y++)
        {
            for (var x = 0; x < SampleSize; x++)
            {
                var at = ((y * height / SampleSize) * width + x * width / SampleSize) * 3;
                pixels[y * SampleSize + x] = new Rgb(data[at], data[at + 1], data[at + 2]);
            }
        }

        return pixels;
    }

    public static DiscordColor PickAccent(ReadOnlySpan<Rgb> pixels)
    {
        const int buckets = 36; // 10° of hue each
        Span<double> weight = stackalloc double[buckets];
        Span<double> red = stackalloc double[buckets];
        Span<double> green = stackalloc double[buckets];
        Span<double> blue = stackalloc double[buckets];
        double brightness = 0;
        var lit = 0;

        foreach (var pixel in pixels)
        {
            var (hue, saturation, value) = ToHsv(pixel);
            if (value >= 0.15)
            {
                brightness += value;
                lit++;
            }

            if (value < 0.2 || saturation < 0.25)
            {
                continue;
            }

            var w = saturation * value;
            var bucket = (int)(hue / 10) % buckets;
            weight[bucket] += w;
            red[bucket] += pixel.R * w;
            green[bucket] += pixel.G * w;
            blue[bucket] += pixel.B * w;
        }
        
        var best = -1;
        double bestScore = 0, total = 0;
        for (var i = 0; i < buckets; i++)
        {
            total += weight[i];
            var score = weight[i] + (weight[(i + buckets - 1) % buckets] + weight[(i + 1) % buckets]) / 2;
            if (score > bestScore)
            {
                bestScore = score;
                best = i;
            }
        }
        
        if (best < 0 || total < pixels.Length * 0.02)
        {
            var grey = (byte)Math.Round(255 * Math.Clamp(lit == 0 ? 0 : brightness / lit, 0.55, 0.75));
            return new DiscordColor(grey, grey, grey);
        }

        double r = 0, g = 0, b = 0, sum = 0;
        foreach (var i in (ReadOnlySpan<int>)[(best + buckets - 1) % buckets, best, (best + 1) % buckets])
        {
            r += red[i];
            g += green[i];
            b += blue[i];
            sum += weight[i];
        }

        var (h, s, v) = ToHsv(new Rgb((byte)(r / sum), (byte)(g / sum), (byte)(b / sum)));
        return FromHsv(h, Math.Clamp(s, 0.45, 0.85), Math.Clamp(v, 0.6, 0.92));
    }

    private static (double Hue, double Saturation, double Value) ToHsv(Rgb pixel)
    {
        double r = pixel.R / 255.0, g = pixel.G / 255.0, b = pixel.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var delta = max - Math.Min(r, Math.Min(g, b));
        var hue = delta == 0 ? 0
            : max == r ? 60 * ((g - b) / delta % 6)
            : max == g ? 60 * ((b - r) / delta + 2)
            : 60 * ((r - g) / delta + 4);
        return (hue < 0 ? hue + 360 : hue, max == 0 ? 0 : delta / max, max);
    }

    private static DiscordColor FromHsv(double hue, double saturation, double value)
    {
        var c = value * saturation;
        var x = c * (1 - Math.Abs(hue / 60 % 2 - 1));
        var (r, g, b) = (int)(hue / 60) switch
        {
            0 => (c, x, 0.0),
            1 => (x, c, 0.0),
            2 => (0.0, c, x),
            3 => (0.0, x, c),
            4 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        var m = value - c;
        return new DiscordColor((float)(r + m), (float)(g + m), (float)(b + m));
    }

    public readonly record struct Rgb(byte R, byte G, byte B);
    [GeneratedRegex(@"^/vi(?:_webp)?/(?<id>[A-Za-z0-9_-]{6,20})/[^/]+$")]
    private static partial Regex YouTubeThumbnail();
}
