namespace Notchle.Core;

/// The game as a pure reducer; port of Sources/NotchleCore/Engine/GameEngine.swift with the same
/// transitions and effects. See the header of Contracts/Game.cs for the rules.
///
/// Integrator decisions (2026-09-24), as in Swift:
/// - Every shuffle draws from a SplitMix64 seeded with `seed`: same seed + same actions = same
///   order, on both platforms.
/// - A set is SetSize tracks drawn from the listing's tracks not yet in ClearedTrackIds
///   (duplicates in the listing collapse to one). Fewer left gives a smaller final set; none
///   left gives Exhausted.
/// - Configure takes effect at the next snippet (and the next set for SetSize).
/// - Anything not listed for the current phase is a no-op: State is left untouched (same
///   instance) and no effects are returned.
public sealed class GameEngine
{
    private static readonly IReadOnlyList<GameEffect> None = Array.Empty<GameEffect>();

    private readonly IAnswerJudge _judge;
    private SplitMix64 _rng;
    /// The last wrong verdict for the current track; shown on reveal. Cleared per track.
    private Verdict? _lastVerdict;

    public GameState State { get; private set; }

    /// `seed` drives every shuffle (SplitMix64, same algorithm as the Swift engine).
    public GameEngine(GameConfig config, IReadOnlySet<string> clearedTrackIds, IAnswerJudge judge, ulong seed)
    {
        _judge = judge;
        _rng = new SplitMix64(seed);
        State = new GameState { Config = config, ClearedTrackIds = new HashSet<string>(clearedTrackIds) };
    }

    /// Applies the action and returns effects in order. Out-of-phase actions: no change, no effects.
    public IReadOnlyList<GameEffect> Send(GameAction action)
    {
        var phase = State.Phase;
        switch (action)
        {
            case GameAction.Load load:
                ClearSession();
                State = State with { Phase = new GamePhase.Loading() };
                return [new GameEffect.Stop(), new GameEffect.Fetch(load.Ref)];

            case GameAction.Loaded loaded when phase is GamePhase.Loading:
                State = State with { Listing = loaded.Listing, SetNumber = 1 };
                return StartNewSet();

            case GameAction.LoadFailed failed when phase is GamePhase.Loading:
                State = State with { Phase = new GamePhase.Error(failed.Message) };
                return None;

            case GameAction.SnippetFinished when phase is GamePhase.PlayingSnippet playing:
                State = State with { Phase = new GamePhase.Guessing(playing.TierIndex) };
                return None;

            case GameAction.Submit submit when phase is GamePhase.PlayingSnippet playing:
                return Submit(submit.Guess, playing.TierIndex, snippetPlaying: true);

            case GameAction.Submit submit when phase is GamePhase.Guessing guessing:
                return Submit(submit.Guess, guessing.TierIndex, snippetPlaying: false);

            case GameAction.Retry when phase is GamePhase.Wrong wrong:
                var nextTier = wrong.TierIndex + 1;
                return nextTier < Tiers.Count ? PlaySnippet(nextTier) : None;

            // Replaying the snippet keeps the tier and records nothing; SnippetStart and the tier
            // length are re-read, so a Configure since the snippet started applies here too. A
            // tier that no longer exists (tiers shrank) is ignored, as Retry does.
            case GameAction.Restart when phase is GamePhase.PlayingSnippet playing && playing.TierIndex < Tiers.Count:
                return PlaySnippet(playing.TierIndex);

            case GameAction.Restart when phase is GamePhase.Guessing guessing && guessing.TierIndex < Tiers.Count:
                return PlaySnippet(guessing.TierIndex);

            case GameAction.Restart when phase is GamePhase.Correct or GamePhase.Revealed:
                return State.CurrentTrack is { } track ? [new GameEffect.RestartTrack(track)] : None;

            // Skip spends this attempt: the next tier plays, nothing is recorded (the outcome is
            // recorded once, when the track is won or lost). No longer tier left: GiveUp.
            case GameAction.Skip when phase is GamePhase.PlayingSnippet or GamePhase.Guessing:
            {
                var tier = phase is GamePhase.PlayingSnippet p ? p.TierIndex : ((GamePhase.Guessing)phase).TierIndex;
                return tier + 1 < Tiers.Count ? PlaySnippet(tier + 1) : Reveal(_lastVerdict);
            }

            case GameAction.GiveUp when phase is GamePhase.PlayingSnippet or GamePhase.Guessing or GamePhase.Wrong:
                return Reveal(_lastVerdict);

            case GameAction.Next when phase is GamePhase.Correct or GamePhase.Revealed or GamePhase.Error:
                return Advance();

            case GameAction.NextSet when phase is GamePhase.SetComplete:
                State = State with { SetNumber = State.SetNumber + 1 };
                return StartNewSet();

            case GameAction.ReplaySet when phase is GamePhase.SetFailed:
                State = State with { CurrentSet = _rng.Shuffled(State.CurrentSet), Results = Array.Empty<TrackOutcome>() };
                return StartTrack(0);

            case GameAction.PlaybackFailed failed when HasActiveTrack(phase):
                State = State with { Phase = new GamePhase.Error(failed.Message) };
                return None;

            case GameAction.Configure configure:
                State = State with { Config = configure.Config };
                return None;

            case GameAction.Reset:
                ClearSession();
                State = State with { Phase = new GamePhase.Idle() };
                return [new GameEffect.Stop()];

            default:
                return None;
        }
    }

