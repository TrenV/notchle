import AppKit
import Foundation
import Testing
@testable import NotchleCore
@testable import NotchleMac

// Live tests, skipped unless asked for:
//   NOTCHLE_LIVE=1 scripts/test.sh --filter PlaybackLive          (network: a real Spotify preview clip)
//   NOTCHLE_LIVE_SPOTIFY=1 scripts/test.sh --filter PlaybackLiveSpotify  (drives the Spotify app: shows
//       its window briefly, plays ~6.5 s of audio, takes over whatever Spotify is playing, leaves it
//       paused; macOS may ask for Automation permission for the calling terminal the first time)

private let liveNetwork = ProcessInfo.processInfo.environment["NOTCHLE_LIVE"] == "1"
private let liveSpotify = ProcessInfo.processInfo.environment["NOTCHLE_LIVE_SPOTIFY"] == "1"

@MainActor
@Suite(.serialized) struct PlaybackLivePreviewTests {
    /// A real `audioPreview` URL from Tests/NotchleCoreTests/Fixtures/embed-playlist-todays-top-hits.html.
    static let previewURL = URL(string: "https://p.scdn.co/mp3-preview/e57b7c5fcb52b8bb4c781cc1e2e5b25f8a6c6e79")!
    static let track = Track(id: "2FZcjBYK4dTt48q94pJbJD", uri: "spotify:track:2FZcjBYK4dTt48q94pJbJD",
                             title: "?", artists: ["?"], durationMs: 200_000, previewURL: previewURL)

    @Test(.enabled(if: liveNetwork, "set NOTCHLE_LIVE=1"))
    func twoSecondSnippetOfARealPreview() async throws {
        let player = PreviewPlayer(volume: 0)
        let start = 3.0
        let began = ContinuousClock.now
        try await player.playSnippet(of: Self.track, from: start, seconds: 2.0)
        let wall = (ContinuousClock.now - began).seconds
        let played = try #require(player.currentTime) - start
        print("LIVE preview: requested 2.000s, audio played \(String(format: "%.3f", played))s, call took \(String(format: "%.3f", wall))s (incl. load)")
        #expect(abs(played - 2.0) <= 0.3)
        #expect(player.isPaused)
    }

    @Test(.enabled(if: liveNetwork, "set NOTCHLE_LIVE=1"))
    func cancellingARealPreviewPausesWithin200ms() async throws {
        let player = PreviewPlayer(volume: 0)
        let snippet = Task { try await player.playSnippet(of: Self.track, from: 0, seconds: 10) }
        #expect(await PlaybackTestSupport.waitUntil(timeout: .seconds(10)) { player.isPlaying })
        try await Task.sleep(for: .seconds(1))

        let cancelledAt = ContinuousClock.now
        snippet.cancel()
        let result = await snippet.result
        let latency = (ContinuousClock.now - cancelledAt).seconds
        let positionAtCancel = player.currentTime ?? -1
        try await Task.sleep(for: .milliseconds(300))
        let positionLater = player.currentTime ?? -1
        print("LIVE preview cancel: latency \(String(format: "%.3f", latency * 1000))ms, position \(String(format: "%.3f", positionAtCancel))s then \(String(format: "%.3f", positionLater))s after 300ms")

        #expect(throws: CancellationError.self) { try result.get() }
        #expect(latency <= 0.2)
        #expect(player.isPaused)
        #expect(abs(positionLater - positionAtCancel) < 0.01, "audio kept advancing after cancel")
    }
}

