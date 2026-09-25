import Foundation
import Testing
@testable import NotchleCore

final class MemoryTokenStore: SpotifyTokenStore, @unchecked Sendable {
    private let lock = NSLock()
    private var tokens: SpotifyTokens?
    private(set) var saves = 0
    init(_ tokens: SpotifyTokens? = nil) { self.tokens = tokens }
    func load() -> SpotifyTokens? { lock.lock(); defer { lock.unlock() }; return tokens }
    func save(_ tokens: SpotifyTokens?) { lock.lock(); self.tokens = tokens; saves += 1; lock.unlock() }
}

/// Answers from a closure and records every request.
final class ScriptedHTTP: HTTPRequesting, @unchecked Sendable {
    private let lock = NSLock()
    private var log: [HTTPRequest] = []
    private let answer: @Sendable (HTTPRequest, Int) async throws -> HTTPResponse
    init(_ answer: @escaping @Sendable (HTTPRequest, Int) async throws -> HTTPResponse) { self.answer = answer }
    var requests: [HTTPRequest] { lock.lock(); defer { lock.unlock() }; return log }
    func send(_ request: HTTPRequest) async throws -> HTTPResponse {
        let n = lock.withLock { log.append(request); return log.count - 1 }
        return try await answer(request, n)
    }
}

let tokenJSON = #"{"access_token":"new-access","token_type":"Bearer","expires_in":3600,"refresh_token":"new-refresh","scope":"user-modify-playback-state user-read-playback-state"}"#

@Suite struct SpotifyPKCETests {
    @Test func sha256KnownVectors() {
        func hex(_ s: String) -> String { SHA256.hash(Array(s.utf8)).map { String(format: "%02x", $0) }.joined() }
        #expect(hex("") == "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")
        #expect(hex("abc") == "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")
        // Two blocks (56 bytes pushes the length into a second block).
        #expect(hex("abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq")
                == "248d6a61d20638b8e5c026930c3e6039a33ce45964ff2167f6ecedd419db06c1")
    }

    @Test func rfc7636AppendixBVector() {
        let octets: [UInt8] = [116, 24, 223, 180, 151, 153, 224, 37, 79, 250, 96, 125, 216, 173, 187, 186,
                               22, 212, 37, 77, 105, 214, 191, 240, 91, 88, 5, 88, 83, 132, 141, 121]
        #expect(PKCE.base64URL(octets) == "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk")
        #expect(PKCE.challenge(for: "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk")
                == "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM")
    }

    @Test func verifierIs86UnreservedCharactersAndRandom() {
        let a = PKCE.makeVerifier(), b = PKCE.makeVerifier()
        #expect(a.count == 86)
        #expect(a.allSatisfy { $0.isLetter || $0.isNumber || $0 == "-" || $0 == "_" })
        #expect(a != b)
        #expect(PKCE.makeState().count == 22)
    }
}