    // Steps

    private IReadOnlyList<double> Tiers => State.Config.Tiers.Count == 0 ? GameConfig.Default.Tiers : State.Config.Tiers;

    private void ClearSession()
    {
        State = State with
        {
            Listing = null,
            CurrentSet = Array.Empty<Track>(),
            Index = 0,
            Results = Array.Empty<TrackOutcome>(),
            SetNumber = 0,
        };
        _lastVerdict = null;
    }

    private IReadOnlyList<GameEffect> StartNewSet()
    {
        if (State.Listing is not { } listing) return None;
        var seen = new HashSet<string>();
        var pool = listing.Tracks
            .Where(track => !State.ClearedTrackIds.Contains(track.Id) && seen.Add(track.Id))
            .ToArray();
        State = State with { Results = Array.Empty<TrackOutcome>(), Index = 0 };
        if (pool.Length == 0)
        {
            State = State with { CurrentSet = Array.Empty<Track>(), Phase = new GamePhase.Exhausted() };
            return None;
        }
        var size = Math.Max(1, State.Config.SetSize);
        State = State with { CurrentSet = _rng.Shuffled(pool).Take(size).ToArray() };
        return StartTrack(0);
    }

    private IReadOnlyList<GameEffect> StartTrack(int index)
    {
        State = State with { Index = index };
        _lastVerdict = null;
        return PlaySnippet(0);
    }

    private IReadOnlyList<GameEffect> PlaySnippet(int tier)
    {
        if (State.CurrentTrack is not { } track) return None;
        State = State with { Phase = new GamePhase.PlayingSnippet(tier) };
        return [new GameEffect.PlaySnippet(track, State.Config.SnippetStart, Tiers[tier])];
    }

    private IReadOnlyList<GameEffect> Submit(Guess guess, int tier, bool snippetPlaying)
    {
        if (State.CurrentTrack is not { } track) return None;
        if (string.IsNullOrWhiteSpace(guess.Title) && string.IsNullOrWhiteSpace(guess.Artist)) return None;

        var verdict = _judge.Judge(guess, track);
        if (verdict.IsCorrect)
        {
            State = State with
            {
                Phase = new GamePhase.Correct(tier),
                Results = [.. State.Results, new TrackOutcome.Correct(tier)],
                CelebrationCount = State.CelebrationCount + 1,
            };
            return [new GameEffect.ContinuePlaying()];
        }
        _lastVerdict = verdict;
        if (tier + 1 < Tiers.Count)
        {
            State = State with { Phase = new GamePhase.Wrong(tier, verdict) };
            return snippetPlaying ? [new GameEffect.Stop()] : None;
        }
        return Reveal(verdict);
    }

    private IReadOnlyList<GameEffect> Reveal(Verdict? verdict)
    {
        State = State with
        {
            Phase = new GamePhase.Revealed(verdict),
            Results = [.. State.Results, new TrackOutcome.Missed()],
        };
        return [new GameEffect.ContinuePlaying()];
    }

    private IReadOnlyList<GameEffect> Advance()
    {
        if (State.CurrentSet.Count == 0) return None; // error while loading: nothing to skip
        // An error mid-track (before an outcome was recorded) counts as a miss.
        if (State.Results.Count <= State.Index)
            State = State with { Results = [.. State.Results, new TrackOutcome.Missed()] };

        var nextIndex = State.Index + 1;
        if (nextIndex < State.CurrentSet.Count) return StartTrack(nextIndex);

        State = State with { Index = State.CurrentSet.Count };
        var correct = State.CorrectCount;
        if (correct == State.CurrentSet.Count)
        {
            State = State with
            {
                Phase = new GamePhase.SetComplete(correct),
                ClearedTrackIds = new HashSet<string>(State.ClearedTrackIds.Concat(State.CurrentSet.Select(t => t.Id))),
            };
            return [new GameEffect.Stop(), new GameEffect.PersistProgress()];
        }
        State = State with { Phase = new GamePhase.SetFailed(correct) };
        return [new GameEffect.Stop()];
    }

    /// Phases in which a track is loaded into the player, so a playback failure is meaningful.
    private static bool HasActiveTrack(GamePhase phase) =>
        phase is GamePhase.PlayingSnippet or GamePhase.Guessing or GamePhase.Wrong or GamePhase.Correct or GamePhase.Revealed;
}
