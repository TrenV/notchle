import Foundation
import Observation
import NotchleCore

/// The one object the notch UI binds to. Owned by the integrator (frozen contract):
/// views read `state`/`settings` and call `send`/`updateSettings`; the app coordinator
/// fills in the closures and pushes new state after every engine step.
@MainActor
@Observable
public final class NotchViewModel {
    public var state: GameState
    public var settings: AppSettings
    /// Name and capability of the active player, for the settings sheet and the
    /// "preview ends after 30s" hint.
    public var playerName: String = ""
    public var playerPlaysFullTrack: Bool = true
    /// Spotify Connect sign-in state (additive; nil in demos and tests that don't need it).
    public var spotifyConnect: SpotifyConnectModel?
    /// Every recorded track, in the order played (agreed addition, 2026-09-24). Views never
    /// show this raw: they go through `HistorySpoilerFilter.visible(_:in:)`.
    public var history: [HistoryEntry] = []
    /// Album cover of the current track, set only once its answer is on screen (correct or
    /// revealed) and cleared when the phase moves on. Views still gate it through
    /// `NotchUIRules.revealedArtworkURL(_:_:)`.
    public var currentArtworkURL: URL?

    @ObservationIgnored public var send: (GameAction) -> Void = { _ in }
    @ObservationIgnored public var updateSettings: (AppSettings) -> Void = { _ in }
    @ObservationIgnored public var clearHistory: () -> Void = {}

    public init(state: GameState = GameState(), settings: AppSettings = AppSettings()) {
        self.state = state
        self.settings = settings
    }
}
