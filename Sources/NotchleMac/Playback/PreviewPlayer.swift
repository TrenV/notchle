import AVFoundation
import Foundation
import NotchleCore

/// Plays the ~30s preview clip (`Track.previewURL`) with AVPlayer. No Spotify app needed.
///
/// `AVPlayer` isn't Sendable, so the player and all its state live on the main actor; the
/// class is `@MainActor` and therefore Sendable, and its async methods satisfy `Player` by
/// hopping to the main actor.
@MainActor
public final class PreviewPlayer: Player {
    nonisolated public let displayName = "30-second previews"
    nonisolated public let playsFullTrack = false

    struct Timing: Sendable {
        /// How long AVPlayer gets to load and start the clip.
        var startTimeout: Duration = .seconds(5)
        var pollInterval: Duration = .milliseconds(10)
        /// Longest single sleep while a snippet plays (bounds the overshoot of the media clock).
        var maxSleepSlice: Double = 0.02
        /// A snippet fails if the media clock doesn't move for this long (network stall).
        var stallTimeout: Duration = .seconds(5)
    }

    private let volume: Float
    private let timing: Timing
    private var player: AVPlayer?
    /// Bumped by every public operation, so a cancelled snippet's late cleanup can't pause a
    /// newer snippet or the song `continuePlaying` just resumed.
    private var generation = 0

    /// - Parameter volume: 0...1, applied to the AVPlayer. Tests pass 0 to run silently.
    public nonisolated init(volume: Float = 1) {
        self.volume = min(1, max(0, volume))
        self.timing = Timing()
    }

    nonisolated init(volume: Float, timing: Timing) {
        self.volume = min(1, max(0, volume))
        self.timing = timing
    }

    public func playSnippet(of track: Track, from start: Double, seconds: Double) async throws {
        guard let url = track.previewURL else { throw PlayerError.noPreview }
        generation += 1
        let myGeneration = generation
        let player = avPlayer()
        do {
            try Task.checkCancellation()
            player.pause()
            let item = AVPlayerItem(url: url)
            player.replaceCurrentItem(with: item)
            let clipLength = try await Self.duration(of: item.asset)
            try Task.checkCancellation()
            let startAt = Self.clampedStart(start, seconds: seconds, clipLength: clipLength)
            let reached = await player.seek(
                to: CMTime(seconds: startAt, preferredTimescale: 600),
                toleranceBefore: .zero, toleranceAfter: .zero)
            try Task.checkCancellation()
            guard reached else {
                throw PlayerError.failed("Couldn't seek the preview clip")
            }
            player.play()
            try await waitUntilAudioAdvances(player, item: item, from: startAt)
            try await waitUntilPlayed(player, until: startAt + max(0, seconds))
            pause(ifStill: myGeneration)
        } catch {
            pause(ifStill: myGeneration)
            throw error
        }
    }

    public func continuePlaying() async throws {
        generation += 1
        guard let player, player.currentItem != nil else {
            throw PlayerError.failed("No preview clip loaded")
        }
        player.play()
    }

    public func stop() async {
        generation += 1
        player?.pause()
        player?.replaceCurrentItem(with: nil)
    }

    // MARK: - Test observation

    /// Seconds into the current clip (nil without a clip). Lets tests measure audio actually played.
    var currentTime: Double? {
        guard let player, player.currentItem != nil else { return nil }
        let seconds = player.currentTime().seconds
        return seconds.isFinite ? seconds : nil
    }

    var isPlaying: Bool { player?.timeControlStatus == .playing }
    var isPaused: Bool { player.map { $0.timeControlStatus == .paused && $0.rate == 0 } ?? true }

    // MARK: - Internals

    private func avPlayer() -> AVPlayer {
        if let player { return player }
        let created = AVPlayer()
        created.volume = volume
        // A snippet must start now, not after AVPlayer decides it has buffered "enough".
        created.automaticallyWaitsToMinimizeStalling = false
        created.actionAtItemEnd = .pause
        player = created
        return created
    }

    /// Where the snippet starts: `start`, pulled back so `seconds` fit in the clip if possible,
    /// and never before 0 or past the end.
    nonisolated static func clampedStart(_ start: Double, seconds: Double, clipLength: Double?) -> Double {
        let requested = start.isFinite ? max(0, start) : 0
        guard let clipLength, clipLength.isFinite, clipLength > 0 else { return requested }
        return min(requested, max(0, clipLength - max(0, seconds)))
    }

    private static func duration(of asset: AVAsset) async throws -> Double? {
        do {
            let duration = try await asset.load(.duration)
            return duration.isNumeric ? duration.seconds : nil
        } catch {
            throw PlayerError.failed("Couldn't load the preview clip: \(error.localizedDescription)")
        }
    }

    /// Waits until the clip's media time moves past `startAt`. `timeControlStatus == .playing`
    /// alone is not enough: measured locally it turns `.playing` ~250 ms before the audio clock
    /// starts, which cut a 0.6 s snippet to ~0.35 s of audio.
    private func waitUntilAudioAdvances(_ player: AVPlayer, item: AVPlayerItem, from startAt: Double) async throws {
        let deadline = ContinuousClock.now.advanced(by: timing.startTimeout)
        while !(player.timeControlStatus == .playing && player.currentTime().seconds > startAt + 0.001) {
            if item.status == .failed {
                throw PlayerError.failed("Preview clip failed: \(item.error?.localizedDescription ?? "unknown error")")
            }
            if ContinuousClock.now >= deadline {
                throw PlayerError.failed("Preview clip didn't start within \(timing.startTimeout)")
            }
            try await Task.sleep(for: timing.pollInterval)
        }
    }

    /// Sleeps (cancellably) until the media clock reaches `target`, so the snippet is `seconds`
    /// of audio even if playback stalls briefly. Ends early if the clip itself ends; fails if
    /// the clock stops advancing for `stallTimeout`.
    private func waitUntilPlayed(_ player: AVPlayer, until target: Double) async throws {
        var lastTime = player.currentTime().seconds
        var lastProgress = ContinuousClock.now
        while true {
            let now = player.currentTime().seconds
            let remaining = target - now
            if remaining <= 0 { return }
            if player.timeControlStatus == .paused { return } // reached the end of the clip
            if now > lastTime {
                lastTime = now
                lastProgress = .now
            } else if ContinuousClock.now - lastProgress > timing.stallTimeout {
                throw PlayerError.failed("Preview clip stalled")
            }
            // Short sleeps near the end keep the overshoot to a few milliseconds.
            try await Task.sleep(for: .seconds(min(remaining, timing.maxSleepSlice)))
        }
    }

    private func pause(ifStill myGeneration: Int) {
        guard myGeneration == generation else { return }
        player?.pause()
    }
}
