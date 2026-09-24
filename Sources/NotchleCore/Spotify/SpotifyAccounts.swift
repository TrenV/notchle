import Foundation

public struct SpotifyTokens: Sendable, Hashable, Codable {
    public var accessToken: String
    public var refreshToken: String
    public var expiresAt: Date
    public var scope: String?

    public init(accessToken: String, refreshToken: String, expiresAt: Date, scope: String? = nil) {
        self.accessToken = accessToken
        self.refreshToken = refreshToken
        self.expiresAt = expiresAt
        self.scope = scope
    }

    /// Refresh a minute early, so a token can't expire in the middle of a snippet.
    public func isFresh(at now: Date) -> Bool { now < expiresAt.addingTimeInterval(-60) }
}

/// Where the Spotify tokens live. macOS: the Keychain (NotchleMac.KeychainSpotifyTokenStore).
public protocol SpotifyTokenStore: Sendable {
    func load() -> SpotifyTokens?
    /// nil signs out.
    func save(_ tokens: SpotifyTokens?)
}

/// What the loopback listener should do with one incoming request.
public enum SpotifyCallback: Sendable, Hashable {
    /// Not GET /callback (a favicon request, say): answer 404 and keep listening.
    case notCallback
    case code(String)
}

/// Authorization Code with PKCE against accounts.spotify.com: pure request building and parsing.
/// https://developer.spotify.com/documentation/web-api/tutorials/code-pkce-flow
public enum SpotifyAccounts {
    public static let scopes = "user-modify-playback-state user-read-playback-state"
    public static let authorizeEndpoint = URL(string: "https://accounts.spotify.com/authorize")!
    public static let tokenEndpoint = URL(string: "https://accounts.spotify.com/api/token")!

    /// Spotify rejects "localhost"; loopback must be an IP literal. Register
    /// "http://127.0.0.1/callback" (no port) in the developer dashboard: for loopback IP literals
    /// Spotify accepts whatever port the authorization request names.
    public static func redirectURI(port: Int) -> String { "http://127.0.0.1:\(port)/callback" }

    public static func authorizeURL(clientID: String, redirectURI: String, codeChallenge: String, state: String) -> URL {
        let query = formEncode([
            ("response_type", "code"),
            ("client_id", clientID),
            ("scope", scopes),
            ("redirect_uri", redirectURI),
            ("state", state),
            ("code_challenge_method", "S256"),
            ("code_challenge", codeChallenge),
        ])
        return URL(string: "\(authorizeEndpoint.absoluteString)?\(query)")!
    }

    public static func tokenRequest(clientID: String, code: String, redirectURI: String, codeVerifier: String) -> HTTPRequest {
        form([("grant_type", "authorization_code"), ("code", code), ("redirect_uri", redirectURI),
              ("client_id", clientID), ("code_verifier", codeVerifier)])
    }

    public static func refreshRequest(clientID: String, refreshToken: String) -> HTTPRequest {
        form([("grant_type", "refresh_token"), ("refresh_token", refreshToken), ("client_id", clientID)])
    }

    /// Parses a token response. Refresh responses may omit refresh_token: keep the old one.
    public static func parseTokenResponse(_ body: Data, now: Date, previousRefreshToken: String? = nil) throws -> SpotifyTokens {
        guard let root = (try? JSONSerialization.jsonObject(with: body)) as? [String: Any] else {
            throw PlayerError.failed("Spotify's sign-in answer couldn't be read")
        }
        let access = root["access_token"] as? String
        let refresh = (root["refresh_token"] as? String) ?? previousRefreshToken
        guard let access, !access.isEmpty, let refresh, !refresh.isEmpty else {
            throw PlayerError.failed("Spotify's sign-in answer had no token")
        }
        let expiresIn = (root["expires_in"] as? NSNumber)?.doubleValue ?? 3600
        return SpotifyTokens(accessToken: access, refreshToken: refresh,
                             expiresAt: now.addingTimeInterval(expiresIn), scope: root["scope"] as? String)
    }

    /// Whether a failed token request means the grant is dead (revoked or expired refresh
    /// token, wrong client id): then the stored tokens are useless and we sign out.
    public static func isRevoked(status: Int, body: Data) -> Bool {
        let code = SpotifyPlayerAPI.jsonObject(body)?["error"] as? String
        return code == "invalid_grant" || code == "invalid_client" || status == 400 || status == 401
    }

    public static func tokenError(status: Int, body: Data) -> PlayerError {
        let code = SpotifyPlayerAPI.jsonObject(body)?["error"] as? String
        return isRevoked(status: status, body: body)
            ? .failed("Spotify sign-in was rejected (\(code ?? String(status))). Connect Spotify again in settings")
            : .failed("Spotify sign-in failed (HTTP \(status))")
    }

    /// Reads one request line ("GET /callback?code=…&state=… HTTP/1.1") the loopback listener
    /// received. A state mismatch (CSRF) or an error such as access_denied throws.
    public static func parseCallback(requestLine: String, expectedState: String) throws -> SpotifyCallback {
        let parts = requestLine.split(separator: " ", omittingEmptySubsequences: true)
        guard parts.count >= 2, parts[0] == "GET" else { return .notCallback }
        let target = String(parts[1])
        let path = target.split(separator: "?", maxSplits: 1, omittingEmptySubsequences: false).first.map(String.init) ?? ""
        guard path == "/callback" else { return .notCallback }
        let items = URLComponents(string: "http://127.0.0.1\(target)")?.queryItems ?? []
        func value(_ name: String) -> String? { items.first { $0.name == name }?.value }
        guard value("state") == expectedState else {
            throw PlayerError.failed("Spotify's sign-in answer didn't match the request. Try Connect Spotify again")
        }
        if let error = value("error") {
            throw PlayerError.failed("Spotify sign-in was cancelled (\(error))")
        }
        guard let code = value("code"), !code.isEmpty else {
            throw PlayerError.failed("Spotify's sign-in answer had no code")
        }
        return .code(code)
    }

    // MARK: - Encoding

    /// RFC 3986 unreserved characters stay; everything else is percent-encoded (like .NET's
    /// Uri.EscapeDataString), so a space is %20 and ":" "/" are encoded too.
    public static func percentEncode(_ value: String) -> String {
        var allowed = CharacterSet(charactersIn: "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789")
        allowed.insert(charactersIn: "-._~")
        return value.addingPercentEncoding(withAllowedCharacters: allowed) ?? value
    }

    public static func formEncode(_ fields: [(String, String)]) -> String {
        fields.map { "\($0.0)=\(percentEncode($0.1))" }.joined(separator: "&")
    }

    private static func form(_ fields: [(String, String)]) -> HTTPRequest {
        HTTPRequest(method: "POST", url: tokenEndpoint,
                    headers: ["Content-Type": "application/x-www-form-urlencoded"],
                    body: Data(formEncode(fields).utf8))
    }
}
