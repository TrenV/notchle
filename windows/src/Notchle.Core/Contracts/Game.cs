namespace Notchle.Core;

// Mirror of Sources/NotchleCore/Contracts/Game.swift. The game is a pure reducer:
// GameEngine.Send(action) replaces State and returns effects for the platform layer to run.
//
// Agreed rules (Tren, 2026-09-24):
// - Heardle-style tiers: 5s, a wrong guess offers a retry at 10s, then 15s. Wrong at the last
//   tier (or giving up) reveals the answer: that track is missed.
// - A guess needs title and every credited artist, any order. Typos are forgiven as long as
//   the gist is right (FuzzyAnswerJudge; shared vectors in /spec/judge-cases.json).
// - Correct: confetti, and the song keeps playing. Next moves on.
// - Set end (Tren, 2026-09-24: "add 20 new songs, or keep the songs you didn't get correctly"):
//   at every set end, complete or failed, the tracks answered correctly join ClearedTrackIds
//   (persisted). Then StartSet(choice), in SetComplete and SetFailed:
//   Replay: the same tracks reshuffled, results cleared (SetNumber unchanged).
//   KeepMisses: the missed tracks plus new ones (not cleared, not in the finished set) up to
//   SetSize, shuffled. At SetComplete (no misses) it equals AllNew.
//   AllNew: SetSize new tracks; fewer left gives a smaller set; none left gives Exhausted.
//   NextSet = StartSet(AllNew) and ReplaySet = StartSet(Replay) remain as aliases.
// - Snippets start at GameConfig.SnippetStart (default 0).
// - Restart (Tren, 2026-09-24): in PlayingSnippet / Guessing it replays the current snippet from
//   its start at the same tier (no attempt used, nothing recorded). In Correct / Revealed it
//   restarts the whole song from 0:00 and lets it play on. Ignored everywhere else, Wrong
//   included: Retry is the way on from there, and a free replay would allow unlimited guesses
//   at the same tier.
// - Skip (Tren, 2026-09-24: "forfeit 1 chance to get the longer version, over completely
//   forfeiting by giving up"): in PlayingSnippet / Guessing it spends the attempt without a
//   guess and plays the next, longer tier. At the last tier it is GiveUp. Ignored elsewhere.

public abstract record GamePhase
{
    public sealed record Idle : GamePhase;
    public sealed record Loading : GamePhase;
    /// Snippet is playing; guess fields may already be used, submitting stops the snippet.
    public sealed record PlayingSnippet(int TierIndex) : GamePhase;
    public sealed record Guessing(int TierIndex) : GamePhase;
    /// Wrong guess with a longer tier left: offer Retry and Give up.
    public sealed record Wrong(int TierIndex, Verdict Verdict) : GamePhase;
    /// Right: confetti, song keeps playing until Next.
    public sealed record Correct(int TierIndex) : GamePhase;
    /// Missed: answer shown, song keeps playing until Next. Verdict is null after a give-up.
    public sealed record Revealed(Verdict? Verdict) : GamePhase;
    public sealed record SetComplete(int CorrectCount) : GamePhase;
    public sealed record SetFailed(int CorrectCount) : GamePhase;
    public sealed record Exhausted : GamePhase;
    /// Next skips the track (missed); Reset goes idle.
    public sealed record Error(string Message) : GamePhase;
}

/// Immutable snapshot; the engine replaces it with `with` expressions.
public sealed record GameState
{
    public required GameConfig Config { get; init; }
    public GamePhase Phase { get; init; } = new GamePhase.Idle();
    public SourceListing? Listing { get; init; }
    public IReadOnlyList<Track> CurrentSet { get; init; } = Array.Empty<Track>();
    public int Index { get; init; }
    public IReadOnlyList<TrackOutcome> Results { get; init; } = Array.Empty<TrackOutcome>();
    /// 1-based set counter within this listing.
    public int SetNumber { get; init; }
    /// Ids of tracks answered correctly at a set end (any set, complete or failed); persisted so relaunches don't repeat them.
    public IReadOnlySet<string> ClearedTrackIds { get; init; } = new HashSet<string>();
    /// Incremented on every correct guess; the UI fires confetti when it changes.
    public int CelebrationCount { get; init; }

    public Track? CurrentTrack => Index >= 0 && Index < CurrentSet.Count ? CurrentSet[Index] : null;
    public int CorrectCount => Results.Count(r => r is TrackOutcome.Correct);
}

/// How the next set is built at a set end.
public enum SetChoice
{
    /// The same tracks again, reshuffled.
    Replay,
    /// The tracks missed in this set, filled up with new ones.
    KeepMisses,
    /// Only new tracks (not cleared, not in the finished set).
    AllNew,
}

public abstract record GameAction
{
    public sealed record Load(SourceRef Ref) : GameAction;
    public sealed record Loaded(SourceListing Listing) : GameAction;
    public sealed record LoadFailed(string Message) : GameAction;
    public sealed record SnippetFinished : GameAction;
    public sealed record Submit(Guess Guess) : GameAction;
    public sealed record Retry : GameAction;
    public sealed record GiveUp : GameAction;
    /// From PlayingSnippet/Guessing: give up this attempt without guessing and play the next,
    /// longer tier. At the last tier it behaves like GiveUp. Ignored elsewhere (in Wrong, Retry
    /// already does this).
    public sealed record Skip : GameAction;
    public sealed record Next : GameAction;
    /// SetComplete / SetFailed only: start the next set as <paramref name="Choice"/> says.
    public sealed record StartSet(SetChoice Choice) : GameAction;
    /// Alias of StartSet(AllNew).
    public sealed record NextSet : GameAction;
    /// Alias of StartSet(Replay).
    public sealed record ReplaySet : GameAction;
    /// Replay the current snippet from its start (PlayingSnippet/Guessing: doesn't use up an
    /// attempt), or restart the whole song from 0:00 (Correct/Revealed). Ignored elsewhere.
    public sealed record Restart : GameAction;
    public sealed record PlaybackFailed(string Message) : GameAction;
    public sealed record Configure(GameConfig Config) : GameAction;
    public sealed record Reset : GameAction;
}

public abstract record GameEffect
{
    public sealed record Fetch(SourceRef Ref) : GameEffect;
    /// Play Seconds of Track from Start, then pause and report SnippetFinished.
    public sealed record PlaySnippet(Track Track, double Start, double Seconds) : GameEffect;
    /// Cancel a running snippet and let the song play on from where it is.
    public sealed record ContinuePlaying : GameEffect;
    public sealed record Stop : GameEffect;
    /// Cancel any running snippet and play Track from the very start, continuing to the end.
    public sealed record RestartTrack(Track Track) : GameEffect;
    /// ClearedTrackIds changed; persist it.
    public sealed record PersistProgress : GameEffect;
}

public interface IAnswerJudge
{
    Verdict Judge(Guess guess, Track track);
}