/// Live smoke test of `SpotifyAppPlayer` against the real Spotify app.
///
/// It can't drive Spotify from inside this test process: observed 2026-09-24, in a process whose
/// entry point is Swift's async `main` (as Swift Testing's is) NSAppleScript sends the Apple
/// Event but Spotify's reply never arrives (blocked in `AEDefaultActiveProc`, ignoring
/// `with timeout`), on any thread. The same code in a process running `NSApplication.run()` (like
/// Notchle.app) or `dispatchMain()` answers in ~20-40 ms. So this test compiles the real
/// Contracts + Playback sources together with a small `NSApplication` host into a temporary
/// executable, runs it, and checks the numbers it prints.
@Suite(.serialized) struct PlaybackLiveSpotifyTests {
    static let root = URL(fileURLWithPath: #filePath).deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()

    static let hostMain = #"""
    import AppKit
    setvbuf(stdout, nil, _IONBF, 0)
    func status() async -> SpotifyStatus? {
        SpotifyStatus(parsing: (try? await NSAppleScriptRunner().run(SpotifyScripts.status)) ?? "")
    }
    func hidden() -> Bool? { NSRunningApplication.runningApplications(withBundleIdentifier: SpotifyScripts.bundleID).first?.isHidden }
    /// Spotify reports "paused" ~300 ms after the pause command (observed): wait for it.
    func settled() async -> SpotifyStatus? {
        let t0 = ContinuousClock.now
        var s = await status()
        while s?.state == .playing, ContinuousClock.now - t0 < .seconds(2) {
            try? await Task.sleep(for: .milliseconds(50)); s = await status()
        }
        return s
    }
    @MainActor func live() async {
        let track = Track(id: "2FZcjBYK4dTt48q94pJbJD", uri: "spotify:track:2FZcjBYK4dTt48q94pJbJD", title: "?", artists: ["?"], durationMs: 200_000, previewURL: nil)
        let player = SpotifyAppPlayer()
        // Control for hide(): make Spotify visible first, so hidden=true afterwards is hide()'s doing.
        NSRunningApplication.runningApplications(withBundleIdentifier: SpotifyScripts.bundleID).forEach { _ = $0.unhide() }
        try? await Task.sleep(for: .milliseconds(500))
        print("UNHIDDEN hidden=\(hidden().map(String.init) ?? "nil")")
        for (start, secs) in [(0.0, 3.0), (30.0, 2.0)] {
            do {
                try await player.playSnippet(of: track, from: start, seconds: secs)
                let s = await settled()
                print("SNIPPET start=\(start) requested=\(secs) played=\((s?.position ?? -99) - start) state=\(s.map { "\($0.state)" } ?? "nil") match=\(s?.trackURI == track.uri) hidden=\(hidden().map(String.init) ?? "nil")")
            } catch { print("SNIPPET start=\(start) error=\(error)") }
        }
        let snippet = Task { try await player.playSnippet(of: track, from: 60, seconds: 3) }
        try? await Task.sleep(for: .milliseconds(1500))
        let c0 = ContinuousClock.now
        snippet.cancel()
        let result = await snippet.result
        let ms = (ContinuousClock.now - c0).components.attoseconds / 1_000_000_000_000_000 + (ContinuousClock.now - c0).components.seconds * 1000
        let s = await settled()
        var cancelled = false
        if case .failure(let e) = result, e is CancellationError { cancelled = true }
        print("CANCEL latency_ms=\(ms) cancellation=\(cancelled) state=\(s.map { "\($0.state)" } ?? "nil") position=\(s?.position ?? -1)")
        await player.stop()
    }
    Task { @MainActor in await live(); exit(0) }
    NSApplication.shared.setActivationPolicy(.prohibited)
    NSApplication.shared.run()
    """#

    /// Runs `executable` with `arguments`, returning stdout; kills it after `timeout` seconds.
    static func run(_ executable: String, _ arguments: [String], timeout: Double) throws -> (status: Int32, output: String) {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: executable)
        process.arguments = arguments
        let pipe = Pipe()
        process.standardOutput = pipe
        process.standardError = pipe
        try process.run()
        let deadline = Date().addingTimeInterval(timeout)
        while process.isRunning && Date() < deadline { Thread.sleep(forTimeInterval: 0.1) }
        if process.isRunning { process.terminate() }
        process.waitUntilExit()
        let output = String(decoding: pipe.fileHandleForReading.readDataToEndOfFile(), as: UTF8.self)
        return (process.terminationStatus, output)
    }

    static func fields(_ line: Substring) -> [String: String] {
        Dictionary(line.split(separator: " ").dropFirst().compactMap { pair -> (String, String)? in
            let kv = pair.split(separator: "=", maxSplits: 1)
            return kv.count == 2 ? (String(kv[0]), String(kv[1])) : nil
        }, uniquingKeysWith: { $1 })
    }

    /// ~6.5 s of audio in snippets of at most 3 s; leaves Spotify paused.
    @Test(.enabled(if: liveSpotify, "set NOTCHLE_LIVE_SPOTIFY=1 (plays audio through the Spotify app)"),
          .timeLimit(.minutes(2)))
    func snippetsAndCancelThroughTheSpotifyApp() throws {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent("notchle-live-spotify-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: dir) }
        var sources: [String] = []
        for folder in ["Sources/NotchleCore/Contracts", "Sources/NotchleMac/Playback"] {
            let url = Self.root.appendingPathComponent(folder)
            for name in try FileManager.default.contentsOfDirectory(atPath: url.path) where name.hasSuffix(".swift") {
                let text = try String(contentsOf: url.appendingPathComponent(name), encoding: .utf8)
                    .replacingOccurrences(of: "import NotchleCore\n", with: "")
                let out = dir.appendingPathComponent(name)
                try text.write(to: out, atomically: true, encoding: .utf8)
                sources.append(out.path)
            }
        }
        let main = dir.appendingPathComponent("main.swift")
        try Self.hostMain.write(to: main, atomically: true, encoding: .utf8)
        let binary = dir.appendingPathComponent("notchle-live-spotify").path
        let build = try Self.run("/usr/bin/swiftc", ["-swift-version", "6", "-O"] + sources + [main.path, "-o", binary], timeout: 120)
        try #require(build.status == 0, "host build failed:\n\(build.output)")

        let live = try Self.run(binary, [], timeout: 45)
        print("LIVE spotify host output:\n\(live.output)")
        try #require(live.status == 0, "host didn't finish (timed out waiting on Spotify or an Automation prompt?)")
        let lines = live.output.split(separator: "\n")

        #expect(lines.contains { $0 == "UNHIDDEN hidden=false" }, "control: Spotify should be visible before the first snippet")
        let snippets = lines.filter { $0.hasPrefix("SNIPPET ") }.map(Self.fields)
        #expect(snippets.count == 2)
        for snippet in snippets {
            let requested = Double(snippet["requested"] ?? "") ?? -1
            let played = Double(snippet["played"] ?? "") ?? -99
            #expect(abs(played - requested) <= 0.3, "snippet \(snippet)")
            #expect(snippet["state"] == "paused" && snippet["match"] == "true" && snippet["hidden"] == "true", "snippet \(snippet)")
        }
        let cancel = try #require(lines.first { $0.hasPrefix("CANCEL ") }.map(Self.fields))
        #expect(cancel["cancellation"] == "true" && cancel["state"] == "paused")
        #expect((Int(cancel["latency_ms"] ?? "") ?? 9999) <= 200)
    }
}
