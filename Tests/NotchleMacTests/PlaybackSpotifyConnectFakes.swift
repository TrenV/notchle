import Foundation
import os
@testable import NotchleCore
@testable import NotchleMac

/// A simulated Spotify Web API + Spotify desktop app for `SpotifyConnectPlayer` tests. Playback
/// progresses in real time after `play`, but only after `loadDelay` (progress_ms sits at the
/// start position while the track "loads", as the real app does). Like URLSession, it refuses
/// requests from a cancelled task.
final class FakeSpotifyWeb: HTTPRequesting, Sendable {
    struct Entry: Sendable {
        let method: String
        let path: String
        let query: String
        let body: String
        let at: ContinuousClock.Instant
        var name: String { path.isEmpty ? method : "\(method) \(path)" }
    }

    struct State: Sendable {
        var devices: [SpotifyDevice]
        var activeDeviceID: String?
        var uri: String?
        var basePositionMs = 0
        /// When progress starts moving (after the load delay); nil while paused.
        var movingSince: ContinuousClock.Instant?
        var log: [Entry] = []
        var hides = 0
    }

    /// Overrides the answer to one request; nil means "behave normally".
    typealias Override = @Sendable (_ entry: Entry, _ nth: Int) -> HTTPResponse?

    let state: OSAllocatedUnfairLock<State>
    let loadDelay: Duration
    private let override: Override?

    init(devices: [SpotifyDevice] = [SpotifyDevice(id: "mac", name: "Test Mac", type: "Computer")],
         loadDelay: Duration = .zero, override: Override? = nil) {
        state = OSAllocatedUnfairLock(initialState: State(devices: devices,
                                                          activeDeviceID: devices.first { $0.isActive }?.id))
        self.loadDelay = loadDelay
        self.override = override
    }

    var log: [Entry] { state.withLock { $0.log } }
    var calls: [String] { log.map(\.name) }
    /// Player commands only: status polls and the album lookup (GET /v1/tracks/…) left out.
    var playerCalls: [String] { calls.filter { $0 != "GET /v1/me/player" && !$0.hasPrefix("GET /v1/tracks/") } }
    static let albumURI = "spotify:album:ALBUM"

    func position(_ s: State) -> Int {
        guard let since = s.movingSince else { return s.basePositionMs }
        let elapsed = ContinuousClock.now - since
        return elapsed < .zero ? s.basePositionMs : s.basePositionMs + Int(elapsed.seconds * 1000)
    }

