import Foundation
import os
@testable import NotchleCore
@testable import NotchleMac

/// A simulated Spotify for `SpotifyAppPlayer` tests: shared by the fake runner (Apple Events)
/// and the fake app control (NSWorkspace), and records every call in order with a timestamp.
final class FakeSpotify: Sendable {
    enum Call: String, Sendable { case play, position, status, pause, resume, launch, hide, other }

    struct Entry: Sendable {
        let call: Call
        let script: String?
        let at: ContinuousClock.Instant
    }

    struct State: Sendable {
        var installed = true
        var running = true
        var log: [Entry] = []
        var counts: [Call: Int] = [:]
        var clockStart: ContinuousClock.Instant?
    }

    /// Answers a script. `nth` is how many times this kind of call was made before (0-based).
    typealias Responder = @Sendable (_ call: Call, _ nth: Int, _ spotify: FakeSpotify) -> Result<String, AppleScriptFailure>

    let state = OSAllocatedUnfairLock(initialState: State())
    private let responder: Responder

    init(installed: Bool = true, running: Bool = true, responder: @escaping Responder) {
        self.responder = responder
        state.withLock {
            $0.installed = installed
            $0.running = running
        }
    }

    static func call(for script: String) -> Call {
        switch script {
        case SpotifyScripts.status: .status
        case SpotifyScripts.pause: .pause
        case SpotifyScripts.resume: .resume
        case _ where script.contains("play track"): .play
        case _ where script.contains("set player position"): .position
        default: .other
        }
    }

    @discardableResult
    func record(_ call: Call, script: String? = nil) -> Int {
        state.withLock {
            let nth = $0.counts[call, default: 0]
            $0.counts[call] = nth + 1
            $0.log.append(Entry(call: call, script: script, at: .now))
            return nth
        }
    }

    func respond(to script: String) -> Result<String, AppleScriptFailure> {
        let call = Self.call(for: script)
        let nth = record(call, script: script)
        return responder(call, nth, self)
    }

    /// A playback position that advances in real time from `base`, starting at the first call.
    func advancingPosition(from base: Double) -> Double {
        state.withLock {
            let start = $0.clockStart ?? .now
            $0.clockStart = start
            return base + (ContinuousClock.now - start).seconds
        }
    }

    var log: [Entry] { state.withLock { $0.log } }
    var calls: [Call] { log.map(\.call) }
    var running: Bool {
        get { state.withLock { $0.running } }
        set { state.withLock { $0.running = newValue } }
    }
    var installed: Bool { state.withLock { $0.installed } }
}

struct FakeRunner: AppleScriptRunning {
    let spotify: FakeSpotify
    func run(_ source: String) async throws -> String {
        try spotify.respond(to: source).get()
    }
}

@MainActor
final class FakeAppControl: SpotifyAppControlling {
    let spotify: FakeSpotify
    var launchError: (any Error)?

    nonisolated init(spotify: FakeSpotify) { self.spotify = spotify }

    var isInstalled: Bool { spotify.installed }
    var isRunning: Bool { spotify.running }

    func launchHidden() async throws {
        spotify.record(.launch)
        if let launchError { throw launchError }
        spotify.running = true
    }

    func hide() { spotify.record(.hide) }
}

extension SpotifyAppPlayer.Timing {
    /// Short timeouts so failure paths finish quickly.
    /// Fast polling with generous deadlines: tests that expect success finish as soon as the
    /// fake answers, and don't fail when other suites (the UI OCR tests) hog the main actor.
    static let fast = SpotifyAppPlayer.Timing(
        launchTimeout: .seconds(10), launchPollInterval: .milliseconds(10),
        confirmTimeout: .seconds(10), confirmPollInterval: .milliseconds(5),
        positionTolerance: 1.0, audibleProgress: 0.05, reseekInterval: .zero,
        endLead: 0.0, maxSleepSlice: 0.05, stallTimeout: .seconds(10))

    /// Short deadlines, only for the tests that assert a timeout fires.
    static let fastTimeouts = SpotifyAppPlayer.Timing(
        launchTimeout: .milliseconds(300), launchPollInterval: .milliseconds(10),
        confirmTimeout: .milliseconds(300), confirmPollInterval: .milliseconds(5),
        positionTolerance: 1.0, audibleProgress: 0.05, reseekInterval: .zero,
        endLead: 0.0, maxSleepSlice: 0.05, stallTimeout: .milliseconds(200))
}

enum PlaybackTestSupport {
    static let track = Track(
        id: "2FZcjBYK4dTt48q94pJbJD", uri: "spotify:track:2FZcjBYK4dTt48q94pJbJD",
        title: "Title", artists: ["Artist"], durationMs: 200_000, previewURL: nil)

    /// Default position 0.1: playing and already audibly past a start of 0.
    static func status(_ state: String, _ uri: String = track.uri, _ position: Double = 0.1) -> Result<String, AppleScriptFailure> {
        .success("\(state)\n\(uri)\n\(position)")
    }

    static let notRunning = AppleScriptFailure(number: -600, message: "Application isn't running.")
    static let denied = AppleScriptFailure(number: -1743, message: "Not authorized to send Apple events to Spotify.")

    /// Polls `condition` until true or `timeout` passes. Returns whether it became true.
    @MainActor
    static func waitUntil(timeout: Duration = .seconds(15), _ condition: () -> Bool) async -> Bool {
        let deadline = ContinuousClock.now.advanced(by: timeout)
        while !condition() {
            if ContinuousClock.now >= deadline { return false }
            try? await Task.sleep(for: .milliseconds(2))
        }
        return true
    }
}

extension Duration {
    var seconds: Double {
        let (s, atto) = components
        return Double(s) + Double(atto) / 1e18
    }
}
