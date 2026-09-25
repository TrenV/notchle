import Foundation

/// An authorised Spotify Web API client over `HTTPRequesting`: adds the bearer token, refreshes
/// it when stale or on a 401 (once), signs out when Spotify rejects the refresh token, and maps
/// failures to `PlayerError`. An actor, so concurrent calls share one refresh.
public actor SpotifyWebAPI {
    private let http: any HTTPRequesting
    private let tokens: any SpotifyTokenStore
    private let clientID: @Sendable () -> String?
    private let now: @Sendable () -> Date
    private var refreshing: Task<SpotifyTokens, any Error>?

    public static let missingClientID = "Spotify Connect needs your Client ID. Enter it in Notchle's settings"
    public static let notSignedIn = "Not signed in to Spotify. Open settings and click Connect Spotify"

    public init(http: any HTTPRequesting, tokens: any SpotifyTokenStore,
                clientID: @escaping @Sendable () -> String?, now: @escaping @Sendable () -> Date = { Date() }) {
        self.http = http
        self.tokens = tokens
        self.clientID = clientID
        self.now = now
    }

    public var isSignedIn: Bool { tokens.load() != nil }

    // MARK: - Player endpoints

    public func devices() async throws -> [SpotifyDevice] {
        SpotifyPlayerAPI.parseDevices(try await send(SpotifyPlayerAPI.devices()).body)
    }

    public func transfer(to deviceID: String) async throws {
        _ = try await send(SpotifyPlayerAPI.transfer(deviceID: deviceID))
    }

    /// Plays the track inside its album (see `SpotifyPlayerAPI.play`). The album uri is looked up
    /// once per track and cached; if the lookup fails it falls back to a bare `uris` play.
    public func play(_ trackURI: String, positionMs: Int, on deviceID: String) async throws {
        let place = (try? await albumPlace(forTrack: trackURI)) ?? nil
        _ = try await send(SpotifyPlayerAPI.play(deviceID: deviceID, trackURI: trackURI, positionMs: positionMs,
                                                 contextURI: place?.album, albumIndex: place?.index))
    }

    public struct AlbumPlace: Sendable, Hashable { public let album: String; public let index: Int? }
    private var albumCache: [String: AlbumPlace] = [:]

    /// The track's album and its 0-based position there (GET /tracks/{id}, cached per track).
    public func albumPlace(forTrack trackURI: String) async throws -> AlbumPlace? {
        if let cached = albumCache[trackURI] { return cached }
        guard trackURI.hasPrefix("spotify:track:"), let id = trackURI.split(separator: ":").last.map(String.init) else { return nil }
        let body = try await send(SpotifyPlayerAPI.track(id: id)).body
        guard let album = SpotifyPlayerAPI.parseAlbumURI(body) else { return nil }
        let place = AlbumPlace(album: album, index: SpotifyPlayerAPI.parseAlbumIndex(body))
        albumCache[trackURI] = place
        return place
    }

    public func albumURI(forTrack trackURI: String) async throws -> String? { try await albumPlace(forTrack: trackURI)?.album }

    public func seek(positionMs: Int, on deviceID: String?) async throws {
        _ = try await send(SpotifyPlayerAPI.seek(positionMs: positionMs, deviceID: deviceID))
    }

    public func resume(on deviceID: String?) async throws {
        _ = try await send(SpotifyPlayerAPI.resume(deviceID: deviceID))
    }

    /// `timeout` bounds a best-effort pause so a dead network can't hold up the playback queue.
    public func pause(on deviceID: String?, timeout: Double? = nil) async throws {
        var request = SpotifyPlayerAPI.pause(deviceID: deviceID)
        request.timeout = timeout
        _ = try await send(request)
    }

    /// nil: nothing is playing anywhere (204).
    public func playback() async throws -> SpotifyPlayback? {
        SpotifyPlayerAPI.parsePlayback(try await send(SpotifyPlayerAPI.playbackState()).body)
    }

    public func displayName() async throws -> String? {
        SpotifyPlayerAPI.parseDisplayName(try await send(SpotifyPlayerAPI.profile()).body)
    }

    // MARK: - Sign-in

    /// Exchanges the authorization code and stores the tokens.
    public func signIn(code: String, redirectURI: String, codeVerifier: String) async throws {
        let id = try requireClientID()
        let response = try await transport(SpotifyAccounts.tokenRequest(
            clientID: id, code: code, redirectURI: redirectURI, codeVerifier: codeVerifier))
        guard response.isSuccess else { throw SpotifyAccounts.tokenError(status: response.status, body: response.body) }
        tokens.save(try SpotifyAccounts.parseTokenResponse(response.body, now: now()))
    }

    public func signOut() {
        refreshing?.cancel()
        refreshing = nil
        tokens.save(nil)
    }

    // MARK: - Internals

    /// Sends an authorised request; on 401 refreshes the token once and retries.
    public func send(_ request: HTTPRequest) async throws -> HTTPResponse {
        var token = try await accessToken(forceRefresh: false)
        var attempt = 0
        while true {
            var authorised = request
            authorised.headers["Authorization"] = "Bearer \(token)"
            let response = try await transport(authorised)
            if response.isSuccess { return response }
            if response.status == 401 && attempt == 0 {
                attempt += 1
                token = try await accessToken(forceRefresh: true)
                continue
            }
            throw SpotifyPlayerAPI.mapError(status: response.status, body: response.body,
                                            retryAfter: response.headers["retry-after"])
        }
    }

    private func requireClientID() throws -> String {
        guard let id = clientID()?.trimmingCharacters(in: .whitespacesAndNewlines), !id.isEmpty else {
            throw PlayerError.unavailable(Self.missingClientID)
        }
        return id
    }

    private func accessToken(forceRefresh: Bool) async throws -> String {
        let id = try requireClientID()
        guard let current = tokens.load() else { throw PlayerError.failed(Self.notSignedIn) }
        if !forceRefresh && current.isFresh(at: now()) { return current.accessToken }
        if let refreshing { return try await refreshing.value.accessToken }
        let task = Task { try await self.refresh(clientID: id, current) }
        refreshing = task
        defer { refreshing = nil }
        return try await task.value.accessToken
    }

    private func refresh(clientID id: String, _ current: SpotifyTokens) async throws -> SpotifyTokens {
        let response = try await transport(SpotifyAccounts.refreshRequest(clientID: id, refreshToken: current.refreshToken))
        guard response.isSuccess else {
            // A revoked refresh token never recovers: sign out, so settings show "Connect Spotify".
            if SpotifyAccounts.isRevoked(status: response.status, body: response.body) { tokens.save(nil) }
            throw SpotifyAccounts.tokenError(status: response.status, body: response.body)
        }
        let refreshed = try SpotifyAccounts.parseTokenResponse(response.body, now: now(),
                                                               previousRefreshToken: current.refreshToken)
        tokens.save(refreshed)
        return refreshed
    }

    /// Transport failures become `PlayerError.failed`; cancellation stays `CancellationError`.
    private func transport(_ request: HTTPRequest) async throws -> HTTPResponse {
        do {
            return try await http.send(request)
        } catch is CancellationError {
            throw CancellationError()
        } catch {
            if Task.isCancelled { throw CancellationError() }
            throw PlayerError.failed("Couldn't reach Spotify: \(error.localizedDescription)")
        }
    }
}

