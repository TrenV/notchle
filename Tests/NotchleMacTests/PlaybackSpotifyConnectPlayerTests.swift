import Foundation
import Testing
@testable import NotchleCore
@testable import NotchleMac

/// `SpotifyConnectPlayer` against a simulated Spotify Web API (`FakeSpotifyWeb`). No real
/// network, no real Spotify: the live path is unobserved by design.
@MainActor
@Suite struct PlaybackSpotifyConnectPlayerTests {
    typealias S = PlaybackTestSupport
    let track = PlaybackTestSupport.track

    func makePlayer(_ web: FakeSpotifyWeb, tokens: MemoryTokenStore = MemoryTokenStore(),
                    machineName: String = "Test Mac", timing: SpotifyConnectPlayer.Timing = .fast) -> SpotifyConnectPlayer {
        let api = SpotifyWebAPI(http: web, tokens: tokens, clientID: { "cid" })
        return SpotifyConnectPlayer(api: api, app: CountingAppControl(web: web), machineName: machineName, timing: timing)
    }

    @Test func requestSequenceDevicesTransferPlayPollPause() async throws {
        let web = FakeSpotifyWeb()
        try await makePlayer(web).playSnippet(of: track, from: 30, seconds: 0.25)

        #expect(web.playerCalls == ["GET /v1/me/player/devices", "PUT /v1/me/player", "PUT /v1/me/player/play",
                                    "HIDE", "HIDE", "PUT /v1/me/player/pause"], "calls: \(web.calls)")
        let log = web.log
        #expect(log[1].body == #"{"device_ids":["mac"],"play":false}"#)
        let play = try #require(log.first { $0.name == "PUT /v1/me/player/play" })
        #expect(play.query == "device_id=mac")
        #expect(play.body == #"{"position_ms":30000,"uris":["spotify:track:2FZcjBYK4dTt48q94pJbJD"]}"#)
        #expect(log.last?.query == "device_id=mac")
        // Timed by progress_ms: from the play request to the pause is ~0.25 s of "audio".
        let played = (log.last!.at - play.at).seconds
        #expect(played >= 0.24 && played < 0.4, "pause came \(played)s after play")
        // Paused within a poll of the target position.
        let pausedAt = web.state.withLock { $0.basePositionMs }
        #expect(pausedAt >= 30_240 && pausedAt < 30_400, "paused at \(pausedAt) ms")
    }

    @Test func anActiveDeviceIsNotTransferredAgain() async throws {
        let web = FakeSpotifyWeb(devices: [SpotifyDevice(id: "mac", name: "Test Mac", type: "Computer", isActive: true)])
        try await makePlayer(web).playSnippet(of: track, from: 0, seconds: 0.1)
        #expect(!web.calls.contains("PUT /v1/me/player"), "calls: \(web.calls)")
    }

    @Test func picksTheDeviceNamedAfterThisMac() async throws {
        let web = FakeSpotifyWeb(devices: [
            SpotifyDevice(id: "phone", name: "Tren's MacBook", type: "Smartphone", isActive: true),
            SpotifyDevice(id: "other", name: "Office iMac", type: "Computer"),
            SpotifyDevice(id: "this", name: "Tren's MacBook", type: "Computer"),
        ])
        try await makePlayer(web, machineName: "Tren's MacBook").playSnippet(of: track, from: 0, seconds: 0.1)
        #expect(web.log.first { $0.name == "PUT /v1/me/player/play" }?.query == "device_id=this")
    }

    @Test func noComputerDeviceIsAClearErrorAndPlaysNothing() async {
        let web = FakeSpotifyWeb(devices: [SpotifyDevice(id: "phone", name: "Phone", type: "Smartphone", isActive: true)])
        await #expect(throws: PlayerError.unavailable("Open the Spotify app on this Mac (it can stay hidden)")) {
            try await makePlayer(web).playSnippet(of: track, from: 0, seconds: 0.1)
        }
        #expect(web.playerCalls == ["GET /v1/me/player/devices"])
    }

    @Test func aLateStartDoesNotShortenTheSnippet() async throws {
        // progress_ms sits at the start for 0.3 s while the track loads (is_playing already true).
        let web = FakeSpotifyWeb(loadDelay: .milliseconds(300))
        try await makePlayer(web).playSnippet(of: track, from: 10, seconds: 0.25)
        let log = web.log
        let play = try #require(log.first { $0.name == "PUT /v1/me/player/play" })
        let played = (log.last!.at - play.at).seconds
        #expect(log.last?.name == "PUT /v1/me/player/pause")
        #expect(played >= 0.54 && played < 0.75, "pause came \(played)s after play; a wall-clock timer would say 0.25")
        let pausedAt = web.state.withLock { $0.basePositionMs }
        #expect(pausedAt >= 10_240, "only \(pausedAt - 10_000) ms of audio")
    }

    @Test func startIsPulledBackSoTheSnippetFits() {
        #expect(SpotifyConnectPlayer.clampedStart(195, seconds: 10, durationMs: 200_000) == 190)
        #expect(SpotifyConnectPlayer.clampedStart(-3, seconds: 1, durationMs: 200_000) == 0)
        #expect(SpotifyConnectPlayer.clampedStart(.nan, seconds: 1, durationMs: 0) == 0)
        #expect(SpotifyConnectPlayer.clampedStart(50, seconds: 1, durationMs: 0) == 50)
    }

    @Test func cancellingMidSnippetPausesPromptlyAndThrowsCancellation() async throws {
        let web = FakeSpotifyWeb()
        let player = makePlayer(web)
        let snippet = Task { try await player.playSnippet(of: track, from: 0, seconds: 10) }
        #expect(await S.waitUntil { web.state.withLock { $0.hides } == 2 })

        let cancelledAt = ContinuousClock.now
        snippet.cancel()
        let result = await snippet.result
        let latency = (ContinuousClock.now - cancelledAt).seconds

        #expect(throws: CancellationError.self) { try result.get() }
        // The fake refuses requests from a cancelled task, like URLSession: the pause only
        // arrives because it is sent from its own task.
        #expect(web.calls.last == "PUT /v1/me/player/pause", "calls: \(web.calls)")
        #expect(web.state.withLock { $0.movingSince } == nil)
        #expect(latency < 0.5, "cancel took \(latency)s")
    }

    @Test func cancelledSnippetDoesNotPauseTheSongContinuePlayingResumed() async throws {
        let web = FakeSpotifyWeb()
        let player = makePlayer(web)
        let snippet = Task { try await player.playSnippet(of: track, from: 0, seconds: 10) }
        #expect(await S.waitUntil { web.state.withLock { $0.hides } == 2 })

        snippet.cancel()
        try await player.continuePlaying()
        _ = await snippet.result
        try await Task.sleep(for: .milliseconds(100))

        let resume = web.log.last { $0.name == "PUT /v1/me/player/play" }
        #expect(resume?.body == "", "the resume has no body")
        #expect(resume?.query == "device_id=mac")
        let afterResume = web.log.drop { $0.name != "PUT /v1/me/player/play" || !$0.body.isEmpty }.map(\.name)
        #expect(afterResume.first == "PUT /v1/me/player/play", "calls: \(web.calls)")
        #expect(!afterResume.contains("PUT /v1/me/player/pause"), "calls: \(web.calls)")
    }

    @Test func cancelledSnippetDoesNotPauseTheRestartedSong() async throws {
        let web = FakeSpotifyWeb()
        let player = makePlayer(web)
        let snippet = Task { try await player.playSnippet(of: track, from: 0, seconds: 10) }
        #expect(await S.waitUntil { web.state.withLock { $0.hides } == 2 })

        // Restart mid-snippet without waiting for the snippet to wind down.
        snippet.cancel()
        try await player.restartTrack(track)
        let result = await snippet.result
        #expect(throws: CancellationError.self) { try result.get() }
        try await Task.sleep(for: .milliseconds(100))

        let plays = web.log.filter { $0.name == "PUT /v1/me/player/play" }
        #expect(plays.count == 2)
        #expect(plays.last?.body == #"{"position_ms":0,"uris":["spotify:track:2FZcjBYK4dTt48q94pJbJD"]}"#)
        let afterRestart = web.log.drop { $0.at < plays.last!.at }.map(\.name)
        #expect(!afterRestart.contains("PUT /v1/me/player/pause"), "calls: \(web.calls)")
        #expect(web.state.withLock { $0.movingSince } != nil, "the restarted song is playing")
    }

    @Test func restartTrackPlaysFromZeroHidesAndNeverPauses() async throws {
        let web = FakeSpotifyWeb()
        try await makePlayer(web).restartTrack(track)
        try await Task.sleep(for: .milliseconds(100))
        #expect(web.playerCalls == ["GET /v1/me/player/devices", "PUT /v1/me/player", "PUT /v1/me/player/play", "HIDE"],
                "calls: \(web.calls)")
        #expect(web.log[2].body == #"{"position_ms":0,"uris":["spotify:track:2FZcjBYK4dTt48q94pJbJD"]}"#)
    }

    @Test func stopPausesTheKnownDeviceOnly() async throws {
        let web = FakeSpotifyWeb()
        let player = makePlayer(web)
        await player.stop()
        #expect(web.calls.isEmpty, "no device yet, nothing to pause")
        try await player.restartTrack(track)
        await player.stop()
        #expect(web.calls.last == "PUT /v1/me/player/pause")
    }

    @Test func premiumRequiredOnPlayIsUnavailable() async {
        let web = FakeSpotifyWeb { entry, _ in
            entry.name == "PUT /v1/me/player/play"
                ? HTTPResponse(status: 403, text: #"{"error":{"status":403,"message":"Premium required","reason":"PREMIUM_REQUIRED"}}"#)
                : nil
        }
        await #expect(throws: PlayerError.unavailable("Spotify Connect needs Spotify Premium")) {
            try await makePlayer(web).playSnippet(of: track, from: 0, seconds: 0.1)
        }
        #expect(web.calls.last == "PUT /v1/me/player/pause", "a failed snippet still pauses")
    }

    @Test func rateLimitedPollingFails() async {
        let web = FakeSpotifyWeb { entry, _ in
            entry.name == "GET /v1/me/player" ? HTTPResponse(status: 429, headers: ["Retry-After": "4"], text: "") : nil
        }
        await #expect(throws: PlayerError.failed("Spotify is rate-limiting Notchle. Try again in 4 s.")) {
            try await makePlayer(web).playSnippet(of: track, from: 0, seconds: 0.1)
        }
    }

    @Test func an401RefreshesTheTokenAndCarriesOn() async throws {
        let web = FakeSpotifyWeb { entry, nth in
            entry.name == "GET /v1/me/player/devices" && nth == 0 ? HTTPResponse(status: 401) : nil
        }
        let tokens = MemoryTokenStore()
        try await makePlayer(web, tokens: tokens).playSnippet(of: track, from: 0, seconds: 0.1)
        #expect(Array(web.calls.prefix(3)) == ["GET /v1/me/player/devices", "POST /api/token", "GET /v1/me/player/devices"])
        #expect(tokens.load()?.accessToken == "new-access")
    }

    @Test func aDifferentTrackFailsAndPauses() async {
        let web = FakeSpotifyWeb { entry, _ in
            entry.name == "GET /v1/me/player"
                ? HTTPResponse(status: 200, text: #"{"is_playing":true,"progress_ms":500,"item":{"uri":"spotify:track:other"}}"#)
                : nil
        }
        do {
            try await makePlayer(web, timing: .fastTimeouts).playSnippet(of: track, from: 0, seconds: 0.1)
            Issue.record("expected a failure")
        } catch PlayerError.failed(let message) {
            #expect(message.hasPrefix("Spotify played a different track"))
            #expect(message.contains("item=spotify:track:other"))   // what Spotify reported, for the log
        } catch { Issue.record("unexpected \(error)") }
        #expect(web.calls.last == "PUT /v1/me/player/pause")
    }

    @Test func anAdFailsImmediately() async {
        let web = FakeSpotifyWeb { entry, _ in
            entry.name == "GET /v1/me/player"
                ? HTTPResponse(status: 200, text: #"{"is_playing":true,"progress_ms":500,"currently_playing_type":"ad","item":null}"#)
                : nil
        }
        await #expect(throws: PlayerError.failed("Spotify is playing an ad")) {
            try await makePlayer(web).playSnippet(of: track, from: 0, seconds: 0.1)
        }
    }

    @Test func aStalledPositionFails() async {
        let web = FakeSpotifyWeb { entry, nth in
            entry.name == "GET /v1/me/player"
                ? HTTPResponse(status: 200, text: #"{"is_playing":true,"progress_ms":100,"item":{"uri":"spotify:track:2FZcjBYK4dTt48q94pJbJD"}}"#)
                : nil
        }
        await #expect(throws: PlayerError.failed("Spotify stopped playing")) {
            try await makePlayer(web, timing: .fastTimeouts).playSnippet(of: track, from: 0, seconds: 5)
        }
    }

    @Test func anIgnoredStartPositionIsSeekedAgain() async throws {
        // Spotify starts at 0 although position_ms was 60000: the player seeks.
        let web = FakeSpotifyWeb { entry, nth in
            entry.name == "GET /v1/me/player" && nth == 0
                ? HTTPResponse(status: 200, text: #"{"is_playing":true,"progress_ms":200,"item":{"uri":"spotify:track:2FZcjBYK4dTt48q94pJbJD"}}"#)
                : nil
        }
        try await makePlayer(web).playSnippet(of: track, from: 60, seconds: 0.1)
        let seek = web.log.first { $0.name == "PUT /v1/me/player/seek" }
        #expect(seek?.query == "position_ms=60000&device_id=mac")
    }

    @Test func notSignedInIsAClearError() async {
        let web = FakeSpotifyWeb()
        await #expect(throws: PlayerError.failed(SpotifyWebAPI.notSignedIn)) {
            try await makePlayer(web, tokens: MemoryTokenStore(nil)).playSnippet(of: track, from: 0, seconds: 0.1)
        }
        #expect(web.calls.isEmpty)
    }

    @Test func displayNameAndDefaultInitFromAnyContext() async {
        let player = await Task.detached {
            SpotifyConnectPlayer(api: SpotifyWebAPI(http: FakeSpotifyWeb(), tokens: MemoryTokenStore(), clientID: { nil }))
        }.value
        #expect(player.displayName == "Spotify (full songs, no window)")
        #expect(player.playsFullTrack)
    }
}