@Suite struct SpotifyAccountsTests {
    @Test func authorizeURLHasEveryParameterEncoded() {
        let url = SpotifyAccounts.authorizeURL(clientID: "cid", redirectURI: "http://127.0.0.1:50123/callback",
                                               codeChallenge: "chal-_", state: "st")
        #expect(url.absoluteString == "https://accounts.spotify.com/authorize?response_type=code&client_id=cid"
            + "&scope=user-modify-playback-state%20user-read-playback-state"
            + "&redirect_uri=http%3A%2F%2F127.0.0.1%3A50123%2Fcallback&state=st"
            + "&code_challenge_method=S256&code_challenge=chal-_")
    }

    @Test func redirectURIIsTheLoopbackIPLiteral() {
        #expect(SpotifyAccounts.redirectURI(port: 50123) == "http://127.0.0.1:50123/callback")
    }

    @Test func tokenAndRefreshRequestsAreFormPosts() {
        let token = SpotifyAccounts.tokenRequest(clientID: "cid", code: "c/1", redirectURI: "http://127.0.0.1:5/callback", codeVerifier: "v")
        #expect(token.method == "POST")
        #expect(token.url.absoluteString == "https://accounts.spotify.com/api/token")
        #expect(token.headers["Content-Type"] == "application/x-www-form-urlencoded")
        #expect(token.bodyText == "grant_type=authorization_code&code=c%2F1&redirect_uri=http%3A%2F%2F127.0.0.1%3A5%2Fcallback&client_id=cid&code_verifier=v")
        let refresh = SpotifyAccounts.refreshRequest(clientID: "cid", refreshToken: "r")
        #expect(refresh.bodyText == "grant_type=refresh_token&refresh_token=r&client_id=cid")
    }

    @Test func parsesTokensAndKeepsTheOldRefreshToken() throws {
        let now = Date(timeIntervalSince1970: 1000)
        let t = try SpotifyAccounts.parseTokenResponse(Data(tokenJSON.utf8), now: now)
        #expect(t == SpotifyTokens(accessToken: "new-access", refreshToken: "new-refresh",
                                   expiresAt: now.addingTimeInterval(3600),
                                   scope: "user-modify-playback-state user-read-playback-state"))
        let refreshed = try SpotifyAccounts.parseTokenResponse(Data(#"{"access_token":"a2","expires_in":60}"#.utf8),
                                                               now: now, previousRefreshToken: "old")
        #expect(refreshed.refreshToken == "old")
        #expect(refreshed.expiresAt == now.addingTimeInterval(60))
        #expect(t.isFresh(at: now.addingTimeInterval(3539)))
        #expect(!t.isFresh(at: now.addingTimeInterval(3541)))
    }

    @Test func unreadableOrTokenlessAnswersThrow() {
        #expect(throws: PlayerError.failed("Spotify's sign-in answer couldn't be read")) {
            try SpotifyAccounts.parseTokenResponse(Data("<html>".utf8), now: Date())
        }
        #expect(throws: PlayerError.failed("Spotify's sign-in answer had no token")) {
            try SpotifyAccounts.parseTokenResponse(Data(#"{"access_token":"a"}"#.utf8), now: Date())
        }
    }

    @Test func tokenErrors() {
        #expect(SpotifyAccounts.isRevoked(status: 400, body: Data(#"{"error":"invalid_grant"}"#.utf8)))
        #expect(!SpotifyAccounts.isRevoked(status: 503, body: Data()))
        #expect(SpotifyAccounts.tokenError(status: 503, body: Data()) == .failed("Spotify sign-in failed (HTTP 503)"))
    }

    @Test func callbackParser() throws {
        let s = "xyz"
        #expect(try SpotifyAccounts.parseCallback(requestLine: "GET /callback?code=AQ%2Fb&state=xyz HTTP/1.1", expectedState: s) == .code("AQ/b"))
        #expect(try SpotifyAccounts.parseCallback(requestLine: "GET /favicon.ico HTTP/1.1", expectedState: s) == .notCallback)
        #expect(try SpotifyAccounts.parseCallback(requestLine: "POST /callback?code=a&state=xyz HTTP/1.1", expectedState: s) == .notCallback)
        #expect(try SpotifyAccounts.parseCallback(requestLine: "GET /callbackx?code=a&state=xyz HTTP/1.1", expectedState: s) == .notCallback)
        #expect(try SpotifyAccounts.parseCallback(requestLine: "", expectedState: s) == .notCallback)
        #expect(throws: PlayerError.failed("Spotify's sign-in answer didn't match the request. Try Connect Spotify again")) {
            try SpotifyAccounts.parseCallback(requestLine: "GET /callback?code=a&state=evil HTTP/1.1", expectedState: s)
        }
        #expect(throws: PlayerError.failed("Spotify sign-in was cancelled (access_denied)")) {
            try SpotifyAccounts.parseCallback(requestLine: "GET /callback?error=access_denied&state=xyz HTTP/1.1", expectedState: s)
        }
        #expect(throws: PlayerError.failed("Spotify's sign-in answer had no code")) {
            try SpotifyAccounts.parseCallback(requestLine: "GET /callback?state=xyz HTTP/1.1", expectedState: s)
        }
    }
}

