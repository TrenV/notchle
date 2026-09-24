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
    func restartTrack(_ track: Track) async throws { log.append("restart:\(track.id)") }
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

@MainActor
@Test func restartTrackWaitsForCancelledSnippetAndNothingPausesAfterIt() async throws {
    let player = RecordingPlayer()
    let dir = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
    let coordinator = AppCoordinator(source: NoSource(), store: ProgressStore(directory: dir), makePlayer: { _ in player })
    let track = Track(id: "t1", uri: "spotify:track:t1", title: "x", artists: ["y"], durationMs: 1, previewURL: nil)

    coordinator.runForTesting(.playSnippet(track, start: 0, seconds: 30))
    try await Task.sleep(for: .milliseconds(20))
    coordinator.runForTesting(.restartTrack(track))
    await coordinator.drainPlayback()
    // The queue awaits the cancelled snippet's own pause before restarting, so the pause lands
    // first and the restarted song is the last thing the player hears about.
    #expect(player.log == ["snippet:t1", "paused:t1", "restart:t1"])

    try await Task.sleep(for: .milliseconds(150))
    #expect(player.log == ["snippet:t1", "paused:t1", "restart:t1"], "something ran after the restart")
}

struct OneTrackSource: TrackSource {
    static let track = Track(id: "t1", uri: "spotify:track:t1", title: "x", artists: ["y"], durationMs: 1, previewURL: nil)
    func listing(for ref: SourceRef) async throws -> SourceListing {
        SourceListing(ref: ref, name: "one", tracks: [Self.track])
    }
}

/// End to end through the engine: restart mid-snippet replays the same tier, and the snippet it
/// cancelled reports nothing (no premature `guessing`).
@MainActor
@Test func restartMidSnippetReplaysTheTierThroughTheEngine() async throws {
    let player = RecordingPlayer()
    let dir = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
    let coordinator = AppCoordinator(source: OneTrackSource(), store: ProgressStore(directory: dir), makePlayer: { _ in player })

    coordinator.send(.load(SourceRef(kind: .playlist, id: "p")))
    let deadline = ContinuousClock.now.advanced(by: .seconds(5))
    while !player.log.contains("snippet:t1"), ContinuousClock.now < deadline {
        try await Task.sleep(for: .milliseconds(5))
    }
    #expect(coordinator.model.state.phase == .playingSnippet(tierIndex: 0))

    coordinator.send(.restart)
    #expect(coordinator.model.state.phase == .playingSnippet(tierIndex: 0))
    try await Task.sleep(for: .milliseconds(200))
    #expect(player.log == ["stop", "snippet:t1", "paused:t1", "snippet:t1"])
    #expect(coordinator.model.state.phase == .playingSnippet(tierIndex: 0))
    #expect(coordinator.model.state.results.isEmpty)
    coordinator.send(.reset)
    await coordinator.drainPlayback()
}
