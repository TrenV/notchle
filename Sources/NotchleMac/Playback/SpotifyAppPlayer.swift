import Foundation
import NotchleCore

/// Drives the Spotify desktop app (`com.spotify.client`) through AppleScript. Plays full tracks.
///
/// Layers: `SpotifyScripts` (pure script text, status parsing, error mapping) → this class
/// (launch, retry, confirm, timing, cancellation; state lives on the main actor) →
/// `AppleScriptRunning` (the Apple Events, on their own serial queue; see `NSAppleScriptRunner`)
/// and `SpotifyAppControlling` (NSWorkspace launch/hide). Everything but the last two is unit
/// tested with fakes.
@MainActor
public final class SpotifyAppPlayer: Player {
    nonisolated public let displayName = "Spotify app"
    nonisolated public let playsFullTrack = true

    struct Timing: Sendable {
        /// How long a freshly launched Spotify gets to answer the status script.
        var launchTimeout: Duration = .seconds(10)
        /// Poll interval while waiting for a launched Spotify to answer.
        var launchPollInterval: Duration = .milliseconds(250)
        /// How long `play track` gets to show up as "playing that track".
        var confirmTimeout: Duration = .seconds(5)
        var confirmPollInterval: Duration = .milliseconds(50)
        /// Re-seek after confirmation when the reported position is further than this from `start`
        /// (Spotify can ignore a position set before the track has loaded).
        var positionTolerance: Double = 1.0
        /// Playback counts as confirmed once the position is this far past `start`.
        var audibleProgress: Double = 0.05
        /// Minimum gap between corrective seeks while confirming.
        var reseekInterval: Duration = .milliseconds(250)
        /// The snippet ends this many seconds before the target position, to absorb the pause
        /// command's own latency.
        var endLead: Double = 0.02
        /// Longest single sleep while a snippet plays.
        var maxSleepSlice: Double = 0.5
        /// A snippet fails if Spotify's position doesn't move for this long.
        var stallTimeout: Duration = .seconds(3)
    }

    private let runner: any AppleScriptRunning
    private let app: any SpotifyAppControlling
    private let timing: Timing
    /// Bumped by every public operation. A snippet only pauses Spotify if no newer operation has
    /// started since, so a cancelled snippet's late cleanup can't pause the next snippet or the
    /// song that `continuePlaying` resumed or `restartTrack` restarted.
    private var generation = 0

