import Foundation

// Shared, frozen contracts. Wave-2 agents build against these; changes go through the
// integrator, not through an individual agent. Foundation-only by design (Windows later).

/// One song as the game knows it. `id` is the Spotify track id (base62).
public struct Track: Sendable, Hashable, Codable, Identifiable {
    public let id: String
    /// `spotify:track:<id>`, what the Spotify desktop app plays.
    public let uri: String
    public let title: String
    /// All credited artists, in credit order. A correct artist guess names every one of them.
    public let artists: [String]
    public let durationMs: Int
    /// ~30s preview clip, used by the preview fallback player. Not every track has one.
    public let previewURL: URL?

    public init(id: String, uri: String, title: String, artists: [String], durationMs: Int, previewURL: URL?) {
        self.id = id
        self.uri = uri
        self.title = title
        self.artists = artists
        self.durationMs = durationMs
        self.previewURL = previewURL
    }
}

/// What a pasted link points at. Raw values are persisted (progress.json, history): additive only.
public enum SourceKind: String, Sendable, Codable, CaseIterable {
    case playlist, album, artist
    /// `music.apple.com/<storefront>/album/…/<digits>`.
    case appleMusicAlbum
    /// `music.apple.com/<storefront>/playlist/…/pl.<id>`.
    case appleMusicPlaylist

    /// Apple Music kinds are read by `AppleMusicTrackSource`, the rest by `EmbedTrackSource`.
    public var isAppleMusic: Bool { self == .appleMusicAlbum || self == .appleMusicPlaylist }
}

/// A parsed Spotify link, e.g. `https://open.spotify.com/playlist/37i9dQZF1DXcBWIGoYBM5M?si=…`
/// or `spotify:album:…`. Parsing lives in `SourceRef+Parsing.swift` (Wave 2, agent A).
public struct SourceRef: Sendable, Hashable, Codable {
    public let kind: SourceKind
    public let id: String
    /// Apple Music storefront from the link ("us", "nl"); nil for Spotify. Optional so older
    /// progress.json files (without the key) still decode.
    public let storefront: String?

    public init(kind: SourceKind, id: String, storefront: String? = nil) {
        self.kind = kind
        self.id = id
        self.storefront = storefront
    }
}

/// The tracks behind a `SourceRef`, plus a display name ("Today's Top Hits").
public struct SourceListing: Sendable, Hashable, Codable {
    public let ref: SourceRef
    public let name: String
    public let tracks: [Track]

    public init(ref: SourceRef, name: String, tracks: [Track]) {
        self.ref = ref
        self.name = name
        self.tracks = tracks
    }
}

/// What the player typed.
public struct Guess: Sendable, Hashable, Codable {
    public var title: String
    public var artist: String

    public init(title: String, artist: String) {
        self.title = title
        self.artist = artist
    }
}

/// Result of judging one guess. Both parts must be right for the guess to count.
public struct Verdict: Sendable, Hashable, Codable {
    public let titleCorrect: Bool
    public let artistCorrect: Bool

    public var isCorrect: Bool { titleCorrect && artistCorrect }

    public init(titleCorrect: Bool, artistCorrect: Bool) {
        self.titleCorrect = titleCorrect
        self.artistCorrect = artistCorrect
    }
}

/// How one track in a set ended.
public enum TrackOutcome: Sendable, Hashable, Codable {
    /// Guessed right; `tierIndex` is the snippet tier it was guessed at (0 = 5s).
    case correct(tierIndex: Int)
    /// Missed at the last tier, gave up, or skipped.
    case missed
}

public struct GameConfig: Sendable, Hashable, Codable {
    /// Snippet lengths in seconds, one per attempt. Agreed default: 5, 10, 15.
    public var tiers: [Double]
    public var setSize: Int
    /// Where the snippet starts, in seconds from the start of the song. Agreed default: 0.
    public var snippetStart: Double

    public init(tiers: [Double] = [5, 10, 15], setSize: Int = 20, snippetStart: Double = 0) {
        self.tiers = tiers
        self.setSize = setSize
        self.snippetStart = snippetStart
    }
}
