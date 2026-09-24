import Foundation
import NotchleCore

/// Full songs through Music.app (AppleScript), from the user's own library. EXPERIMENTAL: Music
/// is launched hidden and re-hidden after every play, but whether `play` ever brings its window
/// forward is not yet measured; run `Notchle --probe-music-window` (see `MusicWindowProbe`).
///
/// Works for any listing (Apple Music or Spotify links): each track is looked up in the library
/// by title + artist + duration (`AppleMusicMatch`). A song that isn't in the library fails with
/// a message saying so; Music's dictionary has no way to play a catalog song by id.
///
/// Same contract details as `SpotifyAppPlayer`: timed by Music's own `player position`,
/// cancellation pauses promptly, and a generation guard keeps a cancelled snippet's late pause
/// from stopping a newer continue/restart.
@MainActor
public final class AppleMusicPlayer: Player {
    nonisolated public let displayName = "Apple Music (library)"
    nonisolated public let playsFullTrack = true

    struct Timing: Sendable {
        var launchTimeout: Duration = .seconds(15)
        var pollInterval: Duration = .milliseconds(50)
        var confirmTimeout: Duration = .seconds(6)
        var positionTolerance: Double = 1.0
        var audibleProgress: Double = 0.05
        var reseekInterval: Duration = .milliseconds(250)
        var endLead: Double = 0.02
        var maxSleepSlice: Double = 0.5
        var stallTimeout: Duration = .seconds(3)
    }

    private let runner: any AppleScriptRunning
    private let app: any SpotifyAppControlling
    private let timing: Timing
    private var generation = 0
    /// Track id → library database ID, so a retry doesn't query the library again.
    private var resolved: [String: Int] = [:]

    public nonisolated convenience init() {
        self.init(runner: NSAppleScriptRunner(), app: WorkspaceMusicControl(), timing: Timing())
    }

    nonisolated init(runner: any AppleScriptRunning, app: any SpotifyAppControlling, timing: Timing) {
        self.runner = runner
        self.app = app
        self.timing = timing
    }

    public func playSnippet(of track: Track, from start: Double, seconds: Double) async throws {
        generation += 1
        let myGeneration = generation
        do {
            let id = try await startPlaying(track, at: start)
            try await waitUntilPlayed(id, until: start + max(0, seconds))
            await pause(ifStill: myGeneration)
        } catch {
            await pause(ifStill: myGeneration)
            throw error
        }
    }

    public func continuePlaying() async throws {
        generation += 1
        guard app.isRunning else { throw PlayerError.failed("Music isn't running") }
        _ = try await run(AppleMusicScripts.resume)
    }

    public func restartTrack(_ track: Track) async throws {
        generation += 1
        _ = try await startPlaying(track, at: 0)
    }

    public func stop() async {
        generation += 1
        if app.isRunning { _ = try? await run(AppleMusicScripts.pause) }
    }

    // MARK: - Internals

    private func startPlaying(_ track: Track, at start: Double) async throws -> Int {
        try Task.checkCancellation()
        guard app.isInstalled else { throw PlayerError.unavailable("Music isn't installed") }
        if !app.isRunning { try await launchAndWait() }
        try Task.checkCancellation()
        let id = try await databaseID(for: track)
        try Task.checkCancellation()
        _ = try await run(AppleMusicScripts.play(databaseID: id))
        app.hide()
        _ = try await run(AppleMusicScripts.setPosition(start))
        try await waitUntilPlaying(id, from: start)
        app.hide()
        return id
    }

    private func databaseID(for track: Track) async throws -> Int {
        if let hit = resolved[track.id] { return hit }
        let output = try await run(AppleMusicScripts.candidates(containing: AppleMusicMatch.searchTerm(for: track.title)))
        guard let song = AppleMusicMatch.best(LibrarySong.parse(output), for: track) else {
            throw PlayerError.failed("This song isn't in your Music library. Add the playlist to your library in Music, or skip it")
        }
        resolved[track.id] = song.databaseID
        return song.databaseID
    }

    private func launchAndWait() async throws {
        try await app.launchHidden()
        let deadline = ContinuousClock.now.advanced(by: timing.launchTimeout)
        while true {
            do {
                _ = try await runner.run(AppleMusicScripts.status)
                app.hide()
                return
            } catch let failure as AppleScriptFailure {
                if failure.kind == .notAuthorized { throw PlayerError.notAuthorized }
            }
            if ContinuousClock.now >= deadline { throw PlayerError.failed("Music didn't respond after launching") }
            try await Task.sleep(for: timing.pollInterval)
        }
    }

    private func run(_ source: String) async throws -> String {
        do {
            return try await runner.run(source)
        } catch let failure as AppleScriptFailure {
            switch failure.kind {
            case .notAuthorized: throw PlayerError.notAuthorized
            case .notRunning: throw PlayerError.failed("Music stopped responding (AppleScript error \(failure.number))")
            case .other: throw PlayerError.failed("Music AppleScript error \(failure.number): \(failure.message)")
            }
        }
    }

    private func status() async throws -> MusicStatus {
        let output = try await run(AppleMusicScripts.status)
        guard let s = MusicStatus(parsing: output) else { throw PlayerError.failed("Unexpected answer from Music: \(output)") }
        return s
    }

    private func waitUntilPlaying(_ id: Int, from start: Double) async throws {
        let deadline = ContinuousClock.now.advanced(by: timing.confirmTimeout)
        var lastSeek = ContinuousClock.now
        while true {
            let s = try await status()
            if s.isPlaying && s.databaseID == id {
                if abs(s.position - start) > timing.positionTolerance {
                    if ContinuousClock.now - lastSeek >= timing.reseekInterval {
                        lastSeek = .now
                        _ = try await run(AppleMusicScripts.setPosition(start))
                    }
                } else if s.position >= start + timing.audibleProgress {
                    return
                }
            }
            if ContinuousClock.now >= deadline {
                throw PlayerError.failed(s.isPlaying && s.databaseID != id
                    ? "Music played a different song" : "Music didn't start playing the song")
            }
            try await Task.sleep(for: timing.pollInterval)
        }
    }

    /// Sleeps until Music's own position reaches `target` (audio time, not wall clock).
    private func waitUntilPlayed(_ id: Int, until target: Double) async throws {
        var lastPosition = -Double.infinity
        var lastProgress = ContinuousClock.now
        while true {
            let s = try await status()
            guard s.databaseID == id else { throw PlayerError.failed("Music played a different song") }
            let remaining = target - s.position
            if remaining <= timing.endLead { return }
            if s.position > lastPosition {
                lastPosition = s.position
                lastProgress = .now
            } else if ContinuousClock.now - lastProgress > timing.stallTimeout {
                throw PlayerError.failed("Music stopped playing")
            }
            try await Task.sleep(for: .seconds(min(remaining - timing.endLead, timing.maxSleepSlice)))
        }
    }

    private func pause(ifStill myGeneration: Int) async {
        guard myGeneration == generation, app.isRunning else { return }
        _ = try? await run(AppleMusicScripts.pause)
    }
}
