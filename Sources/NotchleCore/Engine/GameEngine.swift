import Foundation

/// The game as a pure reducer. See the header comment in `Contracts/Game.swift` for the rules.
///
/// Integrator decisions implemented here (2026-09-24):
/// - Every shuffle draws from a SplitMix64 seeded with `seed`: same seed + same actions = same order.
/// - A set is `setSize` tracks drawn from the listing's tracks not yet in `clearedTrackIDs`
///   (duplicates in the listing collapse to one). Fewer left gives a smaller final set; none
///   left gives `.exhausted`.
/// - `configure` takes effect at the next snippet (and the next set for `setSize`).
/// - Anything not listed for the current phase is a no-op returning `[]`.
public struct GameEngine: Sendable {
    public private(set) var state: GameState
    private let judge: AnswerJudging
    private var rng: SplitMix64
    /// The last wrong verdict for the current track; shown on reveal. Cleared per track.
    private var lastVerdict: Verdict?
    /// Wrong guesses and skips on the current track, for `.recordOutcome`. Cleared per track.
    private var wrongGuesses = 0
    private var skips = 0

    /// `seed` drives every shuffle so tests are deterministic; the app passes a random one.
    public init(config: GameConfig = GameConfig(), clearedTrackIDs: Set<String> = [],
                judge: AnswerJudging = FuzzyAnswerJudge(), seed: UInt64) {
        self.state = GameState(config: config, clearedTrackIDs: clearedTrackIDs)
        self.judge = judge
        self.rng = SplitMix64(seed: seed)
    }

    /// Applies `action` and returns the effects the platform layer must run, in order.
    /// Actions that make no sense in the current phase are ignored (no state change, no effects).
    public mutating func send(_ action: GameAction) -> [GameEffect] {
        switch (action, state.phase) {
        case let (.load(ref), _):
            clearSession()
            state.phase = .loading
            return [.stop, .fetch(ref)]

        case let (.loaded(listing), .loading):
            state.listing = listing
            state.setNumber = 1
            return startNewSet()

        case let (.loadFailed(message), .loading):
            state.phase = .error(message: message)
            return []

        case let (.snippetFinished, .playingSnippet(tier)):
            state.phase = .guessing(tierIndex: tier)
            return []

        case let (.submit(guess), .playingSnippet(tier)):
            return submit(guess, tier: tier, snippetPlaying: true)

        case let (.submit(guess), .guessing(tier)):
            return submit(guess, tier: tier, snippetPlaying: false)

        case let (.retry, .wrong(tier, _)):
            let nextTier = tier + 1
            guard nextTier < tiers.count else { return [] }
            return playSnippet(tier: nextTier)

        case (.giveUp, .playingSnippet), (.giveUp, .guessing), (.giveUp, .wrong):
            return reveal(lastVerdict)

        // Forfeit this attempt for the next tier; no result until the track ends. The last
        // tier has nothing longer to offer, so there it is exactly `giveUp`.
        case let (.skip, .playingSnippet(tier)), let (.skip, .guessing(tier)):
            guard tier + 1 < tiers.count else { return reveal(lastVerdict) }   // not counted as a skip
            skips += 1
            return playSnippet(tier: tier + 1)

        // Replay the same tier: no attempt used, no result, the typed guess is the UI's business.
        case let (.restart, .playingSnippet(tier)), let (.restart, .guessing(tier)):
            return playSnippet(tier: tier)

        // In `wrong`: hear the same snippet again but stay on the wrong screen (Retry or Give up
        // still decide), so a replay is never a free extra guess. Its snippetFinished is ignored.
        case let (.restart, .wrong(tier, _)):
            guard let track = state.currentTrack, tiers.indices.contains(tier) else { return [] }
            return [.playSnippet(track, start: state.config.snippetStart, seconds: tiers[tier])]

        case (.restart, .correct), (.restart, .revealed):
            guard let track = state.currentTrack else { return [] }
            return [.restartTrack(track)]

        case (.next, .correct), (.next, .revealed), (.next, .error):
            return advance()

        case (.nextSet, .setComplete):
            state.setNumber += 1
            return startNewSet()

        case (.replaySet, .setFailed):
            state.currentSet = rng.shuffled(state.currentSet)
            state.results = []
            return startTrack(at: 0)   // resets the per-track counters

        case let (.playbackFailed(message), phase) where phase.hasActiveTrack:
            state.phase = .error(message: message)
            return []

        case let (.configure(config), _):
            state.config = config
            return []

        case (.reset, _):
            clearSession()
            state.phase = .idle
            return [.stop]

        default:
            return []
        }
    }

