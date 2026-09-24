namespace Notchle.Core;

// Mirror of Sources/NotchleCore/Contracts/History.swift: the play history (Tren, 2026-09-24:
// "keep the data of the ones i've gotten correct, in how many tries, etc. i want to be able to
// look back at it later in a diff tab within the notch thingy").

/// One played track, appended when its outcome is decided (GameEffect.RecordOutcome).
/// <param name="TierIndex">Tier the track was guessed at (0 = first try); null when missed.</param>
/// <param name="ListingRef">The playlist / album / artist it came from; null if unknown.</param>
/// <param name="ArtworkUrl">The album cover (ArtworkResolver), resolved after the outcome was
/// decided; null when unknown or offline.</param>
public sealed record HistoryEntry(
    Guid Id,
    DateTimeOffset Date,
    string TrackId,
    string Title,
    IReadOnlyList<string> Artists,
    string ListingName,
    SourceRef? ListingRef,
    bool Correct,
    int? TierIndex,
    int WrongGuesses,
    int Skips,
    Uri? ArtworkUrl = null)
{
    // Value equality over the artist list, like Track and the Swift struct.
    public bool Equals(HistoryEntry? other) =>
        other is not null && Id == other.Id && Date == other.Date && TrackId == other.TrackId
        && Title == other.Title && ListingName == other.ListingName && Equals(ListingRef, other.ListingRef)
        && Correct == other.Correct && TierIndex == other.TierIndex && WrongGuesses == other.WrongGuesses
        && Skips == other.Skips && Equals(ArtworkUrl, other.ArtworkUrl) && Artists.SequenceEqual(other.Artists);

    public override int GetHashCode() => HashCode.Combine(Id, Date, TrackId, Title, Correct, TierIndex, Artists.Count);
}

/// Totals over a history, oldest entry first. Pure.
/// <param name="AverageTries">Mean of TierIndex + 1 over the correct entries; null when none.</param>
/// <param name="CorrectByTier">Correct entries per tier: [0] = first try. As long as the highest
/// tier seen, empty when nothing was guessed.</param>
/// <param name="BestStreak">Longest run of consecutive correct entries.</param>
/// <param name="CurrentStreak">Run of correct entries at the end (the newest).</param>
public sealed record HistoryStats(
    int Total,
    int CorrectCount,
    double Accuracy,
    double? AverageTries,
    IReadOnlyList<int> CorrectByTier,
    int BestStreak,
    int CurrentStreak)
{
    public int MissedCount => Total - CorrectCount;

    public static HistoryStats From(IReadOnlyList<HistoryEntry> chronological)
    {
        var correct = chronological.Where(e => e.Correct && e.TierIndex is not null).ToList();
        var byTier = new int[correct.Count == 0 ? 0 : correct.Max(e => e.TierIndex!.Value) + 1];
        foreach (var e in correct) byTier[e.TierIndex!.Value]++;
        int best = 0, run = 0;
        foreach (var e in chronological)
        {
            run = e.Correct ? run + 1 : 0;
            best = Math.Max(best, run);
        }
        var total = chronological.Count;
        var correctCount = chronological.Count(e => e.Correct);
        return new HistoryStats(
            total,
            correctCount,
            total == 0 ? 0 : (double)correctCount / total,
            correct.Count == 0 ? null : correct.Average(e => e.TierIndex!.Value + 1.0),
            byTier,
            best,
            run);
    }

    // Value equality over the per-tier list.
    public bool Equals(HistoryStats? other) =>
        other is not null && Total == other.Total && CorrectCount == other.CorrectCount
        && Accuracy.Equals(other.Accuracy) && Nullable.Equals(AverageTries, other.AverageTries)
        && BestStreak == other.BestStreak && CurrentStreak == other.CurrentStreak
        && CorrectByTier.SequenceEqual(other.CorrectByTier);

    public override int GetHashCode() => HashCode.Combine(Total, CorrectCount, BestStreak, CurrentStreak);
}
