import NotchleCore

/// Text fields of the notch UI.
public enum UIField: Hashable, Sendable {
    case url, title, artist
}

/// Keys the notch handles itself (everything else goes to the focused control).
public enum UIKeyInput: Hashable, Sendable {
    case returnKey, escape, tab, backTab, commandR
}

/// What a key means in the current phase.
public enum UICommand: Hashable, Sendable {
    /// Submit title + artist (or move focus to the empty one).
    case submitGuess
    /// Parse the URL field and send `.load`.
    case load
    case send(GameAction)
    case collapse
    case closeSettings
    case focus(UIField)
}

/// What the guess fields do when the phase changes.
public enum FieldTransition: Hashable, Sendable {
    case none
    /// A new track: clear both fields and focus Title.
    case clearAndFocusTitle
    /// A retry of the same track: keep what was typed and focus this (the first wrong) field.
    case keepAndFocus(UIField)
    /// A URL is needed: focus the URL field.
    case focusURL
}

/// Pure presentation rules: which phases expand, which keys do what, what may be shown.
public enum NotchUIRules {
    /// Phases that wait for the player: the notch expands by itself when one begins.
    public static func needsInput(_ phase: GamePhase) -> Bool {
        switch phase {
        case .loading, .playingSnippet: false
        case .idle, .guessing, .wrong, .correct, .revealed, .setComplete, .setFailed, .exhausted, .error: true
        }
    }

    /// Phases in which the track must stay secret.
    public static func isGuessPhase(_ phase: GamePhase) -> Bool {
        switch phase {
        case .playingSnippet, .guessing, .wrong: true
        default: false
        }
    }

    /// Phases that show the Title/Artist fields.
    public static func showsGuessFields(_ phase: GamePhase) -> Bool {
        switch phase {
        case .playingSnippet, .guessing: true
        default: false
        }
    }

    /// Phases that show the Spotify URL field.
    public static func showsURLField(_ phase: GamePhase) -> Bool {
        switch phase {
        case .idle, .exhausted: true
        default: false
        }
    }

    /// Phases in which the panel takes the keyboard when it expands by itself.
    public static func wantsKeyboard(_ phase: GamePhase) -> Bool {
        needsInput(phase) || showsGuessFields(phase)
    }

    /// Hover ended: collapse? Only in phases that need no input, and never while the player has
    /// typed a guess during the snippet (collapsing would hide what they are typing).
    public static func collapsesOnHoverEnd(_ phase: GamePhase, hasTypedGuess: Bool) -> Bool {
        if needsInput(phase) { return false }
        if case .playingSnippet = phase, hasTypedGuess { return false }
        return true
    }

    /// The answer, if this phase may show it. The single gate every view goes through:
    /// nil during `playingSnippet`, `guessing` and `wrong` (and whenever there is no track).
    public static func revealedAnswer(_ state: GameState) -> (title: String, artist: String)? {
        switch state.phase {
        case .correct, .revealed:
            guard let track = state.currentTrack else { return nil }
            return (track.title, track.artists.joined(separator: ", "))
        default:
            return nil
        }
    }

    /// Placeholder of the artist field. A guess needs every credited artist, so with more than
    /// one the player is told how many (the count only, never names).
    public static func artistPlaceholder(artistCount: Int) -> String {
        artistCount > 1 ? "\(artistCount) artists, any order" : "Artist(s)"
    }

    /// Extra hint in `.wrong` when the artist half was wrong and several artists are needed.
    public static func artistHint(_ verdict: Verdict, artistCount: Int) -> String? {
        !verdict.artistCorrect && artistCount > 1 ? "need all \(artistCount)" : nil
    }

    /// Number of credited artists of the current track: safe to show in every phase.
    public static func artistCount(_ state: GameState) -> Int {
        state.currentTrack?.artists.count ?? 1
    }

    /// "3/20": the position of the current track in the set; "–" before a set is loaded.
    public static func progressText(_ state: GameState) -> String {
        let total = state.currentSet.isEmpty ? 0 : state.currentSet.count
        guard total > 0 else { return "–" }
        switch state.phase {
        case .setComplete(let n), .setFailed(let n): return "\(n)/\(total)"
        case .idle, .exhausted, .loading: return "–"
        default: return "\(min(state.index + 1, total))/\(total)"
        }
    }

    /// Seconds of the tier, falling back to the last configured tier.
    public static func seconds(ofTier tier: Int, _ config: GameConfig) -> Double {
        guard !config.tiers.isEmpty else { return 0 }
        return config.tiers[max(0, min(tier, config.tiers.count - 1))]
    }

    /// "5s", "10s", "2.5s".
    public static func secondsLabel(_ s: Double) -> String {
        s == s.rounded() ? "\(Int(s))s" : String(format: "%.1fs", s)
    }

    /// Seconds of the retry offered from `.wrong(tierIndex:)`.
    public static func retrySeconds(after tier: Int, _ config: GameConfig) -> Double {
        seconds(ofTier: tier + 1, config)
    }

    /// Field behaviour for a phase change. `old` is nil at launch.
    public static func fieldTransition(from old: GamePhase?, to new: GamePhase) -> FieldTransition {
        if old == new { return .none }
        switch new {
        case .idle, .exhausted:
            return .focusURL
        case .playingSnippet(let tier), .guessing(let tier):
            // Snippet → guessing of the same tier: the player may be mid-typing; leave them be.
            if case .playingSnippet(let t) = old, t == tier, case .guessing = new { return .none }
            if case .wrong(_, let verdict) = old, tier > 0 {
                return .keepAndFocus(verdict.titleCorrect ? .artist : .title)
            }
            return tier == 0 ? .clearAndFocusTitle : .keepAndFocus(.title)
        default:
            return .none
        }
    }

    /// Keyboard mapping. `settingsOpen`: the settings view covers the phase content.
    public static func command(for key: UIKeyInput, phase: GamePhase, focused: UIField?,
                               settingsOpen: Bool = false) -> UICommand? {
        if settingsOpen {
            return key == .escape ? .closeSettings : nil
        }
        switch key {
        case .returnKey:
            switch phase {
            case .idle, .exhausted: return .load
            case .playingSnippet, .guessing: return .submitGuess
            case .wrong: return .send(.retry)
            case .correct, .revealed, .error: return .send(.next)
            case .setComplete: return .send(.nextSet)
            case .setFailed: return .send(.replaySet)
            case .loading: return nil
            }
        case .commandR:
            if case .wrong = phase { return .send(.retry) }
            return nil
        case .escape:
            return isGuessPhase(phase) ? .send(.giveUp) : .collapse
        case .tab, .backTab:
            guard showsGuessFields(phase) else { return nil }
            return focused == .title ? .focus(.artist) : .focus(.title)
        }
    }
}
