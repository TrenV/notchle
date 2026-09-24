import Foundation
import Testing
@testable import NotchleCore
@testable import NotchleMac

@Suite struct SpotifyConnectSettingsRulesTests {
    typealias R = NotchUIRules

    @Test func pickerOffersThreePlayersInOrder() {
        #expect(R.playerChoices.map(\.mode) == [.spotifyApp, .spotifyConnect, .preview])
        #expect(R.playerChoices.map(\.label) == ["Spotify app", "Spotify (no window, Premium)", "30-second previews"])
    }

    @Test func connectControlsOnlyForTheConnectPlayer() {
        for mode in [PlayerMode.spotifyApp, .preview] {
            #expect(R.spotifyConnectControls(mode: mode, status: .connected(displayName: "T"), clientID: "x") == .hidden)
            #expect(R.showsSnippetsRow(R.spotifyConnectControls(mode: mode, status: nil, clientID: "")))
        }
    }

    @Test func signedOutNeedsAClientIDBeforeConnecting() {
        #expect(R.spotifyConnectControls(mode: .spotifyConnect, status: .signedOut, clientID: "  ")
                == .signIn(canConnect: false, busy: false, message: nil))
        #expect(R.spotifyConnectControls(mode: .spotifyConnect, status: nil, clientID: "abc")
                == .signIn(canConnect: true, busy: false, message: nil))
        #expect(R.spotifyConnectControls(mode: .spotifyConnect, status: .connecting, clientID: "abc")
                == .signIn(canConnect: true, busy: true, message: nil))
        #expect(R.spotifyConnectControls(mode: .spotifyConnect, status: .failed("nope"), clientID: "abc")
                == .signIn(canConnect: true, busy: false, message: "nope"))
        #expect(!R.showsSnippetsRow(.signIn(canConnect: true, busy: false, message: nil)))
    }

    @Test func connectedShowsTheNameAndSignOut() {
        #expect(R.spotifyConnectControls(mode: .spotifyConnect, status: .connected(displayName: "Tren"), clientID: "")
                == .connected(displayName: "Tren"))
    }

    @Test func helpTextNamesPremiumTheDashboardAndTheRedirect() {
        #expect(R.spotifyConnectHelp == "Needs Premium and your own app at developer.spotify.com, redirect http://127.0.0.1:43821/callback")
    }

    @Test func playerModeRawValueMatchesTheWindowsPort() throws {
        #expect(PlayerMode.spotifyConnect.rawValue == "spotifyConnect")
        let json = #"{"playerMode":"spotifyConnect","config":{"tiers":[1],"setSize":20,"snippetStart":30}}"#
        #expect(try JSONDecoder().decode(AppSettings.self, from: Data(json.utf8)).playerMode == .spotifyConnect)
    }
}

@MainActor
@Suite struct SpotifyConnectModelTests {
    func model(tokens: MemoryTokenStore = MemoryTokenStore(nil), prefs: InMemoryPreferences = InMemoryPreferences(),
               signIn: @escaping SpotifyConnectModel.SignIn) -> SpotifyConnectModel {
        let api = SpotifyWebAPI(http: FakeSpotifyWeb(), tokens: tokens, clientID: { "cid" })
        return SpotifyConnectModel(api: api, tokens: tokens, defaults: prefs, signIn: signIn)
    }

    @Test func startsConnectedWhenTokensAreStored() {
        let prefs = InMemoryPreferences([SpotifyConnectModel.displayNameKey: "Tren", SpotifyConnectModel.clientIDKey: "cid"])
        let m = model(tokens: MemoryTokenStore(), prefs: prefs) { _, _ in nil }
        #expect(m.status == .connected(displayName: "Tren"))
        #expect(m.clientID == "cid")
    }

