import Foundation
import Observation
import NotchleCore

/// View-local state of the notch (expansion, text fields, focus, animations). The game itself
/// lives in `NotchViewModel`; this object only reacts to it and turns UI intents into actions.
@MainActor
@Observable
public final class NotchUIState {
    public let model: NotchViewModel

    public var isExpanded = false
    public private(set) var isHovering = false
    public var showingSettings = false

    public var urlText = ""
    public var titleText = ""
    public var artistText = ""
    /// Shown under the URL field, e.g. "Unsupported link".
    public var urlMessage: String?

    /// Focus the views should move to. `focusToken` changes on every request so asking for the
    /// same field twice still refocuses it.
    public private(set) var requestedFocus: UIField?
    public private(set) var focusToken = 0
    /// Field that currently has focus, reported back by the views.
    public var focusedField: UIField?

    /// When the running snippet started; drives the local progress animation.
    public var snippetStart: Date?
    /// When the current confetti burst started; nil when none is running.
    public var celebrationStart: Date?
    public private(set) var celebrationSeed: UInt64 = 1
    /// Snapshot/debug override for the system Reduce Motion setting.
    public var reduceMotionOverride: Bool?

    /// Turns the URL field's text into a source. The demo harness swaps this out while the real
    /// parser is still a stub.
    @ObservationIgnored public var parseSource: (String) -> SourceRef? = { SourceRef(string: $0) }
    /// Called when the UI wants the panel to take the keyboard (new input phase).
    @ObservationIgnored public var onWantsKeyboard: () -> Void = {}

    public init(model: NotchViewModel) {
        self.model = model
    }

    public var phase: GamePhase { model.state.phase }

    public var hasTypedGuess: Bool {
        !titleText.trimmingCharacters(in: .whitespaces).isEmpty
            || !artistText.trimmingCharacters(in: .whitespaces).isEmpty
    }

    public var canSubmitGuess: Bool {
        !titleText.trimmingCharacters(in: .whitespaces).isEmpty
            && !artistText.trimmingCharacters(in: .whitespaces).isEmpty
    }

    public func requestFocus(_ field: UIField?) {
        requestedFocus = field
        focusToken &+= 1
    }

    // MARK: Reacting to the game

    /// Call after every game state change. `old` is nil for the first state (launch): that one
    /// never fires confetti.
    public func stateDidChange(from old: GameState?, to new: GameState, now: Date = Date()) {
        let phaseChanged = old?.phase != new.phase
        if phaseChanged {
            urlMessage = nil
            if NotchUIRules.needsInput(new.phase) { isExpanded = true }
            if case .playingSnippet = new.phase { snippetStart = now }
            switch NotchUIRules.fieldTransition(from: old?.phase, to: new.phase) {
            case .none: break
            case .clearAndFocusTitle:
                titleText = ""
                artistText = ""
                requestFocus(.title)
            case .keepAndFocus(let field):
                requestFocus(field)
            case .focusURL:
                requestFocus(.url)
            }
            if isExpanded, NotchUIRules.wantsKeyboard(new.phase) { onWantsKeyboard() }
        }
        if let old, new.celebrationCount > old.celebrationCount {
            celebrationSeed &+= 0x9E37_79B9_7F4A_7C15
            celebrationStart = now
            // Clear it afterwards so the animation timeline stops ticking.
            Task { @MainActor [weak self] in
                try? await Task.sleep(for: .seconds(max(Confetti.duration, CelebrationGlow.duration) + 0.1))
                if self?.celebrationStart == now { self?.celebrationStart = nil }
            }
        }
    }

    // MARK: Hover

    public func hoverChanged(_ inside: Bool) {
        guard inside != isHovering else { return }
        isHovering = inside
        if inside {
            isExpanded = true
        } else if NotchUIRules.collapsesOnHoverEnd(phase, hasTypedGuess: hasTypedGuess), !showingSettings {
            isExpanded = false
        }
    }

    // MARK: Intents

    public func perform(_ command: UICommand) {
        switch command {
        case .submitGuess: submitGuess()
        case .load: load()
        case .send(let action): model.send(action)
        case .collapse:
            showingSettings = false
            isExpanded = false
            requestFocus(nil)
        case .closeSettings: showingSettings = false
        case .focus(let field): requestFocus(field)
        }
    }

    public func load() {
        let text = urlText.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !text.isEmpty else {
            urlMessage = "Paste a Spotify playlist, album or artist link"
            return
        }
        guard let ref = parseSource(text) else {
            urlMessage = "Unsupported link"
            return
        }
        urlMessage = nil
        model.send(.load(ref))
    }

    public func submitGuess() {
        let title = titleText.trimmingCharacters(in: .whitespaces)
        let artist = artistText.trimmingCharacters(in: .whitespaces)
        if title.isEmpty { requestFocus(.title); return }
        if artist.isEmpty { requestFocus(.artist); return }
        model.send(.submit(Guess(title: title, artist: artist)))
    }

    public func setPlayerMode(_ mode: PlayerMode) {
        guard model.settings.playerMode != mode else { return }
        var s = model.settings
        s.playerMode = mode
        model.updateSettings(s)
    }
}
