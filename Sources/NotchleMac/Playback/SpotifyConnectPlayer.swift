import Foundation
import NotchleCore

/// Full tracks through Spotify Connect: the Web API tells the Spotify desktop app on this Mac
/// what to play. Unlike AppleScript `play track`, which makes Spotify bring its window forward on
/// every track change (measured 2026-09-24, ~145 ms after the command), a Connect play request
/// is not expected to activate the app. That is unverified against the real service; as a
/// belt-and-braces measure the app is hidden after every play anyway.
///
/// Mirrors `SpotifyAppPlayer`: snippets are timed by Spotify's own `progress_ms`, cancellation
/// pauses promptly, and a generation guard keeps a cancelled snippet's late pause from stopping
/// a newer operation. The HTTP side (`SpotifyWebAPI`) is portable and lives in NotchleCore.
@MainActor
public final class SpotifyConnectPlayer: Player {
    nonisolated public let displayName = "Spotify (full songs, no window)"
    nonisolated public let playsFullTrack = true

    struct Timing: Sendable {
        /// How long playback gets to actually start moving.
        var startTimeout: Duration = .seconds(8)
        /// Every reading is an HTTP round trip that counts against the rate limit.
        var pollInterval: Duration = .milliseconds(250)
        /// Longest single sleep while a snippet plays.
        var maxSleepSlice: Double = 0.5
        /// A snippet fails if progress_ms doesn't move for this long.
        var stallTimeout: Duration = .seconds(4)
        /// Stop this many seconds early, to absorb the pause request's own latency.
        var endLead: Double = 0.1
        /// Playback counts as started once the position is this far past the start.
        var audibleProgress: Double = 0.05
        /// While starting: re-seek when the position is further than this from the start.
        var positionTolerance: Double = 1.0
        var reseekInterval: Duration = .milliseconds(750)
        /// Bound on the best-effort pause (seconds).
        var pauseTimeout: Double = 3
    }

    private let api: SpotifyWebAPI
    private let app: any SpotifyAppControlling
    private let machineName: String
    private let timing: Timing
    private var deviceID: String?
    /// Bumped by every public operation; see `SpotifyAppPlayer.generation`.
    private var generation = 0

    public nonisolated convenience init(api: SpotifyWebAPI) {
        self.init(api: api, app: WorkspaceSpotifyControl(),
                  machineName: Host.current().localizedName ?? "", timing: Timing())
    }

    nonisolated init(api: SpotifyWebAPI, app: any SpotifyAppControlling, machineName: String, timing: Timing) {
        self.api = api
        self.app = app
        self.machineName = machineName
        self.timing = timing
    }

    public func playSnippet(of track: Track, from start: Double, seconds: Double) async throws {
        generation += 1
        let myGeneration = generation
        do {
            try Task.checkCancellation()
            let device = try await localDevice()
            let startAt = Self.clampedStart(start, seconds: seconds, durationMs: track.durationMs)
            let startMs = Int((startAt * 1000).rounded())
            try await api.play(track.uri, positionMs: startMs, on: device)
            app.hide()
            try await waitUntilAudioAdvances(track, from: startAt, startMs: startMs, device: device)
            app.hide()
            try await waitUntilPlayed(track, until: startAt + max(0, seconds))
            await pause(ifStill: myGeneration)
        } catch {
            // Cancellation (a guess mid-snippet) or a failure: never leave Spotify playing
            // something the game didn't ask for.
            await pause(ifStill: myGeneration)
            throw error
        }
    }

    public func continuePlaying() async throws {
        generation += 1
        try await api.resume(on: deviceID)
    }

    /// Device lookup/transfer as for a snippet, then play from 0 and leave it playing. Bumping
    /// the generation first means a snippet this interrupts can't pause it.
    public func restartTrack(_ track: Track) async throws {
        generation += 1
        try Task.checkCancellation()
        let device = try await localDevice()
        try await api.play(track.uri, positionMs: 0, on: device)
        app.hide()
    }

    public func stop() async {
        generation += 1
        await pauseQuietly()
    }

    // MARK: - Internals

    /// Where the snippet starts: `start`, pulled back so `seconds` fit in the track, never < 0.
    nonisolated static func clampedStart(_ start: Double, seconds: Double, durationMs: Int) -> Double {
        let requested = start.isFinite ? max(0, start) : 0
        guard durationMs > 0 else { return requested }
        return min(requested, max(0, Double(durationMs) / 1000 - max(0, seconds)))
    }

    /// Finds this Mac's Spotify app and makes it the active device.
    private func localDevice() async throws -> String {
        let devices = try await api.devices()
        guard let device = SpotifyPlayerAPI.pickLocalDevice(devices, machineName: machineName), let id = device.id else {
            throw PlayerError.unavailable(SpotifyPlayerAPI.noDeviceMessage)
        }
        deviceID = id
        if !device.isActive { try await api.transfer(to: id) }
        return id
    }

    private func sample() async throws -> SpotifyPlayback {
        try await api.playback() ?? SpotifyPlayback(isPlaying: false, progressMs: 0, itemURI: nil)
    }

    /// Polls until Spotify plays `track` and progress_ms has moved past `start`: "is_playing"
    /// alone comes before any audio (both macOS players reported playing ~0.3 s early).
    private func waitUntilAudioAdvances(_ track: Track, from start: Double, startMs: Int, device: String) async throws {
        let deadline = ContinuousClock.now.advanced(by: timing.startTimeout)
        var lastSeek = ContinuousClock.now
        while true {
            let current = try await sample()
            if current.isAd && current.isPlaying { throw PlayerError.failed("Spotify is playing an ad") }
            let rightTrack = current.isPlaying(track.uri, title: track.title)
            if current.isPlaying && rightTrack {
                if abs(current.position - start) > timing.positionTolerance {
                    // Spotify ignored the position (sent before the track loaded): seek again,
                    // giving each seek time to land before judging it.
                    if ContinuousClock.now - lastSeek >= timing.reseekInterval {
                        lastSeek = .now
                        try await api.seek(positionMs: startMs, on: device)
                    }
                } else if current.position >= start + timing.audibleProgress {
                    return
                }
            }
            if ContinuousClock.now >= deadline {
                throw PlayerError.failed((current.isPlaying && !rightTrack
                    ? "Spotify played a different track" : "Spotify didn't start playing the track")
                    + " (wanted \(track.uri) on device \(device); Spotify reports \(current.summary))")
            }
            try await Task.sleep(for: timing.pollInterval)
        }
    }

    /// Sleeps (cancellably) until progress_ms reaches `target`, so the snippet is that much audio
    /// rather than wall clock. Fails on a track change or a stall.
    private func waitUntilPlayed(_ track: Track, until target: Double) async throws {
        var lastPosition = -Double.infinity
        var lastProgress = ContinuousClock.now
        while true {
            let current = try await sample()
            if current.isAd && current.isPlaying { throw PlayerError.failed("Spotify is playing an ad") }
            guard current.isPlaying(track.uri, title: track.title) else { throw PlayerError.failed("Spotify played a different track (\(current.summary))") }
            let remaining = target - current.position
            if remaining <= timing.endLead + 0.0005 { return }
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
        await pauseQuietly()
    }

    /// Best effort. Runs in its own Task so a cancelled snippet can still send it (a cancelled
    /// URLSession request would fail at once), bounded by `pauseTimeout`.
    private func pauseQuietly() async {
        guard let deviceID else { return }
        let api = self.api
        let timeout = timing.pauseTimeout
        await Task { try? await api.pause(on: deviceID, timeout: timeout) }.value
    }
}
