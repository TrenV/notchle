import Foundation

// The game is a pure reducer: `GameEngine.send(_:)` takes an action, mutates `GameState`
// and returns effects for the platform layer to run (fetch, play, stop). No timers, no I/O,
// no randomness except the injected seed, so it is fully unit-testable and portable.
//
// Agreed rules (Tren, 2026-09-24):
// - Heardle-style tiers: snippet of 5s, a wrong guess offers a retry at 10s, then 15s.
//   A wrong guess at the last tier (or giving up) reveals the answer: that track is missed.
// - A guess needs both title and artists: every credited artist, in any order. Typos are
//   forgiven as long as the gist is right (Tren, 2026-09-24; see FuzzyAnswerJudge).
// - Correct: confetti, and the song keeps playing. `next` moves on.
// - End of a set (Tren, 2026-09-24: "add 20 new songs, or keep the songs you didn't get
//   correctly"): every track answered correctly in the set is cleared (whether or not the set
//   was 20/20) and persisted. Then `startSet(SetChoice)`, from setComplete or setFailed:
//   `.replay` the same tracks reshuffled; `.keepMisses` the missed tracks plus new ones up to
//   the set size; `.allNew` a set of new tracks (not cleared, not in the set just played).
//   Fewer new tracks left gives a smaller set; none left (and nothing kept) gives `exhausted`,
//   paste a new URL. `nextSet`/`replaySet` are aliases for `.allNew`/`.replay`.
// - Snippets start at the start of the song (`GameConfig.snippetStart`).
// - Restart (Tren, 2026-09-24: "a restart button so i can restart the song"): while guessing
//   (`playingSnippet`/`guessing`) it replays the current tier's snippet from its start and
//   costs no attempt; after `correct`/`revealed` it plays the whole song again from 0:00.
//   Not in `wrong`: Retry is the way on there, and a free replay would allow unlimited
//   guesses at the same tier.
// - Skip (Tren, 2026-09-24: "forfeit 1 chance to get the longer version, over completely
//   forfeiting by giving up"): while guessing, give up this attempt without guessing and hear
//   the next, longer tier. At the last tier it is `giveUp`. Not in `wrong` (Retry is that).

public enum GamePhase: Sendable, Hashable {
    /// Nothing loaded. UI asks for a Spotify URL.
    case idle
    case loading
    /// Snippet is playing. Guess fields may already be used; submitting stops the snippet.
    case playingSnippet(tierIndex: Int)
    /// Snippet finished; waiting for a guess.
    case guessing(tierIndex: Int)
    /// Wrong guess and a longer tier is left: UI offers Retry (and Give up).
    case wrong(tierIndex: Int, verdict: Verdict)
    /// Right. Confetti; the song keeps playing until `next`.
    case correct(tierIndex: Int)
    /// Missed; answer is shown and the song keeps playing until `next`.
    /// `verdict` is the last wrong guess, nil if the player gave up without guessing.
    case revealed(verdict: Verdict?)
    /// All tracks of the set answered and every one correct.
    case setComplete(correctCount: Int)
    /// All tracks of the set answered, at least one missed.
    case setFailed(correctCount: Int)
    /// No unplayed tracks left in the listing.
    case exhausted
    /// Loading or playback failed. `next` skips the track (counts as missed); `reset` goes idle.
    case error(message: String)
}

public struct GameState: Sendable, Hashable {
    public var config: GameConfig
    public var phase: GamePhase = .idle
    public var listing: SourceListing?
    /// The current set, in play order.
    public var currentSet: [Track] = []
    /// Index into `currentSet` of the track being played.
    public var index: Int = 0
    /// One entry per finished track of the current set.
    public var results: [TrackOutcome] = []
    /// 1-based set counter within this listing.
    public var setNumber: Int = 0
    /// Track ids answered correctly at the end of a set, per listing. Persisted by the
    /// platform layer so a relaunch does not repeat songs you already cleared.
    public var clearedTrackIDs: Set<String> = []
    /// Incremented on every correct guess. The UI fires confetti when it changes.
    public var celebrationCount: Int = 0

    public var currentTrack: Track? {
        currentSet.indices.contains(index) ? currentSet[index] : nil
    }

    public var correctCount: Int {
        results.filter { if case .correct = $0 { return true } else { return false } }.count
    }

    public init(config: GameConfig = GameConfig(), clearedTrackIDs: Set<String> = []) {
        self.config = config
        self.clearedTrackIDs = clearedTrackIDs
    }
}

public enum GameAction: Sendable, Hashable {
    /// User pasted a URL. Effect: `.fetch`.
    case load(SourceRef)
    /// Platform finished the fetch.
    case loaded(SourceListing)
    case loadFailed(message: String)
    /// Player reports the snippet has played and paused.
    case snippetFinished
    case submit(Guess)
    /// From `.wrong`: play the next (longer) tier.
    case retry
    /// From `.playingSnippet`, `.guessing` or `.wrong`: reveal, counts as missed.
    case giveUp
    /// From playingSnippet/guessing: give up this attempt without guessing and play the next,
    /// longer tier. At the last tier it behaves like giveUp. Ignored elsewhere (in wrong, Retry
    /// already does this).
    case skip
    /// Replay the current snippet from its start (playingSnippet/guessing: doesn't use up an
    /// attempt), or restart the whole song from 0:00 (correct/revealed). Ignored elsewhere.
    case restart
    /// From `.correct`, `.revealed` or `.error`: move to the next track (or end the set).
    case next
    /// From `.setComplete` or `.setFailed`: start the next set as chosen. Ignored elsewhere.
    case startSet(SetChoice)
    /// Alias for `startSet(.allNew)`.
    case nextSet
    /// Alias for `startSet(.replay)`.
    case replaySet
    case playbackFailed(message: String)
    case configure(GameConfig)
    /// Back to `.idle`, keeping `clearedTrackIDs`.
    case reset
}

/// What to play after a set ends.
public enum SetChoice: String, Sendable, Hashable, Codable, CaseIterable {
    /// The same tracks, reshuffled. Keeps the set number.
    case replay
    /// The missed tracks plus new ones up to the set size, shuffled together.
    case keepMisses
    /// Only new tracks: not cleared and not in the set just played.
    case allNew
}

public enum GameEffect: Sendable, Hashable {
    case fetch(SourceRef)
    /// Play `seconds` of `track` from `start`, then pause and report `.snippetFinished`.
    /// Starting a new snippet cancels any snippet still running.
    case playSnippet(Track, start: Double, seconds: Double)
    /// Cancel a running snippet (if any) and let the current song play on from where it is.
    case continuePlaying
    /// Cancel any running snippet and play `track` from the very start, continuing to the end.
    case restartTrack(Track)
    /// Stop playback entirely.
    case stop
    /// `clearedTrackIDs` changed; persist it.
    case persistProgress
    /// The outcome of `track` was just decided: guessed right, or missed (wrong at the last
    /// tier, give up, skip at the last tier, or `next` from an error before an outcome).
    /// Emitted exactly once per played track, first in that step's effect list. The platform
    /// stamps the date and the listing and appends it to the play history.
    /// `wrongGuesses`/`skips` count this track's wrong guesses and skips (a skip at the last
    /// tier is exactly `giveUp`, so it is not counted); both reset per track and on `replaySet`.
    case recordOutcome(Track, TrackOutcome, wrongGuesses: Int, skips: Int)
}

/// Decides whether a guess matches a track. Implemented by `FuzzyAnswerJudge` (Wave 2, agent C).
public protocol AnswerJudging: Sendable {
    func judge(_ guess: Guess, against track: Track) -> Verdict
}