@Suite struct SpotifyPlayerAPITests {
    @Test func requests() {
        let play = SpotifyPlayerAPI.play(deviceID: "dev 1", trackURI: "spotify:track:x", positionMs: 1500)
        #expect(play.method == "PUT")
        #expect(play.url.absoluteString == "https://api.spotify.com/v1/me/player/play?device_id=dev%201")
        #expect(play.bodyText == #"{"position_ms":1500,"uris":["spotify:track:x"]}"#)
        #expect(SpotifyPlayerAPI.transfer(deviceID: "d").bodyText == #"{"device_ids":["d"],"play":false}"#)
        #expect(SpotifyPlayerAPI.pause(deviceID: "d").url.absoluteString == "https://api.spotify.com/v1/me/player/pause?device_id=d")
        #expect(SpotifyPlayerAPI.pause(deviceID: nil).url.absoluteString == "https://api.spotify.com/v1/me/player/pause")
        #expect(SpotifyPlayerAPI.seek(positionMs: -5, deviceID: "d").url.absoluteString
                == "https://api.spotify.com/v1/me/player/seek?position_ms=0&device_id=d")
        #expect(SpotifyPlayerAPI.resume(deviceID: nil).body == Data())
    }

    @Test func picksThisMacThenTheFirstComputer() {
        let devices = SpotifyPlayerAPI.parseDevices(Data("""
            {"devices":[
              {"id":"phone","name":"Tren's MacBook Pro","type":"Smartphone","is_active":true},
              {"id":"other","name":"Studio Mac","type":"Computer","is_active":false},
              {"id":"me","name":"Tren's MacBook Pro","type":"Computer","is_active":false},
              {"id":null,"name":"Web Player","type":"Computer"},
              {"id":"locked","name":"Tren's MacBook Pro","type":"Computer","is_restricted":true}
            ]}
            """.utf8))
        #expect(devices.count == 5)
        #expect(SpotifyPlayerAPI.pickLocalDevice(devices, machineName: "tren's macbook pro")?.id == "me")
        #expect(SpotifyPlayerAPI.pickLocalDevice(devices, machineName: "Elsewhere")?.id == "other")
        #expect(SpotifyPlayerAPI.pickLocalDevice(Array(devices.prefix(1)), machineName: "x") == nil)
        #expect(SpotifyPlayerAPI.parseDevices(Data()).isEmpty)
    }

    @Test func parsesPlaybackAndTheEmpty204() {
        let p = SpotifyPlayerAPI.parsePlayback(Data("""
            {"is_playing":true,"progress_ms":12345,"currently_playing_type":"track",
             "item":{"uri":"spotify:track:x","name":"Some Title"},"device":{"id":"me"}}
            """.utf8))
        #expect(p == SpotifyPlayback(isPlaying: true, progressMs: 12345, itemURI: "spotify:track:x",
                                     currentlyPlayingType: "track", deviceID: "me",
                                     itemName: "Some Title"))   // only compared with the expected title, never shown
        #expect(p?.position == 12.345)
        #expect(SpotifyPlayerAPI.parsePlayback(Data()) == nil)
        #expect(SpotifyPlayerAPI.parseDisplayName(Data(#"{"display_name":"Tren","id":"t"}"#.utf8)) == "Tren")
        #expect(SpotifyPlayerAPI.parseDisplayName(Data(#"{"display_name":null,"id":"t"}"#.utf8)) == "t")
    }

    @Test func errorMapping() {
        func map(_ status: Int, _ body: String = "", retry: String? = nil) -> PlayerError {
            SpotifyPlayerAPI.mapError(status: status, body: Data(body.utf8), retryAfter: retry)
        }
        #expect(map(401) == .failed("Spotify sign-in expired. Connect Spotify again in settings"))
        #expect(map(403, #"{"error":{"status":403,"message":"Player command failed: Premium required","reason":"PREMIUM_REQUIRED"}}"#)
                == .unavailable("Spotify Connect needs Spotify Premium"))
        #expect(map(403, #"{"error":{"status":403,"message":"User not registered"}}"#)
                == .unavailable("Spotify refused playback control (User not registered). It needs Premium, and your account added under User Management in your Spotify developer app"))
        #expect(map(404, #"{"error":{"status":404,"message":"Device not found"}}"#)
                == .unavailable("Open the Spotify app on this Mac (it can stay hidden)"))
        #expect(map(429, retry: "2.2") == .failed("Spotify is rate-limiting Notchle. Try again in 3 s."))
        #expect(map(429) == .failed("Spotify is rate-limiting Notchle. Try again in a minute."))
        #expect(map(500, #"{"error":{"message":"boom"}}"#) == .failed("Spotify returned HTTP 500: boom"))
    }
}

