import Foundation
import NotchleCore

/// The Spotify Connect part of the settings view.
public enum SpotifyConnectControls: Hashable, Sendable {
    /// Another player is chosen: nothing Connect-related shows.
    case hidden
    /// Client ID field + "Connect Spotify" (enabled once a Client ID is typed). `busy`: the
    /// browser sign-in is running (the button reads "Cancel"). `message`: the last error.
    case signIn(canConnect: Bool, busy: Bool, message: String?)
    /// "Connected as <name>" + "Sign out".
    case connected(displayName: String)
}

public extension NotchUIRules {
    /// The player choices, in picker order, with their labels.
    static let playerChoices: [(mode: PlayerMode, label: String)] = [
        (.spotifyApp, "Spotify app"),
        (.spotifyConnect, "Spotify (no window, Premium)"),
        (.preview, "30-second previews"),
    ]

    static let spotifyConnectHelp =
        "Needs Premium and your own app at developer.spotify.com, redirect http://127.0.0.1:43821/callback"

    /// Which Connect controls the settings view shows. They never show track info.
    static func spotifyConnectControls(mode: PlayerMode, status: SpotifyConnectStatus?, clientID: String) -> SpotifyConnectControls {
        guard mode == .spotifyConnect else { return .hidden }
        let hasClientID = !clientID.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
        switch status ?? .signedOut {
        case .connected(let name): return .connected(displayName: name)
        case .connecting: return .signIn(canConnect: true, busy: true, message: nil)
        case .signedOut: return .signIn(canConnect: hasClientID, busy: false, message: nil)
        case .failed(let reason): return .signIn(canConnect: hasClientID, busy: false, message: reason)
        }
    }

    /// The snippet-tiers row makes way for the Connect controls (the notch is only ~120 pt tall).
    static func showsSnippetsRow(_ controls: SpotifyConnectControls) -> Bool { controls == .hidden }
}
