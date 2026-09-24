import Foundation
import Testing
@testable import NotchleCore
@testable import NotchleMac

/// `SpotifyAppPlayer` against a simulated Spotify (`FakeSpotify`): everything except the real
/// Apple Events and NSWorkspace calls.
@MainActor
@Suite struct PlaybackSpotifyAppPlayerTests {
    typealias S = PlaybackTestSupport
    let track = PlaybackTestSupport.track

    func makePlayer(_ spotify: FakeSpotify, timing: SpotifyAppPlayer.Timing = .fast) -> SpotifyAppPlayer {
        SpotifyAppPlayer(runner: FakeRunner(spotify: spotify), app: FakeAppControl(spotify: spotify), timing: timing)
    }

    /// Spotify that starts playing the requested track on the `playingFrom`-th status poll, its
    /// position advancing in real time from `position`.
    static func spotify(running: Bool = true, playingFrom: Int = 2, position: Double = 0.1) -> FakeSpotify {
        FakeSpotify(running: running) { call, nth, world in
            switch call {
            case .status:
                nth >= playingFrom
                    ? S.status("playing", S.track.uri, world.advancingPosition(from: position))
                    : S.status("paused", "spotify:track:previous")
            default: .success("")
            }
        }
    }

    @Test func notInstalledIsUnavailableAndSendsNothing() async {
        let spotify = FakeSpotify(installed: false, running: false) { _, _, _ in .success("") }
        await #expect(throws: PlayerError.unavailable("Spotify isn't installed")) {
            try await makePlayer(spotify).playSnippet(of: track, from: 0, seconds: 0.1)
        }
        #expect(spotify.calls.isEmpty)
    }

    @Test func playsConfirmsHidesThenPausesAfterTheSnippet() async throws {
        let spotify = Self.spotify()
        try await makePlayer(spotify).playSnippet(of: track, from: 0, seconds: 0.25)

        let calls = spotify.calls
        #expect(Array(calls.prefix(6)) == [.play, .hide, .status, .status, .status, .hide])
        #expect(calls.dropFirst(6).dropLast().allSatisfy { $0 == .status }, "calls: \(calls)")
        #expect(calls.last == .pause)
        #expect(spotify.log[0].script == SpotifyScripts.play(uri: track.uri))
        // The snippet ends on Spotify's clock: position 0.1 at confirmation, target 0.25, so the
        // pause comes ~0.15 s after the confirming poll.
        let log = spotify.log
        let played = (log.last!.at - log[4].at).seconds
        #expect(played >= 0.14 && played < 0.22, "pause came \(played)s after confirmation")
    }

    @Test func aStalledPositionFailsAndPauses() async {
        let spotify = FakeSpotify { call, _, _ in call == .status ? S.status("playing", S.track.uri, 0.1) : .success("") }
        await #expect(throws: PlayerError.failed("Spotify stopped playing")) {
            try await makePlayer(spotify, timing: .fastTimeouts).playSnippet(of: track, from: 0, seconds: 5)
        }
        #expect(spotify.calls.last == .pause)
    }

    @Test func aTrackChangeMidSnippetFailsAndPauses() async {
        let spotify = FakeSpotify { call, nth, world in
            guard call == .status else { return .success("") }
            return nth < 3
                ? S.status("playing", S.track.uri, world.advancingPosition(from: 0.1))
                : S.status("playing", "spotify:ad:1", 0.1)
        }
        await #expect(throws: PlayerError.failed("Spotify played a different track")) {
            try await makePlayer(spotify).playSnippet(of: track, from: 0, seconds: 5)
        }
        #expect(spotify.calls.last == .pause)
    }

    @Test func waitsForThePositionToMoveBeforeStartingTheClock() async throws {
        // Seen live: "playing" + the right id while the position sits at 0 during loading.
        let spotify = FakeSpotify { call, nth, _ in
            guard call == .status else { return .success("") }
            return nth < 3 ? S.status("playing", S.track.uri, 0) : S.status("playing", S.track.uri, 0.06)
        }
        try await makePlayer(spotify).playSnippet(of: track, from: 0, seconds: 0.01)
        #expect(spotify.calls == [.play, .hide, .status, .status, .status, .status, .hide, .status, .pause])
    }

    @Test func seeksToStartAndReseeksIfSpotifyIgnoredIt() async throws {
        // Spotify reports position 0 although we asked for 30: the player must seek again.
        let spotify = FakeSpotify { call, nth, _ in
            guard call == .status else { return .success("") }
            return nth == 0 ? S.status("playing", S.track.uri, 0) : S.status("playing", S.track.uri, 30.1)
        }
        try await makePlayer(spotify).playSnippet(of: track, from: 30, seconds: 0.01)
        #expect(spotify.calls == [.play, .hide, .position, .status, .position, .status, .hide, .status, .pause])
        #expect(spotify.log[2].script == SpotifyScripts.setPosition(30))
    }

    @Test func noReseekWhenPositionIsClose() async throws {
        let spotify = Self.spotify(playingFrom: 0, position: 30.2)
        try await makePlayer(spotify).playSnippet(of: track, from: 30, seconds: 0.01)
        #expect(spotify.calls == [.play, .hide, .position, .status, .hide, .status, .pause])
    }

    @Test func launchesHiddenWhenNotRunningAndWaitsForAnAnswer() async throws {
        // Probe: two "not running yet" answers, then Spotify answers; then it plays on the next poll.
        let spotify = FakeSpotify(running: false) { call, nth, _ in
            switch (call, nth) {
            case (.status, 0), (.status, 1): .failure(S.notRunning)
            case (.status, 2): S.status("stopped", "")
            case (.status, _): S.status("playing")
            default: .success("")
            }
        }
        try await makePlayer(spotify).playSnippet(of: track, from: 0, seconds: 0.01)
        #expect(spotify.calls == [.launch, .status, .status, .status, .play, .hide, .status, .hide, .status, .pause])
    }

    @Test func launchThatNeverAnswersTimesOut() async {
        let spotify = FakeSpotify(running: false) { call, _, _ in
            call == .status ? .failure(S.notRunning) : .success("")
        }
        let started = ContinuousClock.now
        await #expect(throws: PlayerError.failed("Spotify didn't respond within 0.3 seconds after launching")) {
            try await makePlayer(spotify, timing: .fastTimeouts).playSnippet(of: track, from: 0, seconds: 0.01)
        }
        #expect((ContinuousClock.now - started).seconds >= 0.3)
        #expect(!spotify.calls.contains(.play))
    }

    @Test func automationDeniedDuringLaunchIsNotAuthorized() async {
        let spotify = FakeSpotify(running: false) { call, _, _ in
            call == .status ? .failure(S.denied) : .success("")
        }
        await #expect(throws: PlayerError.notAuthorized) {
            try await makePlayer(spotify).playSnippet(of: track, from: 0, seconds: 0.01)
        }
        #expect(spotify.calls.filter { $0 == .status }.count == 1)
    }

    @Test func automationDeniedOnPlayIsNotAuthorized() async {
        let spotify = FakeSpotify { call, _, _ in call == .play ? .failure(S.denied) : .success("") }
        await #expect(throws: PlayerError.notAuthorized) {
            try await makePlayer(spotify).playSnippet(of: track, from: 0, seconds: 0.01)
        }
    }

    @Test func spotifyQuitMidwayIsRelaunchedAndRetriedOnce() async throws {
        // The first `play` finds Spotify gone (-600) and it really is gone; relaunch, then retry.
        let spotify = FakeSpotify { call, nth, world in
            switch (call, nth) {
            case (.play, 0):
                world.running = false
                return .failure(S.notRunning)
            case (.status, _): return S.status("playing")
            default: return .success("")
            }
        }
        try await makePlayer(spotify).playSnippet(of: track, from: 0, seconds: 0.01)
        #expect(spotify.calls == [.play, .launch, .status, .play, .hide, .status, .hide, .status, .pause])
    }

    @Test func connectionInvalidTwiceFailsAfterOneRetry() async {
        let spotify = FakeSpotify { call, _, _ in
            call == .play ? .failure(AppleScriptFailure(number: -609, message: "Connection is invalid.")) : .success("")
        }
        await #expect(throws: PlayerError.failed("Spotify stopped responding (AppleScript error -609)")) {
            try await makePlayer(spotify).playSnippet(of: track, from: 0, seconds: 0.01)
        }
        #expect(spotify.calls.filter { $0 == .play }.count == 2)
    }

    @Test func differentTrackFailsAndPauses() async {
        let spotify = FakeSpotify { call, _, _ in
            call == .status ? S.status("playing", "spotify:track:somethingElse") : .success("")
        }
        await #expect(throws: PlayerError.failed("Spotify played a different track")) {
            try await makePlayer(spotify, timing: .fastTimeouts).playSnippet(of: track, from: 0, seconds: 5)
        }
        #expect(spotify.calls.last == .pause)
    }

    @Test func adFailsImmediately() async {
        let spotify = FakeSpotify { call, _, _ in
            call == .status ? S.status("playing", "spotify:ad:000000012c6b0e28") : .success("")
        }
        await #expect(throws: PlayerError.failed("Spotify played a different track")) {
            try await makePlayer(spotify).playSnippet(of: track, from: 0, seconds: 5)
        }
        #expect(spotify.calls.filter { $0 == .status }.count == 1)
        #expect(spotify.calls.last == .pause)
    }

    @Test func neverStartingFailsAfterTheConfirmTimeout() async {
        let spotify = FakeSpotify { call, _, _ in call == .status ? S.status("paused") : .success("") }
        await #expect(throws: PlayerError.failed("Spotify didn't start playing the track")) {
            try await makePlayer(spotify, timing: .fastTimeouts).playSnippet(of: track, from: 0, seconds: 5)
        }
        #expect(spotify.calls.last == .pause)
    }

    @Test func garbledStatusFails() async {
        let spotify = FakeSpotify { call, _, _ in call == .status ? .success("¯\\_(ツ)_/¯") : .success("") }
        await #expect(throws: PlayerError.failed("Unexpected answer from Spotify: ¯\\_(ツ)_/¯")) {
            try await makePlayer(spotify).playSnippet(of: track, from: 0, seconds: 5)
        }
    }

    @Test func cancellingMidSnippetPausesPromptlyAndThrowsCancellation() async throws {
        let spotify = Self.spotify(playingFrom: 0)
        let player = makePlayer(spotify)
        let snippet = Task { try await player.playSnippet(of: track, from: 0, seconds: 10) }
        #expect(await S.waitUntil { spotify.calls.filter { $0 == .hide }.count == 2 })

        let cancelledAt = ContinuousClock.now
        snippet.cancel()
        let result = await snippet.result
        let latency = (ContinuousClock.now - cancelledAt).seconds

        #expect(throws: CancellationError.self) { try result.get() }
        #expect(spotify.calls.last == .pause)
        #expect(latency < 0.1, "cancel took \(latency)s")
    }

    @Test func cancelledSnippetDoesNotPauseTheSongContinuePlayingResumed() async throws {
        let spotify = Self.spotify(playingFrom: 0)
        let player = makePlayer(spotify)
        let snippet = Task { try await player.playSnippet(of: track, from: 0, seconds: 10) }
        #expect(await S.waitUntil { spotify.calls.filter { $0 == .hide }.count == 2 })

        // What the coordinator does on a correct guess: cancel the snippet, keep the song going.
        snippet.cancel()
        try await player.continuePlaying()
        _ = await snippet.result

        let afterConfirm = spotify.calls.drop { $0 != .resume }
        #expect(Array(afterConfirm) == [.resume], "calls: \(spotify.calls)")
    }

    @Test func stopPausesOnlyWhenRunning() async {
        let running = FakeSpotify { _, _, _ in .success("") }
        await makePlayer(running).stop()
        #expect(running.calls == [.pause])

        let notRunning = FakeSpotify(running: false) { _, _, _ in .success("") }
        await makePlayer(notRunning).stop()
        #expect(notRunning.calls.isEmpty)
    }

    @Test func continuePlayingResumesButNeverRelaunches() async throws {
        let running = FakeSpotify { _, _, _ in .success("") }
        try await makePlayer(running).continuePlaying()
        #expect(running.calls == [.resume])

        let notRunning = FakeSpotify(running: false) { _, _, _ in .success("") }
        await #expect(throws: PlayerError.failed("Spotify isn't running")) {
            try await makePlayer(notRunning).continuePlaying()
        }
        #expect(notRunning.calls.isEmpty)
    }

    @Test func defaultInitIsUsableFromAnyContext() async {
        let player = await Task.detached { SpotifyAppPlayer() }.value
        #expect(player.displayName == "Spotify app")
        #expect(player.playsFullTrack)
    }
}
