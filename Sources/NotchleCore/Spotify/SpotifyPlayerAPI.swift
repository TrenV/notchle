import Foundation

public struct SpotifyDevice: Sendable, Hashable {
    public var id: String?
    public var name: String
    public var type: String
    public var isActive: Bool
    public var isRestricted: Bool

    public init(id: String?, name: String, type: String, isActive: Bool = false, isRestricted: Bool = false) {
        self.id = id
        self.name = name
        self.type = type
        self.isActive = isActive
        self.isRestricted = isRestricted
    }
}

/// The parts of GET /v1/me/player the snippet timing needs.
public struct SpotifyPlayback: Sendable, Hashable {
    public var isPlaying: Bool
    public var progressMs: Int
    public var itemURI: String?
    public var currentlyPlayingType: String?
    public var deviceID: String?
    /// `item.linked_from.uri`: Spotify plays a relinked version for the account's market and
    /// reports the requested track here.
    public var linkedFromURI: String?

    public init(isPlaying: Bool, progressMs: Int, itemURI: String?, currentlyPlayingType: String? = "track",
                deviceID: String? = nil, linkedFromURI: String? = nil) {
        self.linkedFromURI = linkedFromURI
        self.isPlaying = isPlaying
        self.progressMs = progressMs
        self.itemURI = itemURI
        self.currentlyPlayingType = currentlyPlayingType
        self.deviceID = deviceID
    }

    public var position: Double { Double(progressMs) / 1000 }

    /// Whether this is `uri`, directly or as the relinked version of it.
    public func isPlayingItem(_ uri: String) -> Bool { itemURI == uri || linkedFromURI == uri }

    /// For error logs: what Spotify reported.
    public var summary: String {
        "is_playing=\(isPlaying) item=\(itemURI ?? "none") linked_from=\(linkedFromURI ?? "none") device=\(deviceID ?? "none") type=\(currentlyPlayingType ?? "none")"
    }
    public var isAd: Bool { currentlyPlayingType == "ad" }
}

/// Spotify Web API player endpoints: pure request building, response parsing and error mapping.
/// https://developer.spotify.com/documentation/web-api/reference/start-a-users-playback
public enum SpotifyPlayerAPI {
    public static let baseURL = "https://api.spotify.com/v1/"

    public static func devices() -> HTTPRequest { HTTPRequest(method: "GET", url: url("me/player/devices")) }
    /// `market=from_token` so Spotify reports `linked_from` for relinked tracks.
    public static func playbackState() -> HTTPRequest { HTTPRequest(method: "GET", url: url("me/player?market=from_token")) }
    public static func profile() -> HTTPRequest { HTTPRequest(method: "GET", url: url("me")) }

    /// PUT /me/player: move playback to this Mac's Spotify app without starting anything.
    public static func transfer(deviceID: String) -> HTTPRequest {
        put("me/player", json: ["device_ids": [deviceID], "play": false])
    }

    /// PUT /me/player/play: this one track, from positionMs, on the given device.
    public static func play(deviceID: String, trackURI: String, positionMs: Int) -> HTTPRequest {
        put("me/player/play?device_id=\(SpotifyAccounts.percentEncode(deviceID))",
            json: ["uris": [trackURI], "position_ms": max(0, positionMs)])
    }

    /// PUT /me/player/play without a body resumes the current track.
    public static func resume(deviceID: String?) -> HTTPRequest { put(withDevice("me/player/play", deviceID), json: nil) }

    public static func pause(deviceID: String?) -> HTTPRequest { put(withDevice("me/player/pause", deviceID), json: nil) }

    public static func seek(positionMs: Int, deviceID: String?) -> HTTPRequest {
        put(withDevice("me/player/seek?position_ms=\(max(0, positionMs))", deviceID), json: nil)
    }

    public static func parseDevices(_ body: Data) -> [SpotifyDevice] {
        guard let list = jsonObject(body)?["devices"] as? [[String: Any]] else { return [] }
        return list.map { d in
            SpotifyDevice(id: d["id"] as? String, name: d["name"] as? String ?? "", type: d["type"] as? String ?? "",
                          isActive: d["is_active"] as? Bool ?? false, isRestricted: d["is_restricted"] as? Bool ?? false)
        }
    }