    @Test func connectRunsTheSignInAndStoresClientIDAndName() async {
        let prefs = InMemoryPreferences()
        let seen = LockedValue<String?>(nil)
        let m = model(prefs: prefs) { id, _ in seen.set(id); return "Tren" }
        m.clientID = "  cid  "
        m.connect()
        #expect(m.status == .connecting)
        await m.waitForConnect()
        #expect(seen.get() == "cid")
        #expect(m.status == .connected(displayName: "Tren"))
        #expect(prefs.values[SpotifyConnectModel.clientIDKey] == "cid")
        #expect(prefs.values[SpotifyConnectModel.displayNameKey] == "Tren")
    }

    @Test func aFailedSignInShowsWhy() async {
        let m = model { _, _ in throw PlayerError.failed("Spotify sign-in was cancelled (access_denied)") }
        m.clientID = "cid"
        m.connect()
        await m.waitForConnect()
        #expect(m.status == .failed("Spotify sign-in was cancelled (access_denied)"))
    }

    @Test func noClientIDNeverStartsTheSignIn() {
        let m = model { _, _ in Issue.record("signed in"); return nil }
        m.connect()
        #expect(m.status == .failed("Enter your Client ID first"))
    }

    @Test func signOutForgetsTokensAndName() {
        let tokens = MemoryTokenStore()
        let prefs = InMemoryPreferences([SpotifyConnectModel.displayNameKey: "Tren"])
        let m = model(tokens: tokens, prefs: prefs) { _, _ in nil }
        m.signOut()
        #expect(tokens.load() == nil)
        #expect(prefs.values[SpotifyConnectModel.displayNameKey] == nil)
        #expect(m.status == .signedOut)
    }

    @Test func refreshNoticesARevokedSignIn() {
        let tokens = MemoryTokenStore()
        let m = model(tokens: tokens) { _, _ in nil }
        #expect(m.status == .connected(displayName: "Spotify"))
        tokens.save(nil) // what SpotifyWebAPI does on invalid_grant
        m.refresh()
        #expect(m.status == .signedOut)
    }
}

final class LockedValue<T: Sendable>: @unchecked Sendable {
    private let lock = NSLock()
    private var value: T
    init(_ value: T) { self.value = value }
    func get() -> T { lock.withLock { value } }
    func set(_ new: T) { lock.withLock { value = new } }
}

/// The real NWListener on 127.0.0.1, driven by URLSession over loopback (no Spotify involved).
@Suite struct LoopbackCallbackListenerTests {
    func get(_ url: String) async throws -> (Int, String) {
        let (data, response) = try await URLSession.shared.data(from: URL(string: url)!)
        return ((response as? HTTPURLResponse)?.statusCode ?? 0, String(decoding: data, as: UTF8.self))
    }

    @Test func catchesTheCodeAfterIgnoringOtherRequests() async throws {
        let listener = try await LoopbackCallbackListener.start(expectedState: "st")
        defer { listener.close() }
        #expect(listener.port > 0)
        let base = "http://127.0.0.1:\(listener.port)"
        let waiting = Task { try await listener.waitForCode() }
        let favicon = try await get("\(base)/favicon.ico")
        #expect(favicon.0 == 404)
        let callback = try await get("\(base)/callback?code=abc&state=st")
        #expect(callback.0 == 200)
        #expect(callback.1.contains("You can close this tab"))
        #expect(try await waiting.value == "abc")
    }

    @Test func aWrongStateFailsTheWait() async throws {
        let listener = try await LoopbackCallbackListener.start(expectedState: "st")
        defer { listener.close() }
        let answer = try await get("http://127.0.0.1:\(listener.port)/callback?code=abc&state=forged")
        #expect(answer.0 == 400)
        await #expect(throws: PlayerError.self) { try await listener.waitForCode() }
    }

    @Test func cancellingTheWaitEndsIt() async throws {
        let listener = try await LoopbackCallbackListener.start(expectedState: "st")
        defer { listener.close() }
        let waiting = Task { try await listener.waitForCode() }
        try await Task.sleep(for: .milliseconds(50))
        waiting.cancel()
        await #expect(throws: CancellationError.self) { try await waiting.value }
    }
}
