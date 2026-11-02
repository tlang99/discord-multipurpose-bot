using ModBot.Common;
using ModBot.Models;
using ModBot.Modules.Music;
using ModBot.Services.Storage;

namespace ModBot.Tests;

/// <summary>The shared helpers in <c>Common/</c> and the JSON repository that every store is built on.</summary>
public sealed class SharedHelperTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "modbot-tests", Guid.NewGuid().ToString("N"));

    private string FilePath => Path.Combine(_directory, "data.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task Repository_update_says_whether_the_key_was_there()
    {
        var repository = new JsonFileRepository<List<string>>(FilePath);
        await repository.MutateAsync(data => data["a"] = ["one"]);

        Assert.True(await repository.UpdateAsync("a", list => list.Add("two")));
        Assert.False(await repository.UpdateAsync("missing", list => list.Add("x")));
        Assert.Equal(["one", "two"], await repository.GetAsync("a"));
        Assert.Null(await repository.GetAsync("missing"));
    }

    [Fact]
    public async Task Repository_creates_a_value_once_and_returns_the_change_result()
    {
        var repository = new JsonFileRepository<List<string>>(FilePath);

        Assert.Equal(1, await repository.MutateOrCreateAsync("a", () => [], list =>
        {
            list.Add("one");
            return list.Count;
        }));
        Assert.Equal(2, await repository.MutateOrCreateAsync("a", () => throw new InvalidOperationException("exists"),
            list =>
            {
                list.Add("two");
                return list.Count;
            }));

        // Saved to disk, not just the cache.
        Assert.Equal(["one", "two"], await new JsonFileRepository<List<string>>(FilePath).GetAsync("a"));
    }

    [Fact]
    public async Task Repository_remove_says_whether_anything_was_removed()
    {
        var repository = new JsonFileRepository<string>(FilePath);
        await repository.MutateAsync(data => data["a"] = "x");

        Assert.True(await repository.RemoveAsync("a"));
        Assert.False(await repository.RemoveAsync("a"));
    }

    [Fact]
    public async Task Repository_keeps_nothing_from_a_change_that_throws()
    {
        var repository = new JsonFileRepository<string>(FilePath);
        await repository.MutateAsync(data => data["a"] = "before");

        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.MutateAsync(data =>
        {
            data["a"] = "after";
            throw new InvalidOperationException();
        }));

        Assert.Equal("before", await repository.GetAsync("a"));
    }

    [Fact]
    public async Task Keyed_lock_runs_one_at_a_time_per_key()
    {
        var gate = new KeyedLock<int>();
        var running = 0;
        var mostAtOnce = 0;

        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => gate.RunAsync(1, async () =>
        {
            mostAtOnce = Math.Max(mostAtOnce, Interlocked.Increment(ref running));
            await Task.Delay(1);
            Interlocked.Decrement(ref running);
        })));

        Assert.Equal(1, mostAtOnce);
    }

    [Theory]
    [InlineData("short", 10, "short")]
    [InlineData("exactly10!", 10, "exactly10!")]
    [InlineData("this is too long", 10, "this is t…")]
    public void Truncate_cuts_with_an_ellipsis(string value, int max, string expected)
    {
        var cut = TextTools.Truncate(value, max);

        Assert.Equal(expected, cut);
        Assert.True(cut.Length <= max);
    }

    [Fact]
    public void Truncate_never_splits_an_emoji()
    {
        // "abcd" + 😀 (two UTF-16 units) + "efgh": a cut to 6 would otherwise keep only the emoji's first half.
        var cut = TextTools.Truncate("abcd😀efgh", 6);

        Assert.Equal("abcd…", cut);
        Assert.DoesNotContain(cut, char.IsSurrogate);
    }

    [Fact]
    public async Task Repository_writes_nothing_when_updating_or_removing_a_missing_key()
    {
        var repository = new JsonFileRepository<string>(FilePath);
        await repository.MutateAsync(data => data["a"] = "x");
        var writtenAt = File.GetLastWriteTimeUtc(FilePath);
        File.SetLastWriteTimeUtc(FilePath, writtenAt.AddMinutes(-5));

        Assert.False(await repository.UpdateAsync("missing", _ => { }));
        Assert.False(await repository.RemoveAsync("missing"));
        Assert.Equal(writtenAt.AddMinutes(-5), File.GetLastWriteTimeUtc(FilePath));
    }

    [Fact]
    public async Task Periodic_loop_keeps_going_after_a_tick_throws()
    {
        using var stop = new CancellationTokenSource();
        var ticks = 0;

        await PeriodicLoop.RunAsync(TimeSpan.FromMilliseconds(10), () =>
        {
            if (++ticks == 3)
            {
                stop.Cancel();
            }

            throw new InvalidOperationException("boom");
        }, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, "Test job", stop.Token, runFirstTickNow: true);

        Assert.Equal(3, ticks);
    }

    [Theory]
    [InlineData(" #music ", "music")]
    [InlineData("music", "music")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Channel_names_are_cleaned_the_same_way_everywhere(string? configured, string expected)
        => Assert.Equal(expected, ConfiguredChannels.ChannelName(configured));

    [Fact]
    public void Song_links_escape_both_brackets_and_skip_the_preview()
    {
        var song = new QueuedSong { Title = "x](https://evil) [y", Uri = "https://youtu.be/abc" };

        var link = MusicFormatting.SongLink(song);

        // The title's own brackets are escaped, so it can't close the link early or open one of its own.
        Assert.StartsWith(@"[x\]", link);
        Assert.Contains(@" \[y](", link);
        Assert.EndsWith("](<https://youtu.be/abc>)", link);
    }

    [Fact]
    public void Songs_without_a_web_link_are_shown_in_bold()
        => Assert.Equal("**Local file**", MusicFormatting.SongLink(new QueuedSong { Title = "Local file" }));
}
