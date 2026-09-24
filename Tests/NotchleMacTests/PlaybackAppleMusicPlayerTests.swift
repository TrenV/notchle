import Foundation
import os
import Testing
@testable import NotchleCore
@testable import NotchleMac

/// A simulated Music.app: one library song (database ID 42), position advancing in real time
/// once `play` has been sent. Records every script in order.
final class FakeMusic: AppleScriptRunning, Sendable {
    struct State: Sendable {
        var running = true
        var playing = false
        var clock: ContinuousClock.Instant?
        var base = 0.0
        var log: [(String, ContinuousClock.Instant)] = []
    }
    let state = OSAllocatedUnfairLock(initialState: State())
    let library: String
    /// Position stops advancing (stall).
    let frozen: Bool

    init(library: String = "42\tSo Good (feat. Kendrick Lamar)\tJhené Aiko & Kendrick Lamar\t237.25\n7\tSo Good\tSomeone Else\t200\n",
         running: Bool = true, frozen: Bool = false) {
        self.library = library
        self.frozen = frozen
        state.withLock { $0.running = running }
    }

    static func kind(_ s: String) -> String {
        if s == AppleMusicScripts.status { return "status" }
        if s == AppleMusicScripts.pause { return "pause" }
        if s == AppleMusicScripts.resume { return "resume" }
        if s.contains("whose name contains") { return "find" }
        if s.contains("whose database ID") { return "play" }
        if s.contains("set player position") { return "seek" }
        return "other"
    }

    var calls: [String] { state.withLock { $0.log.map { Self.kind($0.0) } } }
    var log: [(String, ContinuousClock.Instant)] { state.withLock { $0.log } }

    func run(_ source: String) async throws -> String {
        let kind = Self.kind(source)
        return state.withLock { s in
            s.log.append((source, .now))
            switch kind {
            case "find": return library
            case "play": s.playing = true; s.clock = .now; s.base = 0
            case "seek":
                let v = Double(source.components(separatedBy: "position to ")[1].prefix { $0 == "." || $0.isNumber }) ?? 0
                s.base = v; s.clock = .now
            case "pause": s.playing = false
            case "resume": s.playing = true
            case "status":
                let pos = frozen ? s.base : s.base + (s.clock.map { (ContinuousClock.now - $0).seconds } ?? 0)
                return "\(s.playing ? "playing" : "paused")\n\(s.clock == nil ? "" : "42")\n\(pos)"
            default: break
            }
            return ""
        }
    }
}

@MainActor
final class FakeMusicApp: SpotifyAppControlling {
    let music: FakeMusic
    var launches = 0
    var hides = 0
    nonisolated init(_ music: FakeMusic) { self.music = music }
    var isInstalled: Bool { true }
    var isRunning: Bool { music.state.withLock { $0.running } }
    func launchHidden() async throws { launches += 1; music.state.withLock { $0.running = true } }
    func hide() { hides += 1 }
}

extension AppleMusicPlayer.Timing {
    static let fast = AppleMusicPlayer.Timing(
        launchTimeout: .seconds(10), pollInterval: .milliseconds(5), confirmTimeout: .seconds(2),
        positionTolerance: 1, audibleProgress: 0.05, reseekInterval: .zero, endLead: 0,
        maxSleepSlice: 0.05, stallTimeout: .milliseconds(300))
}

@MainActor
@Suite struct PlaybackAppleMusicPlayerTests {
    let track = Track(id: "am.6810716062", uri: "applemusic:song:6810716062", title: "So Good (feat. Kendrick Lamar)",
                      artists: ["Jhené Aiko"], durationMs: 237_251, previewURL: nil)

    func make(_ music: FakeMusic) -> (AppleMusicPlayer, FakeMusicApp) {
        let app = FakeMusicApp(music)
        return (AppleMusicPlayer(runner: music, app: app, timing: .fast), app)
    }

    @Test func snippetFindsTheLibrarySongPlaysItAndPausesOnMusicsClock() async throws {
        let music = FakeMusic()
        let (player, app) = make(music)
        try await player.playSnippet(of: track, from: 0, seconds: 0.3)
        let calls = music.calls
        #expect(Array(calls.prefix(3)) == ["find", "play", "seek"])
        #expect(calls.last == "pause")
        #expect(music.log[1].0 == AppleMusicScripts.play(databaseID: 42))
        let played = (music.log.last!.1 - music.log[1].1).seconds
        #expect(played >= 0.28 && played < 0.5, "paused \(played)s after play")
        #expect(app.hides >= 2)
    }

    @Test func launchesMusicHiddenWhenNotRunning() async throws {
        let music = FakeMusic(running: false)
        let (player, app) = make(music)
        try await player.playSnippet(of: track, from: 0, seconds: 0.05)
        #expect(app.launches == 1)
    }