@Suite struct SpotifyWebAPITests {
    static let now = Date(timeIntervalSince1970: 10_000)
    static let fresh = SpotifyTokens(accessToken: "old-access", refreshToken: "old-refresh", expiresAt: now.addingTimeInterval(3600))
    static let stale = SpotifyTokens(accessToken: "old-access", refreshToken: "old-refresh", expiresAt: now.addingTimeInterval(30))

    func api(_ http: ScriptedHTTP, _ store: MemoryTokenStore, clientID: String? = "cid") -> SpotifyWebAPI {
        SpotifyWebAPI(http: http, tokens: store, clientID: { clientID }, now: { Self.now })
    }

    static func isToken(_ r: HTTPRequest) -> Bool { r.url.host == "accounts.spotify.com" }

    @Test func sendsTheBearerToken() async throws {
        let http = ScriptedHTTP { _, _ in HTTPResponse(status: 200, text: #"{"devices":[]}"#) }
        _ = try await api(http, MemoryTokenStore(Self.fresh)).devices()
        #expect(http.requests.map(\.url.path) == ["/v1/me/player/devices"])
        #expect(http.requests[0].headers["Authorization"] == "Bearer old-access")
    }

    @Test func refreshesAStaleTokenFirst() async throws {
        let store = MemoryTokenStore(Self.stale)
        let http = ScriptedHTTP { r, _ in
            Self.isToken(r) ? HTTPResponse(status: 200, text: #"{"access_token":"new-access","expires_in":3600}"#)
                            : HTTPResponse(status: 200, text: #"{"devices":[]}"#)
        }
        _ = try await api(http, store).devices()
        #expect(http.requests.count == 2)
        #expect(http.requests[0].bodyText == "grant_type=refresh_token&refresh_token=old-refresh&client_id=cid")
        #expect(http.requests[1].headers["Authorization"] == "Bearer new-access")
        #expect(store.load()?.refreshToken == "old-refresh")
        #expect(store.load()?.accessToken == "new-access")
    }

    @Test func a401RefreshesOnceAndRetries() async throws {
        let http = ScriptedHTTP { r, n in
            if Self.isToken(r) { return HTTPResponse(status: 200, text: tokenJSON) }
            return n == 0 ? HTTPResponse(status: 401, text: #"{"error":{"status":401,"message":"The access token expired"}}"#)
                          : HTTPResponse(status: 204)
        }
        try await api(http, MemoryTokenStore(Self.fresh)).pause(on: "d")
        #expect(http.requests.map { Self.isToken($0) } == [false, true, false])
        #expect(http.requests[2].headers["Authorization"] == "Bearer new-access")
    }

    @Test func aSecond401IsSignInExpired() async {
        let http = ScriptedHTTP { r, _ in
            Self.isToken(r) ? HTTPResponse(status: 200, text: tokenJSON) : HTTPResponse(status: 401)
        }
        await #expect(throws: PlayerError.failed("Spotify sign-in expired. Connect Spotify again in settings")) {
            try await api(http, MemoryTokenStore(Self.fresh)).pause(on: "d")
        }
        #expect(http.requests.count == 3)
    }

    @Test func aRevokedRefreshTokenSignsOut() async {
        let store = MemoryTokenStore(Self.stale)
        let http = ScriptedHTTP { _, _ in
            HTTPResponse(status: 400, text: #"{"error":"invalid_grant","error_description":"Refresh token revoked"}"#)
        }
        await #expect(throws: PlayerError.failed("Spotify sign-in was rejected (invalid_grant). Connect Spotify again in settings")) {
            try await api(http, store).devices()
        }
        #expect(store.load() == nil)
    }

    @Test func aServerErrorOnRefreshKeepsTheTokens() async {
        let store = MemoryTokenStore(Self.stale)
        let http = ScriptedHTTP { _, _ in HTTPResponse(status: 503) }
        await #expect(throws: PlayerError.failed("Spotify sign-in failed (HTTP 503)")) {
            try await api(http, store).devices()
        }
        #expect(store.load() == Self.stale)
    }

    @Test func concurrentCallsShareOneRefresh() async throws {
        let http = ScriptedHTTP { r, _ in
            if Self.isToken(r) {
                try await Task.sleep(for: .milliseconds(50))
                return HTTPResponse(status: 200, text: tokenJSON)
            }
            return HTTPResponse(status: 200, text: #"{"devices":[]}"#)
        }
        let client = api(http, MemoryTokenStore(Self.stale))
        async let a = client.devices()
        async let b = client.devices()
        _ = try await (a, b)
        #expect(http.requests.filter(Self.isToken).count == 1)
    }

    @Test func missingClientIDOrTokens() async {
        let http = ScriptedHTTP { _, _ in HTTPResponse(status: 200) }
        await #expect(throws: PlayerError.unavailable(SpotifyWebAPI.missingClientID)) {
            try await api(http, MemoryTokenStore(Self.fresh), clientID: "  ").devices()
        }
        await #expect(throws: PlayerError.failed(SpotifyWebAPI.notSignedIn)) {
            try await api(http, MemoryTokenStore(nil)).devices()
        }
        #expect(http.requests.isEmpty)
    }

    @Test func transportFailuresAndCancellation() async {
        let http = ScriptedHTTP { _, _ in throw URLError(.notConnectedToInternet) }
        await #expect(throws: PlayerError.self) { try await api(http, MemoryTokenStore(Self.fresh)).devices() }
        let cancelling = ScriptedHTTP { _, _ in throw CancellationError() }
        await #expect(throws: CancellationError.self) { try await api(cancelling, MemoryTokenStore(Self.fresh)).devices() }
    }
}

/// A listener that "receives" whatever the test hands it.
final class FakeCallbackListener: SpotifyCallbackListening, @unchecked Sendable {
    let port = 51234
    let expectedState: String
    let redirectTarget: @Sendable (String) -> String
    private(set) var closed = false
    init(expectedState: String, redirectTarget: @escaping @Sendable (String) -> String) {
        self.expectedState = expectedState
        self.redirectTarget = redirectTarget
    }
    func waitForCode() async throws -> String {
        let line = "GET \(redirectTarget(expectedState)) HTTP/1.1"
        guard case .code(let code) = try SpotifyAccounts.parseCallback(requestLine: line, expectedState: expectedState) else {
            throw PlayerError.failed("not a callback")
        }
        return code
    }
    func close() { closed = true }
}

@Suite struct SpotifySignInTests {
    @Test func fullFlowExchangesTheCodeWithTheMatchingVerifier() async throws {
        let store = MemoryTokenStore()
        let http = ScriptedHTTP { r, _ in
            r.url.host == "accounts.spotify.com"
                ? HTTPResponse(status: 200, text: tokenJSON)
                : HTTPResponse(status: 200, text: #"{"display_name":"Tren"}"#)
        }
        let api = SpotifyWebAPI(http: http, tokens: store, clientID: { "cid" })
        let opened = LockedBox<URL?>(nil)
        let listener = LockedBox<FakeCallbackListener?>(nil)
        let name = try await SpotifySignIn.run(
            clientID: " cid ", api: api,
            startListener: { state in
                let l = FakeCallbackListener(expectedState: state) { "/callback?code=the-code&state=\($0)" }
                listener.value = l
                return l
            },
            openBrowser: { opened.value = $0 })

        #expect(name == "Tren")
        #expect(store.load()?.accessToken == "new-access")
        #expect(listener.value?.closed == true)
        let query = URLComponents(url: try #require(opened.value), resolvingAgainstBaseURL: false)?.queryItems ?? []
        func q(_ n: String) -> String? { query.first { $0.name == n }?.value }
        #expect(q("client_id") == "cid")
        #expect(q("redirect_uri") == "http://127.0.0.1:51234/callback")
        #expect(q("state") == listener.value?.expectedState)
        // The token request carries the verifier whose S256 is the challenge we sent.
        let form = URLComponents(string: "x:?" + http.requests[0].bodyText)?.queryItems ?? []
        let verifier = try #require(form.first { $0.name == "code_verifier" }?.value)
        #expect(PKCE.challenge(for: verifier) == q("code_challenge"))
        #expect(form.first { $0.name == "code" }?.value == "the-code")
        #expect(form.first { $0.name == "redirect_uri" }?.value == "http://127.0.0.1:51234/callback")
        #expect(http.requests.map(\.url.path) == ["/api/token", "/v1/me"])
    }

    @Test func aForgedStateNeverReachesTheTokenEndpoint() async {
        let store = MemoryTokenStore()
        let http = ScriptedHTTP { _, _ in HTTPResponse(status: 200, text: tokenJSON) }
        let api = SpotifyWebAPI(http: http, tokens: store, clientID: { "cid" })
        await #expect(throws: PlayerError.self) {
            _ = try await SpotifySignIn.run(
                clientID: "cid", api: api,
                startListener: { state in FakeCallbackListener(expectedState: state) { _ in "/callback?code=x&state=forged" } },
                openBrowser: { _ in })
        }
        #expect(http.requests.isEmpty)
        #expect(store.load() == nil)
    }

    @Test func emptyClientIDFailsBeforeAnything() async {
        let api = SpotifyWebAPI(http: ScriptedHTTP { _, _ in HTTPResponse(status: 200) }, tokens: MemoryTokenStore(), clientID: { nil })
        await #expect(throws: PlayerError.unavailable(SpotifyWebAPI.missingClientID)) {
            _ = try await SpotifySignIn.run(clientID: " ", api: api,
                                            startListener: { _ in Issue.record("started"); throw CancellationError() },
                                            openBrowser: { _ in })
        }
    }
}

final class LockedBox<T>: @unchecked Sendable {
    private let lock = NSLock()
    private var _value: T
    init(_ value: T) { _value = value }
    var value: T {
        get { lock.lock(); defer { lock.unlock() }; return _value }
        set { lock.lock(); _value = newValue; lock.unlock() }
    }
}

/// Spotify can play a relinked version for the account's market: it reports that version's uri
/// in `item.uri` and the requested one in `item.linked_from.uri`. That must count as the track.
@Test func relinkedTrackCountsAsTheRequestedOne() throws {
    let body = Data("""
    {"is_playing":true,"progress_ms":1200,"currently_playing_type":"track",
     "device":{"id":"mac"},
     "item":{"uri":"spotify:track:RELINKED","linked_from":{"uri":"spotify:track:REQUESTED"}}}
    """.utf8)
    let playback = try #require(SpotifyPlayerAPI.parsePlayback(body))
    #expect(playback.linkedFromURI == "spotify:track:REQUESTED")
    #expect(playback.isPlayingItem("spotify:track:REQUESTED"))
    #expect(playback.isPlayingItem("spotify:track:RELINKED"))
    #expect(!playback.isPlayingItem("spotify:track:OTHER"))
    #expect(playback.summary.contains("linked_from=spotify:track:REQUESTED"))
}

/// Tren's second live run: Spotify started elsewhere in the album when the offset was the
/// playlist's track uri. The offset is now the track's position in its album (disc 1).
@Test func albumIndexComesFromTrackNumberOnDiscOne() {
    #expect(SpotifyPlayerAPI.parseAlbumIndex(Data(#"{"track_number":4,"disc_number":1,"album":{"uri":"spotify:album:A"}}"#.utf8)) == 3)
    #expect(SpotifyPlayerAPI.parseAlbumIndex(Data(#"{"track_number":4}"#.utf8)) == 3)
    #expect(SpotifyPlayerAPI.parseAlbumIndex(Data(#"{"track_number":4,"disc_number":2}"#.utf8)) == nil)
    #expect(SpotifyPlayerAPI.parseAlbumIndex(Data(#"{"album":{}}"#.utf8)) == nil)
}

@Test func playWithAnAlbumIndexUsesAPositionOffset() {
    let r = SpotifyPlayerAPI.play(deviceID: "mac", trackURI: "spotify:track:T", positionMs: 5000,
                                  contextURI: "spotify:album:A", albumIndex: 3)
    #expect(String(data: r.body ?? Data(), encoding: .utf8) == #"{"context_uri":"spotify:album:A","offset":{"position":3},"position_ms":5000}"#)
}

@Test func aSameTitledSubstituteCountsAsTheTrack() throws {
    let body = Data(#"{"is_playing":true,"progress_ms":10,"item":{"uri":"spotify:track:OTHERID","name":"Blinding Lights"}}"#.utf8)
    let p = try #require(SpotifyPlayerAPI.parsePlayback(body))
    #expect(p.isPlaying("spotify:track:REQUESTED", title: "blinding lights"))
    #expect(!p.isPlaying("spotify:track:REQUESTED", title: "Save Your Tears"))
}
