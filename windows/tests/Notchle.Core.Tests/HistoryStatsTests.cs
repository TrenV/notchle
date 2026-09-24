using Notchle.Core;

namespace Notchle.Core.Tests;

public class HistoryStatsTests
{
    internal static HistoryEntry Entry(bool correct, int? tier = null, string id = "t", int minute = 0, int wrong = 0, int skips = 0,
        Uri? artwork = null) =>
        new(Guid.NewGuid(), new DateTimeOffset(2026, 9, 24, 12, minute, 0, TimeSpan.Zero), id, $"Song {id}", ["A", "B"],
            "List", new SourceRef(SourceKind.Playlist, "p"), correct, correct ? tier ?? 0 : null, wrong, skips, artwork);

    [Fact]
    public void EmptyHistory()
    {
        Assert.Equal(new HistoryStats(0, 0, 0, null, [], 0, 0), HistoryStats.From([]));
    }

    [Fact]
    public void TotalsAccuracyAverageTiersAndStreaks()
    {
        // ✓1 ✓2 ✗ ✓1 ✓3 ✓1 ✗ ✓2  (oldest first)
        HistoryEntry[] h =
        [
            Entry(true, 0), Entry(true, 1), Entry(false), Entry(true, 0), Entry(true, 2), Entry(true, 0), Entry(false), Entry(true, 1),
        ];
        var stats = HistoryStats.From(h);
        Assert.Equal(8, stats.Total);
        Assert.Equal(6, stats.CorrectCount);
        Assert.Equal(2, stats.MissedCount);
        Assert.Equal(0.75, stats.Accuracy);
        Assert.Equal((1 + 2 + 1 + 3 + 1 + 2) / 6.0, stats.AverageTries!.Value, 10);
        Assert.Equal([3, 2, 1], stats.CorrectByTier);
        Assert.Equal(3, stats.BestStreak);
        Assert.Equal(1, stats.CurrentStreak);
    }

    [Fact]
    public void AllMissedHasNoAverageAndNoStreak()
    {
        var stats = HistoryStats.From([Entry(false), Entry(false)]);
        Assert.Equal((0, 0.0, (double?)null, 0, 0), (stats.CorrectCount, stats.Accuracy, stats.AverageTries, stats.BestStreak, stats.CurrentStreak));
        Assert.Empty(stats.CorrectByTier);
    }

    [Fact]
    public void CurrentStreakIsTheTrailingRun()
    {
        var stats = HistoryStats.From([Entry(false), Entry(true), Entry(true)]);
        Assert.Equal((2, 2), (stats.BestStreak, stats.CurrentStreak));
    }

    [Fact]
    public void EntriesCompareByValueIncludingArtists()
    {
        var a = Entry(true, 1);
        var b = a with { Artists = ["A", "B"] };
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, a with { Artists = ["A"] });
        Assert.NotEqual(a, a with { ArtworkUrl = new Uri("https://i.scdn.co/image/x") });
    }
}