/// The loopback half of the sign-in: a listener on http://127.0.0.1:<port>/callback that
/// returns the authorization code (or throws). macOS: NotchleMac.LoopbackCallbackListener.
public protocol SpotifyCallbackListening: Sendable {
    var port: Int { get }
    func waitForCode() async throws -> String
    func close()
}

/// "Connect Spotify": Authorization Code + PKCE with a loopback redirect. The platform supplies
/// the listener (started with the `state` it must expect) and a way to open the browser.
public enum SpotifySignIn {
    /// Returns the account's display name.
    public static func run(
        clientID: String,
        api: SpotifyWebAPI,
        startListener: @Sendable (_ expectedState: String) async throws -> any SpotifyCallbackListening,
        openBrowser: @Sendable (URL) async throws -> Void
    ) async throws -> String? {
        let id = clientID.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !id.isEmpty else { throw PlayerError.unavailable(SpotifyWebAPI.missingClientID) }
        let verifier = PKCE.makeVerifier()
        let state = PKCE.makeState()
        let listener = try await startListener(state)
        defer { listener.close() }
        let redirect = SpotifyAccounts.redirectURI(port: listener.port)
        try await openBrowser(SpotifyAccounts.authorizeURL(
            clientID: id, redirectURI: redirect, codeChallenge: PKCE.challenge(for: verifier), state: state))
        let code = try await listener.waitForCode()
        try await api.signIn(code: code, redirectURI: redirect, codeVerifier: verifier)
        return try? await api.displayName()
    }
}
