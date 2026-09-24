import Foundation
#if canImport(FoundationNetworking)
import FoundationNetworking
#endif

// Ports: the seams where a platform plugs in. macOS adapters live in NotchleMac; a Windows
// build would provide its own `Player` (and could reuse `URLSessionHTTPClient` as-is).

/// Fetches the tracks behind a Spotify link.
public protocol TrackSource: Sendable {
    func listing(for ref: SourceRef) async throws -> SourceListing
}

public enum SourceError: Error, Sendable, Hashable {
    case invalidURL
    case notFound
    case network(String)
    case parseFailed(String)
    /// The page parsed but held no playable tracks.
    case empty
}

/// Minimal HTTP seam so sources are testable against fixtures and portable.
public protocol HTTPClient: Sendable {
    func get(_ url: URL, headers: [String: String]) async throws -> (status: Int, body: Data)
}

public struct URLSessionHTTPClient: HTTPClient {
    public init() {}

    public func get(_ url: URL, headers: [String: String]) async throws -> (status: Int, body: Data) {
        var request = URLRequest(url: url)
        for (name, value) in headers { request.setValue(value, forHTTPHeaderField: name) }
        let (data, response) = try await URLSession.shared.data(for: request)
        return ((response as? HTTPURLResponse)?.statusCode ?? 0, data)
    }
}

/// Plays audio for the game.
///
/// Contract every implementation must honour:
/// - `playSnippet` starts `track` at `start` seconds, returns once `seconds` of audio have
///   played and playback is paused. It must react to Task cancellation promptly (pause and
///   throw `CancellationError`) because the player may submit a guess mid-snippet.
/// - `continuePlaying` resumes the current track from wherever it is paused, to the end.
/// - `restartTrack` plays `track` from 0:00 and keeps playing (no pause). A snippet it
///   interrupts must not pause it afterwards.
/// - Nothing an implementation does may reveal the title or artist on screen.
public protocol Player: Sendable {
    /// Shown in settings, e.g. "Spotify app" or "30-second previews".
    var displayName: String { get }
    /// false for the preview player: "keeps playing" ends after the ~30s clip.
    var playsFullTrack: Bool { get }

    func playSnippet(of track: Track, from start: Double, seconds: Double) async throws
    func continuePlaying() async throws
    /// Plays `track` from 0:00 and keeps playing (no pause). Must not reveal the title on screen.
    func restartTrack(_ track: Track) async throws
    func stop() async
}

public enum PlayerError: Error, Sendable, Hashable {
    /// e.g. Spotify desktop app not installed.
    case unavailable(String)
    /// macOS Automation permission denied (AppleScript error -1743).
    case notAuthorized
    /// Preview player and the track has no preview URL.
    case noPreview
    case failed(String)
}

/// Settings the platform layer persists alongside progress.
public enum PlayerMode: String, Sendable, Codable, CaseIterable {
    case spotifyApp
    case preview
}

public struct AppSettings: Sendable, Hashable, Codable {
    public var playerMode: PlayerMode
    public var lastSource: SourceRef?
    public var config: GameConfig

    public init(playerMode: PlayerMode = .spotifyApp, lastSource: SourceRef? = nil, config: GameConfig = GameConfig()) {
        self.playerMode = playerMode
        self.lastSource = lastSource
        self.config = config
    }
}
