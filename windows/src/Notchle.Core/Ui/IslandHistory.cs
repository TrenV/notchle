using System.Globalization;

namespace Notchle.Core.Ui;

// The History tab of the island (Tren, 2026-09-24): what was played, in how many tries, with
// totals. Mirror of Sources/NotchleMac/UI/HistoryRules.swift. Pure: the WPF view only draws it.

/// The two tabs in the island header.
public enum IslandTab { Play, History }

/// One played track as the list shows it.
/// <paramref name="TrackId"/> / <paramref name="ArtworkUrl"/>: the 28 DIP thumbnail (cached per track).
public sealed record HistoryRow(Guid Id, string Title, string Artists, string Listing, string When, string Badge,
    bool Correct, int Skips, int WrongGuesses, string TrackId = "", Uri? ArtworkUrl = null);

/// Rows of one local calendar day, newest first.
public sealed record HistoryDay(string Heading, IReadOnlyList<HistoryRow> Rows);

/// The stats row above the list.
public sealed record HistoryStatsRow(string Correct, string Accuracy, string AverageTries, string BestStreak);

public static class HistoryRules
{
    public const string PlayLabel = "Play";
    public const string HistoryLabel = "History";
    public const string EmptyText = "No songs yet: play a set and your results show up here.";
    public const string ClearLabel = "Clear history";
    public const string ClearConfirmLabel = "Clear all history?";
    /// The list shows at most this many (newest) entries; the stats cover all visible ones.
    public const int MaxRows = 200;

    /// SPOILER RULE: entries for tracks of the set being played stay hidden until the set ends
    /// (SetComplete / SetFailed) or the playlist is quit (the set is then empty). A failed set is
    /// replayed with the same tracks, so the history would give its answers away.
    public static IReadOnlyList<HistoryEntry> Visible(IReadOnlyList<HistoryEntry> history, GameState state)
    {
        if (state.Phase is GamePhase.SetComplete or GamePhase.SetFailed || state.CurrentSet.Count == 0) return history;
        var hidden = state.CurrentSet.Select(t => t.Id).ToHashSet();
        return history.Where(e => !hidden.Contains(e.TrackId)).ToList();
    }

    /// "1st try", "2nd try", "3rd try", …; "missed".
    public static string Badge(HistoryEntry entry) =>
        !entry.Correct || entry.TierIndex is not { } tier ? "missed" : $"{Ordinal(tier + 1)} try";

    public static string Ordinal(int n)
    {
        var suffix = (n % 100) is 11 or 12 or 13 ? "th" : (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" };
        return $"{n}{suffix}";
    }

    public static HistoryStatsRow StatsRow(HistoryStats stats) => new(
        $"✓ {stats.CorrectCount}/{stats.Total}",
        $"{Math.Round(stats.Accuracy * 100).ToString(CultureInfo.InvariantCulture)}%",
        stats.AverageTries is { } avg ? $"avg {avg.ToString("0.0", CultureInfo.InvariantCulture)} tries" : "avg –",
        $"best streak {stats.BestStreak}");

    /// "just now", "5m ago", "3h ago", "2d ago", then "21 Sep".
    public static string RelativeTime(DateTimeOffset date, DateTimeOffset now, TimeZoneInfo zone)
    {
        var ago = now - date;
        if (ago < TimeSpan.FromMinutes(1)) return "just now";
        if (ago < TimeSpan.FromHours(1)) return $"{(int)ago.TotalMinutes}m ago";
        if (ago < TimeSpan.FromDays(1)) return $"{(int)ago.TotalHours}h ago";
        if (ago < TimeSpan.FromDays(7)) return $"{(int)ago.TotalDays}d ago";
        return TimeZoneInfo.ConvertTime(date, zone).ToString("d MMM", CultureInfo.InvariantCulture);
    }

    /// "Today", "Yesterday", "Mon 21 Sep" (with the year when it isn't this year).
    public static string DayHeading(DateOnly day, DateOnly today) =>
        day == today ? "Today"
        : day == today.AddDays(-1) ? "Yesterday"
        : day.ToString(day.Year == today.Year ? "ddd d MMM" : "ddd d MMM yyyy", CultureInfo.InvariantCulture);

    /// The History screen for <paramref name="history"/> (oldest first) while
    /// <paramref name="state"/> is being played. <paramref name="zone"/> decides the day groups
    /// (local time by default).
    public static IslandScreen.History Screen(IReadOnlyList<HistoryEntry> history, GameState state, DateTimeOffset now,
        bool clearArmed, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        var visible = Visible(history, state);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var days = visible
            .Select((entry, order) => (entry, order))
            .OrderByDescending(x => x.entry.Date).ThenByDescending(x => x.order)
            .Take(MaxRows)
            .GroupBy(x => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(x.entry.Date, zone).DateTime))
            .Select(g => new HistoryDay(DayHeading(g.Key, today), g.Select(x => Row(x.entry, now, zone)).ToList()))
            .ToList();
        return new IslandScreen.History(
            visible.Count == 0 ? null : StatsRow(HistoryStats.From(visible)),
            days,
            visible.Count == 0 ? EmptyText : null,
            history.Count - visible.Count,
            history.Count > 0,
            clearArmed);
    }

    private static HistoryRow Row(HistoryEntry e, DateTimeOffset now, TimeZoneInfo zone) => new(
        e.Id,
        e.Title,
        string.Join(", ", e.Artists),
        e.ListingName,
        RelativeTime(e.Date, now, zone),
        Badge(e),
        e.Correct,
        e.Skips,
        e.WrongGuesses,
        e.TrackId,
        e.ArtworkUrl);

    /// "2 songs from this set show up when it ends."
    public static string? HiddenNote(int hidden) =>
        hidden <= 0 ? null : hidden == 1 ? "1 song from this set shows up when it ends." : $"{hidden} songs from this set show up when it ends.";
}
