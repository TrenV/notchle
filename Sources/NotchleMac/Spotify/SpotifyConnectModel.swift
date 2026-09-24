import AppKit
import Observation
import NotchleCore

/// The two strings the sign-in keeps outside the Keychain. `UserDefaults` in the app.
public protocol SpotifyConnectPreferences: AnyObject {
    func string(forKey key: String) -> String?
    func set(_ value: Any?, forKey key: String)
    func removeObject(forKey key: String)
}

extension UserDefaults: SpotifyConnectPreferences {}

/// For tests and snapshots: nothing touches the real defaults.
final class InMemoryPreferences: SpotifyConnectPreferences {
    var values: [String: String] = [:]
    init(_ values: [String: String] = [:]) { self.values = values }
    func string(forKey key: String) -> String? { values[key] }
    func set(_ value: Any?, forKey key: String) { values[key] = value as? String }
    func removeObject(forKey key: String) { values[key] = nil }
}

public enum SpotifyConnectStatus: Sendable, Hashable {
    case signedOut
    case connecting
    case connected(displayName: String)
    /// The last sign-in failed; the text says why.
    case failed(String)
}

/// Spotify Connect sign-in state for the settings view. The client id lives in UserDefaults
/// (key `spotifyClientID`; `AppSettings` is a frozen contract without such a field), the tokens
/// in the Keychain (`KeychainSpotifyTokenStore`), the display name in UserDefaults
/// (`spotifyDisplayName`, only for "Connected as …").
@MainActor
@Observable
public final class SpotifyConnectModel {
    nonisolated public static let clientIDKey = "spotifyClientID"
    nonisolated public static let displayNameKey = "spotifyDisplayName"

    public typealias SignIn = @Sendable (_ clientID: String, _ api: SpotifyWebAPI) async throws -> String?

    public var clientID: String {
        didSet { defaults.set(clientID.trimmingCharacters(in: .whitespacesAndNewlines), forKey: Self.clientIDKey) }
    }
    public private(set) var status: SpotifyConnectStatus = .signedOut

    public let api: SpotifyWebAPI
    @ObservationIgnored private let tokens: any SpotifyTokenStore
    @ObservationIgnored private let defaults: any SpotifyConnectPreferences
    @ObservationIgnored private let signIn: SignIn
    @ObservationIgnored private var task: Task<Void, Never>?

    public init(api: SpotifyWebAPI, tokens: any SpotifyTokenStore, defaults: any SpotifyConnectPreferences, signIn: @escaping SignIn) {
        self.api = api
        self.tokens = tokens
        self.defaults = defaults
        self.signIn = signIn
        self.clientID = defaults.string(forKey: Self.clientIDKey) ?? ""
        refresh()
    }

    /// The real thing: Keychain, UserDefaults.standard, the browser and a loopback listener.
    public static func live() -> SpotifyConnectModel {
        let tokens = KeychainSpotifyTokenStore()
        let api = SpotifyWebAPI(http: URLSessionHTTPRequester(), tokens: tokens,
                                clientID: { UserDefaults.standard.string(forKey: clientIDKey) })
        return SpotifyConnectModel(api: api, tokens: tokens, defaults: UserDefaults.standard) { clientID, api in
            try await SpotifySignIn.run(
                clientID: clientID, api: api,
                startListener: { state in try await LoopbackCallbackListener.start(expectedState: state) },
                openBrowser: { url in
                    let opened = await MainActor.run { NSWorkspace.shared.open(url) }
                    if !opened { throw PlayerError.failed("Couldn't open the browser for the Spotify sign-in") }
                })
        }
    }

    /// Re-reads the Keychain (a revoked refresh token signs out behind our back).
    public func refresh() {
        guard status != .connecting else { return }
        if tokens.load() != nil {
            status = .connected(displayName: defaults.string(forKey: Self.displayNameKey) ?? "Spotify")
        } else if case .failed = status {
            // Keep the error visible until the next attempt.
        } else {
            status = .signedOut
        }
    }

    /// Runs the browser sign-in. A second click while connecting cancels the first attempt.
    public func connect() {
        task?.cancel()
        let id = clientID.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !id.isEmpty else {
            status = .failed("Enter your Client ID first")
            return
        }
        defaults.set(id, forKey: Self.clientIDKey)
        status = .connecting
        let signIn = self.signIn
        let api = self.api
        task = Task {
            do {
                let name = try await signIn(id, api) ?? "Spotify"
                guard !Task.isCancelled else { return }
                self.defaults.set(name, forKey: Self.displayNameKey)
                self.status = .connected(displayName: name)
            } catch {
                guard !Task.isCancelled else { return }
                self.status = .failed(Self.describe(error))
            }
        }
    }

    public func cancelConnect() {
        task?.cancel()
        task = nil
        if status == .connecting { status = .signedOut }
    }

    public func signOut() {
        task?.cancel()
        task = nil
        defaults.removeObject(forKey: Self.displayNameKey)
        status = .signedOut
        let api = self.api
        Task { await api.signOut() }
        tokens.save(nil)
    }

    /// A model frozen in `status`, for UI snapshots (no Keychain, no network, no defaults).
    static func snapshot(status: SpotifyConnectStatus, clientID: String) -> SpotifyConnectModel {
        var signedIn = false
        var prefs: [String: String] = [:]
        if case .connected(let name) = status { signedIn = true; prefs[displayNameKey] = name }
        let tokens = SnapshotTokenStore(signedIn: signedIn)
        let api = SpotifyWebAPI(http: NoHTTP(), tokens: tokens, clientID: { nil })
        let model = SpotifyConnectModel(api: api, tokens: tokens,
                                        defaults: InMemoryPreferences(prefs),
                                        signIn: { _, _ in nil })
        model.status = status
        model.clientID = clientID
        return model
    }

    /// Waits for a running sign-in (tests).
    func waitForConnect() async { await task?.value }

    static func describe(_ error: any Error) -> String {
        switch error {
        case PlayerError.unavailable(let reason), PlayerError.failed(let reason): reason
        case is CancellationError: "Sign-in cancelled"
        default: error.localizedDescription
        }
    }
}

private struct SnapshotTokenStore: SpotifyTokenStore {
    let signedIn: Bool
    func load() -> SpotifyTokens? {
        signedIn ? SpotifyTokens(accessToken: "a", refreshToken: "r", expiresAt: .distantFuture) : nil
    }
    func save(_ tokens: SpotifyTokens?) {}
}

private struct NoHTTP: HTTPRequesting {
    func send(_ request: HTTPRequest) async throws -> HTTPResponse {
        throw PlayerError.failed("no network in snapshots")
    }
}
