import Foundation
import NotchleCore

// STUB — Wave 2, agent B replaces this file.
/// Plays the ~30s preview clip with AVPlayer. No Spotify app needed.
public final class PreviewPlayer: Player {
    public let displayName = "30-second previews"
    public let playsFullTrack = false

    public init() {}

    public func playSnippet(of track: Track, from start: Double, seconds: Double) async throws {
        throw PlayerError.failed("not implemented")
    }

    public func continuePlaying() async throws {}
    public func stop() async {}
}
