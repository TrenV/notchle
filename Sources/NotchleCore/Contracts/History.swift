import Foundation

// Play history (Tren, 2026-09-24: "keep the data of the ones i've gotten correct, in how many
// tries, etc. i want to be able to look back at it later in a diff tab within the notch thingy").
// The engine emits `GameEffect.recordOutcome` once per track; the platform stamps date and
// listing, appends a `HistoryEntry` and persists it with `HistoryStore`.

/// One played track.
public struct HistoryEntry: Sendable, Hashable, Codable, Identifiable {
    public let id: UUID
    public let date: Date
    public let trackID: String
    public let title: String
    public let artists: [String]
    public let listingName: String
    public let listingRef: SourceRef?
    public let correct: Bool
    /// Tier it was guessed at (0 = 5s); nil when missed.
    public let tierIndex: Int?
    public let wrongGuesses: Int
    public let skips: Int
    /// Album cover (Spotify oEmbed `thumbnail_url`, 300×300), resolved after the outcome.
    /// JSON key "artworkURL"; absent in older files (decodes as nil).
    public let artworkURL: URL?

    public init(id: UUID = UUID(), date: Date, trackID: String, title: String, artists: [String],
                listingName: String, listingRef: SourceRef?, correct: Bool, tierIndex: Int?,
                wrongGuesses: Int, skips: Int, artworkURL: URL? = nil) {
        self.id = id
        self.date = date
        self.trackID = trackID
        self.title = title
        self.artists = artists
        self.listingName = listingName
        self.listingRef = listingRef
        self.correct = correct
        self.tierIndex = correct ? tierIndex : nil
        self.wrongGuesses = wrongGuesses
        self.skips = skips
        self.artworkURL = artworkURL
    }

    /// The same entry with its cover set.
    public func withArtworkURL(_ url: URL?) -> HistoryEntry {
        HistoryEntry(id: id, date: date, trackID: trackID, title: title, artists: artists,
                     listingName: listingName, listingRef: listingRef, correct: correct,
                     tierIndex: tierIndex, wrongGuesses: wrongGuesses, skips: skips, artworkURL: url)
    }

    /// The entry for a `.recordOutcome` effect.
    public init(id: UUID = UUID(), date: Date, track: Track, outcome: TrackOutcome,
                wrongGuesses: Int, skips: Int, listingName: String, listingRef: SourceRef?,
                artworkURL: URL? = nil) {
        let tier: Int? = if case .correct(let t) = outcome { t } else { nil }
        self.init(id: id, date: date, trackID: track.id, title: track.title, artists: track.artists,
                  listingName: listingName, listingRef: listingRef, correct: tier != nil, tierIndex: tier,
                  wrongGuesses: wrongGuesses, skips: skips, artworkURL: artworkURL)
    }
}

/// Summary numbers over a history, pure.
public struct HistoryStats: Sendable, Hashable {
    public let total: Int
    public let correct: Int
    /// correct / total, 0 when empty.
    public let accuracy: Double
    /// Mean of (tierIndex + 1) over correct entries; nil when none is correct.
    public let averageTries: Double?
    /// Correct entries per tier index (index 0 = guessed at the first snippet). Sized to the
    /// highest tier seen.
    public let perTier: [Int]
    /// Longest run of consecutive correct entries, in date order.
    public let bestStreak: Int
    /// Run of correct entries ending at the newest entry.
    public let currentStreak: Int

    /// `entries` in any order; they are sorted by date (stable) first.
    public init(_ entries: [HistoryEntry]) {
        let ordered = entries.enumerated()
            .sorted { $0.element.date != $1.element.date ? $0.element.date < $1.element.date : $0.offset < $1.offset }
            .map(\.element)
        total = ordered.count
        let hits = ordered.filter(\.correct)
        correct = hits.count
        accuracy = total == 0 ? 0 : Double(correct) / Double(total)
        let tiers = hits.compactMap(\.tierIndex)
        averageTries = tiers.isEmpty ? nil : Double(tiers.reduce(0) { $0 + $1 + 1 }) / Double(tiers.count)
        var counts = Array(repeating: 0, count: (tiers.max() ?? -1) + 1)
        for t in tiers where t >= 0 { counts[t] += 1 }
        perTier = counts
        var best = 0, run = 0
        for e in ordered {
            run = e.correct ? run + 1 : 0
            best = max(best, run)
        }
        bestStreak = best
        currentStreak = run
    }
}

/// The spoiler rule: while a set is in progress its tracks may come round again (a replay after
/// a miss), so the history must not show them. They appear once the set ends
/// (`setComplete`/`setFailed`) or the player leaves the listing (idle, loading, exhausted).
public enum HistorySpoilerFilter {
    /// Track ids that must stay hidden in `state`.
    public static func hiddenTrackIDs(_ state: GameState) -> Set<String> {
        switch state.phase {
        case .idle, .loading, .setComplete, .setFailed, .exhausted:
            return []
        case .playingSnippet, .guessing, .wrong, .correct, .revealed, .error:
            return Set(state.currentSet.map(\.id))
        }
    }

    /// `entries` minus every entry for a hidden track, newest first.
    public static func visible(_ entries: [HistoryEntry], in state: GameState) -> [HistoryEntry] {
        let hidden = hiddenTrackIDs(state)
        return entries.filter { !hidden.contains($0.trackID) }.sorted { $0.date > $1.date }
    }
}