    // MARK: - Steps

    private var tiers: [Double] {
        state.config.tiers.isEmpty ? GameConfig().tiers : state.config.tiers
    }

    private mutating func clearSession() {
        state.listing = nil
        state.currentSet = []
        state.index = 0
        state.results = []
        state.setNumber = 0
        resetTrackCounters()
    }

    private mutating func resetTrackCounters() {
        lastVerdict = nil
        wrongGuesses = 0
        skips = 0
    }

    /// The `.recordOutcome` effect for the current track.
    private func record(_ outcome: TrackOutcome) -> [GameEffect] {
        guard let track = state.currentTrack else { return [] }
        return [.recordOutcome(track, outcome, wrongGuesses: wrongGuesses, skips: skips)]
    }

    private mutating func startNewSet() -> [GameEffect] {
        guard let listing = state.listing else { return [] }
        var seen = Set<String>()
        let pool = listing.tracks.filter { track in
            !state.clearedTrackIDs.contains(track.id) && seen.insert(track.id).inserted
        }
        state.results = []
        state.index = 0
        guard !pool.isEmpty else {
            state.currentSet = []
            state.phase = .exhausted
            return []
        }
        let size = max(1, state.config.setSize)
        state.currentSet = Array(rng.shuffled(pool).prefix(size))
        return startTrack(at: 0)
    }

    private mutating func startTrack(at index: Int) -> [GameEffect] {
        state.index = index
        resetTrackCounters()
        return playSnippet(tier: 0)
    }

    private mutating func playSnippet(tier: Int) -> [GameEffect] {
        guard let track = state.currentTrack else { return [] }
        state.phase = .playingSnippet(tierIndex: tier)
        return [.playSnippet(track, start: state.config.snippetStart, seconds: tiers[tier])]
    }

    private mutating func submit(_ guess: Guess, tier: Int, snippetPlaying: Bool) -> [GameEffect] {
        guard let track = state.currentTrack else { return [] }
        let isBlank = { (s: String) in s.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }
        if isBlank(guess.title) && isBlank(guess.artist) { return [] }

        let verdict = judge.judge(guess, against: track)
        if verdict.isCorrect {
            state.phase = .correct(tierIndex: tier)
            state.results.append(.correct(tierIndex: tier))
            state.celebrationCount += 1
            return record(.correct(tierIndex: tier)) + [.continuePlaying]
        }
        lastVerdict = verdict
        wrongGuesses += 1
        if tier + 1 < tiers.count {
            state.phase = .wrong(tierIndex: tier, verdict: verdict)
            return snippetPlaying ? [.stop] : []
        }
        return reveal(verdict)
    }

    private mutating func reveal(_ verdict: Verdict?) -> [GameEffect] {
        state.phase = .revealed(verdict: verdict)
        state.results.append(.missed)
        return record(.missed) + [.continuePlaying]
    }

    private mutating func advance() -> [GameEffect] {
        guard !state.currentSet.isEmpty else { return [] }  // error while loading: nothing to skip
        // An error mid-track (before an outcome was recorded) counts as a miss.
        var recorded: [GameEffect] = []
        if state.results.count <= state.index {
            state.results.append(.missed)
            recorded = record(.missed)
        }

        let nextIndex = state.index + 1
        if nextIndex < state.currentSet.count {
            return recorded + startTrack(at: nextIndex)
        }

        state.index = state.currentSet.count
        let correct = state.correctCount
        if correct == state.currentSet.count {
            state.phase = .setComplete(correctCount: correct)
            state.clearedTrackIDs.formUnion(state.currentSet.map(\.id))
            return recorded + [.stop, .persistProgress]
        }
        state.phase = .setFailed(correctCount: correct)
        return recorded + [.stop]
    }
}

private extension GamePhase {
    /// Phases in which a track is loaded into the player, so a playback failure is meaningful.
    var hasActiveTrack: Bool {
        switch self {
        case .playingSnippet, .guessing, .wrong, .correct, .revealed: true
        default: false
        }
    }
}