    @Test func aSongNotInTheLibraryFailsWithoutPlaying() async {
        let music = FakeMusic(library: "7\tSo Good\tSomeone Else\t200\n")
        await #expect(throws: PlayerError.failed("This song isn't in your Music library. Add the playlist to your library in Music, or skip it")) {
            try await make(music).0.playSnippet(of: track, from: 0, seconds: 1)
        }
        #expect(!music.calls.contains("play"))
    }

    @Test func aStallFailsAndPauses() async {
        let music = FakeMusic(frozen: true)
        await #expect(throws: PlayerError.self) {
            try await make(music).0.playSnippet(of: track, from: 0, seconds: 5)
        }
        #expect(music.calls.last == "pause")
    }

    @Test func cancellationPausesPromptly() async throws {
        let music = FakeMusic()
        let (player, _) = make(music)
        let task = Task { try await player.playSnippet(of: track, from: 0, seconds: 30) }
        #expect(await PlaybackTestSupport.waitUntil { music.calls.contains("seek") })
        try await Task.sleep(for: .milliseconds(100))
        let cancelledAt = ContinuousClock.now
        task.cancel()
        await #expect(throws: CancellationError.self) { try await task.value }
        #expect(music.calls.last == "pause")
        #expect((music.log.last!.1 - cancelledAt).seconds < 0.3)
    }

    @Test func aCancelledSnippetNeverPausesALaterContinue() async throws {
        let music = FakeMusic()
        let (player, _) = make(music)
        let task = Task { try await player.playSnippet(of: track, from: 0, seconds: 30) }
        #expect(await PlaybackTestSupport.waitUntil { music.calls.contains("seek") })
        task.cancel()
        try await player.continuePlaying()   // runs before the snippet's task winds down
        _ = try? await task.value
        // The bug this guards: the cancelled snippet pausing after the resume. A read-only status
        // poll already in flight may still land after it (seen on the CI Mac runner).
        let afterResume = Array(music.calls.drop { $0 != "resume" })
        #expect(afterResume.first == "resume", "calls: \(music.calls)")
        #expect(!afterResume.contains("pause"), "calls: \(music.calls)")
    }

    @Test func restartPlaysFromZeroAndKeepsPlaying() async throws {
        let music = FakeMusic()
        let (player, _) = make(music)
        try await player.restartTrack(track)
        #expect(music.calls.contains("play") && !music.calls.contains("pause"))
        #expect(music.log.first { FakeMusic.kind($0.0) == "seek" }?.0 == AppleMusicScripts.setPosition(0))
    }

    // MARK: - Pure parts

    @Test func matchingPrefersTitleArtistAndDuration() {
        let songs = LibrarySong.parse("1\tSo Good\tJhené Aiko\t300\n2\tSo Good (feat. Kendrick Lamar)\tJhene Aiko\t237\n3\tSo Good\tOther\t237\nbad line\n")
        #expect(songs.count == 3)
        #expect(AppleMusicMatch.best(songs, for: track)?.databaseID == 2)
        let spotifyStyle = Track(id: "x", uri: "spotify:track:x", title: "So Good - Remastered 2011", artists: ["Jhené Aiko", "Kendrick Lamar"], durationMs: 237_000, previewURL: nil)
        #expect(AppleMusicMatch.best(songs, for: spotifyStyle)?.databaseID == 2)
        #expect(AppleMusicMatch.best(songs, for: Track(id: "y", uri: "", title: "Nope", artists: ["A"], durationMs: 0, previewURL: nil)) == nil)
    }

    @Test func searchTermDropsBracketsAndSuffixes() {
        #expect(AppleMusicMatch.searchTerm(for: "So Good (feat. Kendrick Lamar)") == "So Good")
        #expect(AppleMusicMatch.searchTerm(for: "Let It Be - Remastered 2009") == "Let It Be")
        #expect(AppleMusicMatch.searchTerm(for: "(Intro)") == "(Intro)")
    }

    @Test func statusParsesDutchDecimals() {
        let s = MusicStatus(parsing: "playing\n42\n12,5")
        #expect(s?.isPlaying == true && s?.databaseID == 42 && s?.position == 12.5)
        #expect(MusicStatus(parsing: "stopped\n\n0")?.databaseID == nil)
        #expect(MusicStatus(parsing: "garbage") == nil)
    }

    @Test func titleCannotBreakOutOfTheScriptString() {
        let script = AppleMusicScripts.candidates(containing: #"a" & (do shell script "x") & ""#)
        #expect(script.contains(#""a\" & (do shell script \"x\") & \"""#))
    }

    /// Compiles (never runs, sends no Apple Event, doesn't launch Music) every script against
    /// Music's scripting dictionary.
    @Test func scriptsCompileAgainstMusicsDictionary() async throws {
        let runner = NSAppleScriptRunner()
        for script in [AppleMusicScripts.status, AppleMusicScripts.candidates(containing: "x"),
                       AppleMusicScripts.play(databaseID: 1), AppleMusicScripts.setPosition(2),
                       AppleMusicScripts.pause, AppleMusicScripts.resume] {
            try await runner.compile(script)
        }
        await #expect(throws: AppleScriptFailure.self) {
            try await runner.compile(#"tell application id "com.apple.Music" to set x to database shmid of current track"#)
        }
    }
}