    func send(_ request: HTTPRequest) async throws -> HTTPResponse {
        if Task.isCancelled { throw URLError(.cancelled) }
        let comps = URLComponents(url: request.url, resolvingAgainstBaseURL: false)
        let entry = Entry(method: request.method, path: request.url.path, query: comps?.query ?? "",
                          body: request.bodyText, at: .now)
        let nth = state.withLock { s in
            s.log.append(entry)
            return s.log.filter { $0.name == entry.name }.count - 1
        }
        if let answer = override?(entry, nth) { return answer }
        return state.withLock { s -> HTTPResponse in
            switch entry.name {
            case "POST /api/token":
                return HTTPResponse(status: 200, text: tokenJSON)
            case let name where name.hasPrefix("GET /v1/tracks/"):
                return HTTPResponse(status: 200, text: #"{"album":{"uri":"\#(FakeSpotifyWeb.albumURI)"}}"#)
            case "GET /v1/me/player/devices":
                let list = s.devices.map { d in
                    "{\"id\":\(d.id.map { "\"\($0)\"" } ?? "null"),\"name\":\"\(d.name)\",\"type\":\"\(d.type)\","
                        + "\"is_active\":\(d.id == s.activeDeviceID),\"is_restricted\":\(d.isRestricted)}"
                }
                return HTTPResponse(status: 200, text: "{\"devices\":[\(list.joined(separator: ","))]}")
            case "PUT /v1/me/player":
                let json = try? JSONSerialization.jsonObject(with: request.body ?? Data()) as? [String: Any]
                s.activeDeviceID = (json?["device_ids"] as? [String])?.first
                return HTTPResponse(status: 204)
            case "PUT /v1/me/player/play":
                if let json = try? JSONSerialization.jsonObject(with: request.body ?? Data()) as? [String: Any] {
                    s.uri = (json["uris"] as? [String])?.first
                        ?? ((json["offset"] as? [String: Any])?["uri"] as? String)   // album-context play
                    s.basePositionMs = json["position_ms"] as? Int ?? 0
                    s.movingSince = ContinuousClock.now.advanced(by: loadDelay)
                } else {
                    s.movingSince = s.movingSince ?? .now
                }
                return HTTPResponse(status: 202)
            case "PUT /v1/me/player/pause":
                s.basePositionMs = position(s)
                s.movingSince = nil
                return HTTPResponse(status: 204)
            case "PUT /v1/me/player/seek":
                let ms = Int(comps?.queryItems?.first { $0.name == "position_ms" }?.value ?? "0") ?? 0
                s.basePositionMs = ms
                if s.movingSince != nil { s.movingSince = .now }
                return HTTPResponse(status: 204)
            case "GET /v1/me/player":
                guard let uri = s.uri else { return HTTPResponse(status: 204) }
                return HTTPResponse(status: 200, text: """
                    {"is_playing":\(s.movingSince != nil),"progress_ms":\(position(s)),"currently_playing_type":"track",
                     "item":{"uri":"\(uri)"},"device":{"id":"\(s.activeDeviceID ?? "")"}}
                    """)
            case "GET /v1/me":
                return HTTPResponse(status: 200, text: #"{"display_name":"Tren"}"#)
            default:
                return HTTPResponse(status: 404)
            }
        }
    }

    /// Entries from the first one named `name` onwards.
    func log(from name: String) -> [Entry] { Array(log.drop { $0.name != name }) }
}

/// Counts `hide()`; Spotify is always "running".
@MainActor
final class CountingAppControl: SpotifyAppControlling {
    let web: FakeSpotifyWeb
    nonisolated init(web: FakeSpotifyWeb) { self.web = web }
    var isInstalled: Bool { true }
    var isRunning: Bool { true }
    func launchHidden() async throws {}
    func hide() {
        web.state.withLock {
            $0.hides += 1
            $0.log.append(.init(method: "HIDE", path: "", query: "", body: "", at: .now))
        }
    }
}

final class MemoryTokenStore: SpotifyTokenStore, @unchecked Sendable {
    private let lock = NSLock()
    private var tokens: SpotifyTokens?
    init(_ tokens: SpotifyTokens? = SpotifyTokens(accessToken: "a", refreshToken: "r", expiresAt: .distantFuture)) {
        self.tokens = tokens
    }
    func load() -> SpotifyTokens? { lock.withLock { tokens } }
    func save(_ tokens: SpotifyTokens?) { lock.withLock { self.tokens = tokens } }
}

let tokenJSON = #"{"access_token":"new-access","expires_in":3600,"refresh_token":"new-refresh"}"#

extension SpotifyConnectPlayer.Timing {
    /// Fast polling, generous deadlines (see SpotifyAppPlayer.Timing.fast).
    static let fast = SpotifyConnectPlayer.Timing(
        startTimeout: .seconds(10), pollInterval: .milliseconds(5), maxSleepSlice: 0.05,
        stallTimeout: .seconds(10), endLead: 0, audibleProgress: 0.05, positionTolerance: 1.0,
        reseekInterval: .zero, pauseTimeout: 3)
    static let fastTimeouts = SpotifyConnectPlayer.Timing(
        startTimeout: .milliseconds(300), pollInterval: .milliseconds(5), maxSleepSlice: 0.05,
        stallTimeout: .milliseconds(200), endLead: 0, audibleProgress: 0.05, positionTolerance: 1.0,
        reseekInterval: .zero, pauseTimeout: 3)
}
