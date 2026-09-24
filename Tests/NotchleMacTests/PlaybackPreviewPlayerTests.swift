import Foundation
import Testing
@testable import NotchleCore
@testable import NotchleMac

/// `PreviewPlayer` with a real AVPlayer on a generated local WAV clip (offline, silent at volume 0).
@MainActor
@Suite(.serialized) struct PlaybackPreviewPlayerTests {
    typealias S = PlaybackTestSupport

    /// A mono 16-bit 8 kHz WAV of a quiet tone, `seconds` long, written once per test run.
    static func wavClip(seconds: Double) throws -> URL {
        let url = FileManager.default.temporaryDirectory
            .appendingPathComponent("notchle-playback-\(Int(seconds * 1000))ms-\(ProcessInfo.processInfo.processIdentifier).wav")
        if FileManager.default.fileExists(atPath: url.path) { return url }
        let rate = 8000
        let samples = Int(Double(rate) * seconds)
        var data = Data()
        func append<T: FixedWidthInteger>(_ value: T) { withUnsafeBytes(of: value.littleEndian) { data.append(contentsOf: $0) } }
        data.append(contentsOf: Array("RIFF".utf8)); append(UInt32(36 + samples * 2))
        data.append(contentsOf: Array("WAVE".utf8))
        data.append(contentsOf: Array("fmt ".utf8)); append(UInt32(16)); append(UInt16(1)); append(UInt16(1))
        append(UInt32(rate)); append(UInt32(rate * 2)); append(UInt16(2)); append(UInt16(16))
        data.append(contentsOf: Array("data".utf8)); append(UInt32(samples * 2))
        for n in 0..<samples {
            append(Int16(sin(Double(n) * 2 * .pi * 440 / Double(rate)) * 1000))
        }
        try data.write(to: url)
        return url
    }

    static func track(_ url: URL?) -> Track {
        Track(id: "t", uri: "spotify:track:t", title: "T", artists: ["A"], durationMs: 30_000, previewURL: url)
    }

    @Test func noPreviewURLIsNoPreview() async {
        await #expect(throws: PlayerError.noPreview) {
            try await PreviewPlayer(volume: 0).playSnippet(of: Self.track(nil), from: 0, seconds: 1)
        }
    }

    @Test func clampsTheStartIntoTheClip() {
        #expect(PreviewPlayer.clampedStart(0, seconds: 5, clipLength: 30) == 0)
        #expect(PreviewPlayer.clampedStart(10, seconds: 5, clipLength: 30) == 10)
        #expect(PreviewPlayer.clampedStart(28, seconds: 5, clipLength: 30) == 25)
        #expect(PreviewPlayer.clampedStart(100, seconds: 15, clipLength: 30) == 15)
        #expect(PreviewPlayer.clampedStart(5, seconds: 40, clipLength: 30) == 0)
        #expect(PreviewPlayer.clampedStart(-3, seconds: 5, clipLength: 30) == 0)
        #expect(PreviewPlayer.clampedStart(7, seconds: 5, clipLength: nil) == 7)
        #expect(PreviewPlayer.clampedStart(.nan, seconds: 5, clipLength: 30) == 0)
    }

    @Test func playsTheRequestedLengthThenPauses() async throws {
        let player = PreviewPlayer(volume: 0)
        let started = ContinuousClock.now
        try await player.playSnippet(of: Self.track(try Self.wavClip(seconds: 8)), from: 1, seconds: 0.6)
        let wall = (ContinuousClock.now - started).seconds

        #expect(player.isPaused)
        let position = try #require(player.currentTime)
        let played = position - 1
        #expect(abs(played - 0.6) < 0.15, "played \(played)s of audio")
        #expect(wall >= 0.6 && wall < 1.5, "call took \(wall)s")
    }

    @Test func startIsPulledBackSoTheSnippetFits() async throws {
        let player = PreviewPlayer(volume: 0)
        try await player.playSnippet(of: Self.track(try Self.wavClip(seconds: 3)), from: 10, seconds: 0.3)
        let position = try #require(player.currentTime)
        // Clip is 3s: start clamps to 2.7, so it ends near 3.0.
        #expect(position > 2.8 && position <= 3.05, "ended at \(position)s")
    }

    @Test func cancellingMidSnippetPausesPromptly() async throws {
        let player = PreviewPlayer(volume: 0)
        let track = Self.track(try Self.wavClip(seconds: 8))
        let snippet = Task { try await player.playSnippet(of: track, from: 0, seconds: 5) }
        #expect(await S.waitUntil { player.isPlaying })
        try await Task.sleep(for: .milliseconds(300))

        let cancelledAt = ContinuousClock.now
        snippet.cancel()
        let result = await snippet.result
        let latency = (ContinuousClock.now - cancelledAt).seconds

        #expect(throws: CancellationError.self) { try result.get() }
        #expect(player.isPaused)
        #expect(latency < 0.2, "cancel took \(latency)s")
        let position = try #require(player.currentTime)
        #expect(position < 1.0, "kept playing to \(position)s")
    }

    @Test func continuePlayingAfterACancelledSnippetKeepsPlaying() async throws {
        let player = PreviewPlayer(volume: 0)
        let track = Self.track(try Self.wavClip(seconds: 8))
        let snippet = Task { try await player.playSnippet(of: track, from: 0, seconds: 5) }
        #expect(await S.waitUntil { player.isPlaying })

        snippet.cancel()
        try await player.continuePlaying()
        _ = await snippet.result
        try await Task.sleep(for: .milliseconds(100))
        #expect(player.isPlaying, "the cancelled snippet's cleanup paused the resumed song")
        await player.stop()
    }

    @Test func continuePlayingResumesAndStopClearsTheItem() async throws {
        let player = PreviewPlayer(volume: 0)
        try await player.playSnippet(of: Self.track(try Self.wavClip(seconds: 8)), from: 0, seconds: 0.2)
        #expect(player.isPaused)

        try await player.continuePlaying()
        #expect(await S.waitUntil { player.isPlaying })

        await player.stop()
        #expect(player.isPaused)
        #expect(player.currentTime == nil)
        await #expect(throws: PlayerError.failed("No preview clip loaded")) { try await player.continuePlaying() }
    }

    @Test func unplayableClipFails() async throws {
        let url = FileManager.default.temporaryDirectory.appendingPathComponent("notchle-not-audio.mp3")
        try Data("this is not audio".utf8).write(to: url)
        await #expect {
            try await PreviewPlayer(volume: 0).playSnippet(of: Self.track(url), from: 0, seconds: 1)
        } throws: { error in
            guard case .failed = error as? PlayerError else { return false }
            return true
        }
    }

    @Test func defaultInitIsUsableFromAnyContext() async {
        let player = await Task.detached { PreviewPlayer() }.value
        #expect(player.displayName == "30-second previews")
        #expect(!player.playsFullTrack)
    }
}
