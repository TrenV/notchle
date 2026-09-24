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
    /// Play or History. The game keeps running behind the History tab; switching back shows
    /// it unchanged (typed guess included).
    public private(set) var tab: NotchTab = .play
    /// When "Clear history?" was armed; nil when it isn't (same window as Quit).
    public internal(set) var clearHistoryArmedAt: Date?
    /// Field that had focus when the History tab opened, restored on the way back.
    private var focusBeforeHistory: UIField?

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

    /// When the mouse left the expanded shape; nil while it is inside (or nothing is pending).
    public private(set) var mouseLeftAt: Date?
    /// Last key press that reached the panel.
    public private(set) var lastKeyAt: Date?
    /// When the current phase began; drives the collapsed pill's brief flashes.
    public internal(set) var phaseStartedAt = Date()
    /// Bumped when a flash ends so the collapsed pill redraws.
    public private(set) var indicatorRefresh = 0
    /// When "Quit playlist?" was armed; nil when it isn't. Expires after
    /// `NotchUIRules.quitConfirmWindow` (settable in-module for snapshots).
    public internal(set) var quitArmedAt: Date?

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
            // No auto-expand: the notch opens only on hover or a click.
            phaseStartedAt = now
            let flash = NotchUIRules.flashDuration(new.phase)
            if flash > 0 {
                Task { @MainActor [weak self] in
                    try? await Task.sleep(for: .seconds(flash + 0.05))
                    self?.indicatorRefresh &+= 1
                }
            }
            if case .playingSnippet = new.phase { snippetStart = now }
            if !NotchUIRules.showsQuit(new.phase) { quitArmedAt = nil }
            // A field that is gone with the old phase no longer holds focus (so no longer "typing").
            switch focusedField {
            case .title?, .artist?: if !NotchUIRules.showsGuessFields(new.phase) { focusedField = nil }
            case .url?: if !NotchUIRules.showsURLField(new.phase) { focusedField = nil }
            case nil: break
            }
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

    // MARK: Hover, click, typing → expand / auto-collapse

    public func hoverChanged(_ inside: Bool, now: Date = Date()) {
        guard inside != isHovering else { return }
        isHovering = inside
        if inside {
            mouseLeftAt = nil          // re-entering cancels a pending collapse
            isExpanded = true
        } else if isExpanded {
            mouseLeftAt = now          // collapse after the grace period, unless typing
        }
    }

    /// A click on the shape (also covers a hover the monitors missed).
    public func clicked() {
        mouseLeftAt = nil
        isExpanded = true
    }

    /// Every key press that reaches the (key) panel.
    public func noteKeyActivity(now: Date = Date()) {
        lastKeyAt = now
    }

    /// Opens the collapsed panel because the player started typing into it.
    public func expandForTyping(now: Date = Date()) {
        lastKeyAt = now
        isExpanded = true
        if !isHovering { mouseLeftAt = now }
        requestFocus(requestedFocus ?? (NotchUIRules.showsURLField(phase) ? .url : .title))
    }

    public var focusedText: String {
        switch focusedField {
        case .url: urlText
        case .title: titleText
        case .artist: artistText
        case nil: ""
        }
    }

    public func isTyping(now: Date = Date()) -> Bool {
        NotchUIRules.isTyping(focused: focusedField, focusedText: focusedText, lastKeyAt: lastKeyAt, now: now)
    }

    /// A collapse is waiting on the grace period or on typing to stop.
    public var autoCollapsePending: Bool { isExpanded && mouseLeftAt != nil }

    /// Call periodically while `autoCollapsePending`. Returns true when it collapsed.
    @discardableResult
    public func evaluateAutoCollapse(now: Date = Date()) -> Bool {
        guard isExpanded else { mouseLeftAt = nil; return false }
        // An armed "Quit playlist?" holds the panel open until it is answered or expires.
        let busy = isTyping(now: now) || isQuitArmed(now: now) || isClearHistoryArmed(now: now)
        guard NotchUIRules.shouldAutoCollapse(expanded: isExpanded, mouseLeftAt: mouseLeftAt,
                                              typing: busy, now: now) else { return false }
        collapse()
        return true
    }

    public func collapse() {
        isExpanded = false
        showingSettings = false
        // Reopening shows the game again (hover in mid-snippet lands on the guess fields).
        tab = .play
        focusBeforeHistory = nil
        clearHistoryArmedAt = nil
        quitArmedAt = nil
        mouseLeftAt = nil
        focusedField = nil
    }

    /// The collapsed pill for the current state.
    public func collapsedIndicator(now: Date = Date()) -> NotchUIRules.CollapsedIndicator {
        _ = indicatorRefresh
        return NotchUIRules.collapsedIndicator(model.state, secondsInPhase: now.timeIntervalSince(phaseStartedAt))
    }

    // MARK: Intents

    public func perform(_ command: UICommand) {
        switch command {
        case .submitGuess: submitGuess()
        case .load: load()
        case .send(.restart): restart()
        case .send(.skip): skip()
        case .send(let action): model.send(action)
        case .collapse:
            collapse()
            requestFocus(nil)
        case .closeSettings: showingSettings = false
        case .focus(let field): requestFocus(field)
        case .quit: quitPressed()
        case .disarmQuit: disarmQuit()
        case .showTab(let tab): showTab(tab)
        }
    }

    // MARK: History tab

    /// The expanded shape is the taller History size.
    public var usesTallLayout: Bool { tab == .history && !showingSettings }

    /// Switches tab (⌘1/⌘2 or the header switch). Opens a collapsed panel; closes settings.
    public func showTab(_ new: NotchTab, now: Date = Date()) {
        showingSettings = false
        clearHistoryArmedAt = nil
        if !isExpanded {
            isExpanded = true
            if !isHovering { mouseLeftAt = now }
        }
        guard new != tab else { return }
        if new == .history {
            focusBeforeHistory = focusedField
            tab = .history
        } else {
            tab = .play
            // Back to the game: the fields reappear; put the cursor where it was.
            let back = focusBeforeHistory ?? (NotchUIRules.showsGuessFields(phase) ? .title
                                             : NotchUIRules.showsURLField(phase) ? .url : nil)
            focusBeforeHistory = nil
            if let back, back == .url ? NotchUIRules.showsURLField(phase) : NotchUIRules.showsGuessFields(phase) {
                requestFocus(back)
            }
        }
    }

    /// History entries the History tab may show: the spoiler filter over the model's history.
    public var visibleHistory: [HistoryEntry] {
        HistorySpoilerFilter.visible(model.history, in: model.state)
    }

    public func isClearHistoryArmed(now: Date = Date()) -> Bool {
        NotchUIRules.isQuitArmed(armedAt: clearHistoryArmedAt, now: now)
    }

    /// "Clear history" button: first press arms "Clear history?", a second press within the
    /// window clears.
    public func clearHistoryPressed(now: Date = Date()) {
        if isClearHistoryArmed(now: now) {
            clearHistoryArmedAt = nil
            model.clearHistory()
            return
        }
        clearHistoryArmedAt = now
        Task { @MainActor [weak self] in
            try? await Task.sleep(for: .seconds(NotchUIRules.quitConfirmWindow))
            if self?.clearHistoryArmedAt == now { self?.clearHistoryArmedAt = nil }
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
        lastKeyAt = nil
        model.send(.load(ref))
    }

    public func submitGuess() {
        let title = titleText.trimmingCharacters(in: .whitespaces)
        let artist = artistText.trimmingCharacters(in: .whitespaces)
        if title.isEmpty { requestFocus(.title); return }
        if artist.isEmpty { requestFocus(.artist); return }
        lastKeyAt = nil                // submitted: no longer typing
        model.send(.submit(Guess(title: title, artist: artist)))
    }

    public func isQuitArmed(now: Date = Date()) -> Bool {
        NotchUIRules.isQuitArmed(armedAt: quitArmedAt, now: now)
    }

    /// The header's quit button or ⌘N. First press arms "Quit playlist?" for
    /// `quitConfirmWindow` seconds; a second press within it quits.
    public func quitPressed(now: Date = Date()) {
        guard NotchUIRules.showsQuit(phase) else { quitArmedAt = nil; return }
        if isQuitArmed(now: now) {
            confirmQuit()
            return
        }
        quitArmedAt = now
        if !isExpanded {                    // ⌘N on the collapsed panel: show the question
            isExpanded = true
            if !isHovering { mouseLeftAt = now }
        }
        Task { @MainActor [weak self] in
            try? await Task.sleep(for: .seconds(NotchUIRules.quitConfirmWindow))
            if self?.quitArmedAt == now { self?.quitArmedAt = nil }   // revert the capsule
        }
    }

    public func disarmQuit() {
        quitArmedAt = nil
    }

    /// Stops playback and goes back to an empty, focused URL field (a new playlist).
    private func confirmQuit() {
        quitArmedAt = nil
        showingSettings = false
        urlText = ""
        urlMessage = nil
        lastKeyAt = nil
        model.send(.reset)
        requestFocus(.url)
    }

    /// Replays the snippet (guess phases) or restarts the song (correct/revealed). The typed
    /// guess and the focus stay: it is the same track.
    public func restart(now: Date = Date()) {
        let before = phase
        guard NotchUIRules.canRestart(before) else { return }
        // A phase change re-applies `requestedFocus`: point it at the field the player is in.
        if let field = focusedField, NotchUIRules.showsGuessFields(before) { requestFocus(field) }
        model.send(.restart)
        // playingSnippet → playingSnippet is no phase change, so restart the progress here.
        if case .playingSnippet = before, case .playingSnippet = phase { snippetStart = now }
    }

    /// Forfeits this attempt for the next, longer tier. The typed guess and the focus stay.
    /// Inert at the last tier: the UI never turns a Skip into a Give up.
    public func skip() {
        guard NotchUIRules.skipSeconds(phase, model.state.config) != nil else { return }
        if let field = focusedField, NotchUIRules.showsGuessFields(phase) { requestFocus(field) }
        model.send(.skip)
    }

    public func setPlayerMode(_ mode: PlayerMode) {
        guard model.settings.playerMode != mode else { return }
        var s = model.settings
        s.playerMode = mode
        model.updateSettings(s)
    }
}
