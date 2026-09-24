import Foundation
import Testing
import NotchleCore
@testable import Notchle

/// Records the order of player calls. A snippet "plays" until cancelled, then logs its pause.
@MainActor
final class RecordingPlayer: Player {
    nonisolated let displayName = "fake"
    nonisolated let playsFullTrack = true
    var log: [String] = []

    func playSnippet(of track: Track, from start: Double, seconds: Double) async throws {
        log.append("snippet:\(track.id)")
        do {
            try await Task.sleep(for: .seconds(seconds))
        } catch {
            // Simulate a real player taking a moment to pause after cancellation.
            try? await Task.sleep(for: .milliseconds(50))
            log.append("paused:\(track.id)")
            throw CancellationError()
        }
        log.append("finished:\(track.id)")
    }

    func continuePlaying() async throws { log.append("continue") }
    func stop() async { log.append("stop") }
}

struct NoSource: TrackSource {
    func listing(for ref: SourceRef) async throws -> SourceListing { throw SourceError.empty }
}

@MainActor
@Test func continueWaitsForCancelledSnippetToPause() async throws {
    let player = RecordingPlayer()
    let dir = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
    let coordinator = AppCoordinator(source: NoSource(), store: ProgressStore(directory: dir), makePlayer: { _ in player })
    let track = Track(id: "t1", uri: "spotify:track:t1", title: "x", artists: ["y"], durationMs: 1, previewURL: nil)

    coordinator.runForTesting(.playSnippet(track, start: 0, seconds: 30))
    try await Task.sleep(for: .milliseconds(20))
    coordinator.runForTesting(.continuePlaying)
    await coordinator.drainPlayback()

    #expect(player.log == ["snippet:t1", "paused:t1", "continue"])
}
