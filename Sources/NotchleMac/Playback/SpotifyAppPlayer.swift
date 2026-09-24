import Foundation
import NotchleCore

// STUB — Wave 2, agent B replaces this file.
/// Drives the Spotify desktop app through AppleScript. Plays full tracks.
public final class SpotifyAppPlayer: Player {
    public let displayName = "Spotify app"
    public let playsFullTrack = true

    public init() {}

    public func playSnippet(of track: Track, from start: Double, seconds: Double) async throws {
        throw PlayerError.failed("not implemented")
    }

    public func continuePlaying() async throws {}
    public func stop() async {}
}
