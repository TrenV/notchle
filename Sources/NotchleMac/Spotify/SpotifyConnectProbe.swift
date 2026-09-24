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
            func state(_ label: String) async {
                try? await Task.sleep(for: .milliseconds(1500))
                log("\n== \(label): state after 1.5 s")
                _ = try? await api.playback()
            }
            func raw(_ method: String, _ path: String, _ json: String?) async {
                var r = HTTPRequest(method: method, url: URL(string: "https://api.spotify.com/v1/" + path)!)
                if let json { r.body = Data(json.utf8); r.headers["Content-Type"] = "application/json" }
                _ = try? await api.send(r)
            }
            log("\n== B. play without device_id")
            await raw("PUT", "me/player/play", #"{"uris":["\#(testTrack)"],"position_ms":0}"#)
            await state("B")
            log("\n== C. transfer with play:true, then play")
            await raw("PUT", "me/player", #"{"device_ids":["\#(deviceID)"],"play":true}"#)
            try? await Task.sleep(for: .milliseconds(800))
            await raw("PUT", "me/player/play?device_id=\(deviceID)", #"{"uris":["\#(testTrack)"]}"#)
            await state("C")
            log("\n== D. local play in the app (AppleScript resume), then remote play")
            let p = Process(); p.executableURL = URL(fileURLWithPath: "/usr/bin/osascript")
            p.arguments = ["-e", "tell application \"Spotify\" to play"]; try? p.run(); p.waitUntilExit()
            await state("D-local")
            await raw("PUT", "me/player/play?device_id=\(deviceID)", #"{"uris":["\#(testTrack)"]}"#)
            await state("D-remote")
            log("\n== 5. pause")
            await raw("PUT", "me/player/pause", nil)
            let q = Process(); q.executableURL = URL(fileURLWithPath: "/usr/bin/osascript")
            q.arguments = ["-e", "tell application \"Spotify\" to pause"]; try? q.run(); q.waitUntilExit()
        } catch {
            log("ERROR: \(String(reflecting: error))")
        }
        log("\nDone.")
    }

    /// `--probe-spotify-connect-live`: with something already playing (started from the phone),
    /// checks which commands Spotify honours for this app: read state, pause, resume, play a uri.
    public static func runLive() async {
        try? FileManager.default.removeItem(at: logURL)
        log("Notchle Spotify Connect LIVE probe, \(Date())")
        let api = SpotifyWebAPI(http: LoggingRequester(), tokens: KeychainSpotifyTokenStore(),
                                clientID: { UserDefaults.standard.string(forKey: "spotifyClientID") })
        func raw(_ method: String, _ path: String, _ json: String? = nil) async {
            var r = HTTPRequest(method: method, url: URL(string: "https://api.spotify.com/v1/" + path)!)
            if let json { r.body = Data(json.utf8); r.headers["Content-Type"] = "application/json" }
            _ = try? await api.send(r)
        }
        func state(_ label: String) async {
            try? await Task.sleep(for: .milliseconds(1500))
            log("\n== \(label)")
            await raw("GET", "me/player?market=from_token")
        }
        await state("1. state while the phone's song plays")
        log("\n== 2. pause"); await raw("PUT", "me/player/pause")
        await state("2b. state after pause")
        log("\n== 3. resume (no body)"); await raw("PUT", "me/player/play")
        await state("3b. state after resume")
        log("\n== 4. seek to 30 s"); await raw("PUT", "me/player/seek?position_ms=30000")
        await state("4b. state after seek")
        log("\n== 5. play a specific uri"); await raw("PUT", "me/player/play", #"{"uris":["\#(testTrack)"]}"#)
        await state("5b. state after play uri")
        log("\n== 6. add to queue + next"); await raw("POST", "me/player/queue?uri=\(testTrack)")
        await raw("POST", "me/player/next")
        await state("6b. state after queue+next")
        log("\n== 7. pause"); await raw("PUT", "me/player/pause")
        log("\nDone.")
    }

    /// `--probe-spotify-requests <file>`: runs each line "METHOD path [json]" (or "sleep ms")
    /// against the Web API with the saved sign-in and logs Spotify's raw answers. Tokens are
    /// never logged. For quick experiments without a rebuild.
    public static func runScript(_ path: String) async {
        try? FileManager.default.removeItem(at: logURL)
        log("Notchle Spotify request script \(path), \(Date())")
        let api = SpotifyWebAPI(http: LoggingRequester(), tokens: KeychainSpotifyTokenStore(),
                                clientID: { UserDefaults.standard.string(forKey: "spotifyClientID") })
        let lines = ((try? String(contentsOfFile: path, encoding: .utf8)) ?? "").split(separator: "\n")
        for line in lines where !line.trimmingCharacters(in: .whitespaces).isEmpty && !line.hasPrefix("#") {
            let parts = line.split(separator: " ", maxSplits: 2).map(String.init)
            if parts[0] == "sleep", parts.count > 1, let ms = Int(parts[1]) {
                try? await Task.sleep(for: .milliseconds(ms)); continue
            }
            guard parts.count >= 2, let url = URL(string: "https://api.spotify.com/v1/" + parts[1]) else { continue }
            var r = HTTPRequest(method: parts[0], url: url)
            if parts.count == 3 { r.body = Data(parts[2].utf8); r.headers["Content-Type"] = "application/json" }
            log("")
            _ = try? await api.send(r)
        }
        log("\nDone.")
    }
}