    public nonisolated convenience init() {
        self.init(runner: NSAppleScriptRunner(), app: WorkspaceSpotifyControl(), timing: Timing())
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
            try Task.checkCancellation()
            try await ensureRunning()
            try Task.checkCancellation()
            _ = try await runScript(SpotifyScripts.play(uri: track.uri))
            app.hide()
            if start > 0 {
                _ = try await runScript(SpotifyScripts.setPosition(start))
            }
            try await waitUntilPlaying(track, from: start)
            app.hide()
            try await waitUntilPlayed(track, until: start + max(0, seconds))
            await pause(ifStill: myGeneration)
        } catch {
            // Cancellation (a guess mid-snippet), a wrong track or a failed confirmation: never
            // leave Spotify playing something the game didn't ask for.
            await pause(ifStill: myGeneration)
            throw error
        }
    }

    public func continuePlaying() async throws {
        generation += 1
        // Deliberately no relaunch: if Spotify quit there is nothing to continue.
        guard app.isRunning else { throw PlayerError.failed("Spotify isn't running") }
        _ = try await runScript(SpotifyScripts.resume, relaunchIfNeeded: false)
    }

    /// `play track` from 0:00, confirmed like a snippet's start, then left playing. Bumping the
    /// generation means a snippet this interrupts can't pause the restarted song.
    public func restartTrack(_ track: Track) async throws {
        generation += 1
        try Task.checkCancellation()
        try await ensureRunning()
        try Task.checkCancellation()
        _ = try await runScript(SpotifyScripts.play(uri: track.uri))
        app.hide()
        _ = try await runScript(SpotifyScripts.setPosition(0))
        try await waitUntilPlaying(track, from: 0)
        app.hide()
    }

    public func stop() async {
        generation += 1
        await pauseIfRunning()
    }

    // MARK: - Internals

    /// Installed? Running? If not running, launch hidden and wait until it answers.
    private func ensureRunning() async throws {
        guard app.isInstalled else { throw PlayerError.unavailable("Spotify isn't installed") }
        if !app.isRunning {
            try await launchAndWait()
        }
    }

    private func launchAndWait() async throws {
        try await app.launchHidden()
        let deadline = ContinuousClock.now.advanced(by: timing.launchTimeout)
        while true {
            do {
                _ = try await runner.run(SpotifyScripts.status)
                return
            } catch let failure as AppleScriptFailure {
                if failure.kind == .notAuthorized { throw PlayerError.notAuthorized }
                // Still starting up: -600/-609 or a not-yet-ready scripting interface. Keep asking.
            }
            if ContinuousClock.now >= deadline {
                throw PlayerError.failed("Spotify didn't respond within \(timing.launchTimeout) after launching")
            }
            try await Task.sleep(for: timing.launchPollInterval)
        }
    }

    /// Runs `source`; on "app not running" relaunches once and retries once.
    private func runScript(_ source: String, relaunchIfNeeded: Bool = true) async throws -> String {
        do {
            return try await runner.run(source)
        } catch let failure as AppleScriptFailure {
            guard failure.kind == .notRunning, relaunchIfNeeded else { throw failure.playerError }
            try Task.checkCancellation()
            guard app.isInstalled else { throw PlayerError.unavailable("Spotify isn't installed") }
            if !app.isRunning { try await launchAndWait() }
            do {
                return try await runner.run(source)
            } catch let retryFailure as AppleScriptFailure {
                throw retryFailure.playerError
            }
        }
    }

    private func status() async throws -> SpotifyStatus {
        let output = try await runScript(SpotifyScripts.status)
        guard let status = SpotifyStatus(parsing: output) else {
            throw PlayerError.failed("Unexpected answer from Spotify: \(output)")
        }
        return status
    }

    /// Polls until Spotify reports `track` playing *and* its position has moved past `start`.
    /// Observed live (Spotify 1.3.1.234): right after
    /// `play track` Spotify already reports "playing" with the new track id while the position
    /// sits at 0 for ~300 ms as the track loads, so "playing + id" alone would start the clock
    /// before any audio is heard and shorten the snippet.
    private func waitUntilPlaying(_ track: Track, from start: Double) async throws {
        let deadline = ContinuousClock.now.advanced(by: timing.confirmTimeout)
        var lastSeek = ContinuousClock.now
        while true {
            let current = try await status()
            if current.isPlaying && current.trackURI == track.uri {
                if abs(current.position - start) > timing.positionTolerance {
                    // Spotify ignored the seek (sent before the track had loaded): seek again,
                    // but give each seek time to land before judging it.
                    if ContinuousClock.now - lastSeek >= timing.reseekInterval {
                        lastSeek = .now
                        _ = try await runScript(SpotifyScripts.setPosition(start))
                    }
                } else if current.position >= start + timing.audibleProgress {
                    return
                }
            } else if current.isPlaying && current.isAd {
                throw PlayerError.failed("Spotify played a different track")
            }
            if ContinuousClock.now >= deadline {
                if current.isPlaying && current.trackURI != track.uri {
                    throw PlayerError.failed("Spotify played a different track")
                }
                throw PlayerError.failed("Spotify didn't start playing the track")
            }
            try await Task.sleep(for: timing.confirmPollInterval)
        }
    }

    /// Sleeps (cancellably) until Spotify's own position reaches `target`, so the snippet is
    /// `seconds` of audio rather than `seconds` of wall clock. Observed live: a wall-clock sleep
    /// started at confirmation gave 2.656 s of audio for a 3 s snippet, because the position still
    /// jitters around 0 while the track loads. Long sleeps are sliced so a stall is noticed; the
    /// final slice sleeps exactly the remainder, so the overshoot is one status round trip.
    private func waitUntilPlayed(_ track: Track, until target: Double) async throws {
        var lastPosition = -Double.infinity
        var lastProgress = ContinuousClock.now
        while true {
            let current = try await status()
            guard current.trackURI == track.uri else {
                throw PlayerError.failed("Spotify played a different track")
            }
            let remaining = target - current.position
            if remaining <= timing.endLead { return }
            if current.position > lastPosition {
                lastPosition = current.position
                lastProgress = .now
            } else if ContinuousClock.now - lastProgress > timing.stallTimeout {
                throw PlayerError.failed("Spotify stopped playing")
            }
            try await Task.sleep(for: .seconds(min(remaining - timing.endLead, timing.maxSleepSlice)))
        }
    }

    private func pause(ifStill myGeneration: Int) async {
        guard myGeneration == generation else { return }
        await pauseIfRunning()
    }

    /// Best effort, and never launches Spotify just to pause it (a `tell` would).
    private func pauseIfRunning() async {
        guard app.isRunning else { return }
        _ = try? await runScript(SpotifyScripts.pause, relaunchIfNeeded: false)
    }
}