    /// The Spotify desktop app on this Mac: a controllable "Computer" device named after this
    /// machine (the app's default device name), else the first controllable Computer.
    public static func pickLocalDevice(_ devices: [SpotifyDevice], machineName: String) -> SpotifyDevice? {
        let computers = devices.filter {
            $0.id != nil && !$0.isRestricted && $0.type.caseInsensitiveCompare("Computer") == .orderedSame
        }
        return computers.first { $0.name.caseInsensitiveCompare(machineName) == .orderedSame } ?? computers.first
    }

    /// GET /me/player: 204 (empty body) means nothing is playing anywhere → nil.
    public static func parsePlayback(_ body: Data) -> SpotifyPlayback? {
        guard let root = jsonObject(body) else { return nil }
        let item = root["item"] as? [String: Any]
        let device = root["device"] as? [String: Any]
        return SpotifyPlayback(
            isPlaying: root["is_playing"] as? Bool ?? false,
            progressMs: (root["progress_ms"] as? NSNumber)?.intValue ?? 0,
            itemURI: item?["uri"] as? String,
            currentlyPlayingType: root["currently_playing_type"] as? String,
            deviceID: device?["id"] as? String,
            linkedFromURI: (item?["linked_from"] as? [String: Any])?["uri"] as? String)
    }

    /// GET /me → display_name (falls back to the account id).
    public static func parseDisplayName(_ body: Data) -> String? {
        guard let root = jsonObject(body) else { return nil }
        if let name = root["display_name"] as? String, !name.isEmpty { return name }
        return root["id"] as? String
    }

    public static let noDeviceMessage = "Open the Spotify app on this Mac (it can stay hidden)"

    /// Maps a failed player call to a PlayerError whose text fits the notch.
    public static func mapError(status: Int, body: Data, retryAfter: String?) -> PlayerError {
        let error = jsonObject(body)?["error"] as? [String: Any]
        let message = error?["message"] as? String
        let reason = error?["reason"] as? String
        switch status {
        case 401:
            return .failed("Spotify sign-in expired. Connect Spotify again in settings")
        case 403 where reason == "PREMIUM_REQUIRED":
            return .unavailable("Spotify Connect needs Spotify Premium")
        case 403:
            return .unavailable("Spotify refused playback control (\(message ?? "forbidden")). It needs Premium, and your account added under User Management in your Spotify developer app")
        case 404:
            return .unavailable(noDeviceMessage)
        case 429:
            if let seconds = retryAfter.flatMap(Double.init) {
                return .failed("Spotify is rate-limiting Notchle. Try again in \(max(1, Int(seconds.rounded(.up)))) s.")
            }
            return .failed("Spotify is rate-limiting Notchle. Try again in a minute.")
        default:
            return .failed("Spotify returned HTTP \(status)\(message.map { ": \($0)" } ?? "")")
        }
    }

    static func jsonObject(_ body: Data) -> [String: Any]? {
        guard !body.isEmpty else { return nil }
        return (try? JSONSerialization.jsonObject(with: body)) as? [String: Any]
    }

    private static func url(_ path: String) -> URL { URL(string: baseURL + path)! }

    private static func withDevice(_ path: String, _ deviceID: String?) -> String {
        guard let deviceID else { return path }
        return "\(path)\(path.contains("?") ? "&" : "?")device_id=\(SpotifyAccounts.percentEncode(deviceID))"
    }

    private static func put(_ path: String, json: [String: Any]?) -> HTTPRequest {
        // Spotify wants a Content-Length on PUT even without a body, so always send one.
        let body = json.flatMap { try? JSONSerialization.data(withJSONObject: $0, options: [.sortedKeys]) } ?? Data()
        return HTTPRequest(method: "PUT", url: url(path), headers: ["Content-Type": "application/json"], body: body)
    }
}
