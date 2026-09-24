import AppKit
import NotchleCore

/// Diagnostic (`Notchle --probe-spotify-connect`): runs the Connect play sequence with the
/// saved sign-in and logs every request and Spotify's raw response to
/// ~/Library/Logs/Notchle-spotify-connect-probe.txt. Never logs tokens: the Authorization
/// header is not written, and token-endpoint bodies are skipped. Plays ~4 s, then pauses.
@MainActor
public enum SpotifyConnectProbe {
    nonisolated public static let logURL = FileManager.default.homeDirectoryForCurrentUser
        .appendingPathComponent("Library/Logs/Notchle-spotify-connect-probe.txt")
    nonisolated static let testTrack = "spotify:track:2FZcjBYK4dTt48q94pJbJD"

    nonisolated static func log(_ line: String) {
        Swift.print(line)
        let data = Data((line + "\n").utf8)
        if let h = try? FileHandle(forWritingTo: logURL) { h.seekToEndOfFile(); h.write(data); try? h.close() }
        else { try? data.write(to: logURL) }
    }

    struct LoggingRequester: HTTPRequesting {
        let inner = URLSessionHTTPRequester()
        func send(_ request: HTTPRequest) async throws -> HTTPResponse {
            let isToken = request.url.host == "accounts.spotify.com"
            let body = request.body.flatMap { String(data: $0, encoding: .utf8) } ?? ""
            SpotifyConnectProbe.log("→ \(request.method) \(request.url.absoluteString)\(isToken || body.isEmpty ? "" : "  body: \(body)")")
            let response = try await inner.send(request)
            let text = isToken ? "(token response, not logged)" : (String(data: response.body, encoding: .utf8) ?? "<\(response.body.count) bytes>")
            SpotifyConnectProbe.log("← \(response.status)  \(text.prefix(1500))")
            return response
        }
    }

    public static func run() async {
        try? FileManager.default.removeItem(at: logURL)
        log("Notchle Spotify Connect probe, \(Date()), machine name \"\(Host.current().localizedName ?? "?")\"")
        let api = SpotifyWebAPI(http: LoggingRequester(), tokens: KeychainSpotifyTokenStore(),
                                clientID: { UserDefaults.standard.string(forKey: "spotifyClientID") })
        do {
            log("\n== 0. account")
            _ = try? await api.send(HTTPRequest(method: "GET", url: URL(string: "https://api.spotify.com/v1/me")!))
            log("\n== 1. devices")
            let devices = try await api.devices()
            guard let device = SpotifyPlayerAPI.pickLocalDevice(devices, machineName: Host.current().localizedName ?? ""), let deviceID = device.id else {
                log("No Computer device: open the Spotify app on this Mac."); return
            }
            log("chosen device: \(deviceID) \"\(device.name)\" active=\(device.isActive)")
            log("\n== 2. transfer")
            try await api.transfer(to: deviceID)
            try await Task.sleep(for: .milliseconds(800))
            log("\n== 3. play")
            try await api.play(testTrack, positionMs: 0, on: deviceID)
            for i in 1...5 {
                try await Task.sleep(for: .milliseconds(800))
                log("\n== 4.\(i) state")
                _ = try await api.playback()
            }
            log("\n== 5. pause")
            try await api.pause(on: deviceID)
        } catch {
            log("ERROR: \(String(reflecting: error))")
        }
        log("\nDone.")
    }
}
