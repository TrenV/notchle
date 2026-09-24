using Notchle.Core;
using static Notchle.Core.GameAction;
using Effect = Notchle.Core.GameEffect;
using Phase = Notchle.Core.GamePhase;

namespace Notchle.Core.Tests;

/// Mirrors Tests/NotchleCoreTests/EngineTests.swift scenario for scenario.
public class EngineTests
{
    /// Judge stub: a guess is right when its title equals the track id and its artist is "a".
    private sealed class IdJudge : IAnswerJudge
    {
        public Verdict Judge(Guess guess, Track track) => new(guess.Title == track.Id, guess.Artist == "a");
    }

    private static readonly SourceRef Ref = new(SourceKind.Playlist, "pl");
    private static readonly GameAction WrongGuess = new Submit(new Guess("nope", "a"));
    private static readonly Verdict WrongVerdict = new(false, true);
    private static readonly HashSet<string> NoneCleared = [];

    private static SourceListing Listing(int count) =>
        new(Ref, "Test", Enumerable.Range(0, count)
            .Select(i => new Track($"t{i}", $"spotify:track:t{i}", $"Song {i}", ["a"], 200_000, null))
            .ToArray());

    private static GameAction Right(Track? track) => new Submit(new Guess(track?.Id ?? "", "a"));

    private static GameEngine Fresh(ulong seed = 1, GameConfig? config = null, IReadOnlySet<string>? cleared = null) =>
        new(config ?? GameConfig.Default, cleared ?? NoneCleared, new IdJudge(), seed);

    private static GameEngine Engine(int tracks = 25, ulong seed = 42, GameConfig? config = null, IReadOnlySet<string>? cleared = null)
    {
        var e = Fresh(seed, config, cleared);
        e.Send(new Load(Ref));
        e.Send(new Loaded(Listing(tracks)));
        return e;
    }

    /// Plays the current set to the end, answering right except at the given indices.
    private static IReadOnlyList<GameEffect> PlaySet(GameEngine e, params int[] missing)
    {
        IReadOnlyList<GameEffect> last = [];
        for (var i = 0; i < e.State.CurrentSet.Count; i++)
        {
            Assert.Equal(i, e.State.Index);
            e.Send(missing.Contains(i) ? new GiveUp() : Right(e.State.CurrentTrack));
            last = e.Send(new Next());
        }
        return last;
    }

    private static IEnumerable<string> Ids(GameEngine e) => e.State.CurrentSet.Select(t => t.Id);

    [Fact]
    public void LoadStopsAndFetches()
    {
        var e = Fresh();
        Assert.Equal([new Effect.Stop(), new Effect.Fetch(Ref)], e.Send(new Load(Ref)));
        Assert.Equal(new Phase.Loading(), e.State.Phase);
    }

    [Fact]
    public void LoadedStartsSetOneAtTierZero()
    {
        var e = Fresh(config: new GameConfig([5, 10, 15], 20, 7));
        e.Send(new Load(Ref));
        var effects = e.Send(new Loaded(Listing(30)));
        var first = e.State.CurrentTrack!;
        Assert.Equal([new Effect.PlaySnippet(first, 7, 5)], effects);
        Assert.Equal(new Phase.PlayingSnippet(0), e.State.Phase);
        Assert.Equal(1, e.State.SetNumber);
        Assert.Equal(20, e.State.CurrentSet.Count);
        Assert.Equal(20, Ids(e).Distinct().Count());
    }

    [Fact]
    public void LoadFailedGoesToErrorAndNextIsIgnored()
    {
        var e = Fresh();
        e.Send(new Load(Ref));
        Assert.Empty(e.Send(new LoadFailed("boom")));
        Assert.Equal(new Phase.Error("boom"), e.State.Phase);
        Assert.Empty(e.Send(new Next()));
        Assert.Equal(new Phase.Error("boom"), e.State.Phase);
        Assert.Equal([new Effect.Stop()], e.Send(new Reset()));
        Assert.Equal(new Phase.Idle(), e.State.Phase);
    }

    [Fact]
    public void FullTwentyOutOfTwentyCompletesAndPersists()
    {
        var e = Engine(tracks: 25);
        var set = e.State.CurrentSet;
        var celebrations = e.State.CelebrationCount;
        IReadOnlyList<GameEffect> lastEffects = [];
        for (var i = 0; i < 20; i++)
        {
            var current = e.State.CurrentTrack!;
            Assert.Equal([new Effect.ContinuePlaying(), new Effect.RecordOutcome(current, new TrackOutcome.Correct(0), 0, 0)],
                e.Send(Right(current)));
            Assert.Equal(new Phase.Correct(0), e.State.Phase);
            lastEffects = e.Send(new Next());
            if (i < 19) Assert.Equal([new Effect.PlaySnippet(set[i + 1], 0, 5)], lastEffects);
        }
        Assert.Equal([new Effect.Stop(), new Effect.PersistProgress()], lastEffects);
        Assert.Equal(new Phase.SetComplete(20), e.State.Phase);
        Assert.Equal(Enumerable.Repeat<TrackOutcome>(new TrackOutcome.Correct(0), 20), e.State.Results);
        Assert.Equal(celebrations + 20, e.State.CelebrationCount);
        Assert.True(e.State.ClearedTrackIds.SetEquals(set.Select(t => t.Id)));
        Assert.Null(e.State.CurrentTrack);
    }

    [Fact]
    public void OneMissFailsTheSetAndReplayReshufflesSameTracks()
    {
        var e = Engine(tracks: 25);
        var set = Ids(e).ToArray();
        Assert.Equal([new Effect.Stop(), new Effect.PersistProgress()], PlaySet(e, missing: 7));
        Assert.Equal(new Phase.SetFailed(19), e.State.Phase);
        // The 19 right answers are cleared even though the set failed; the miss is not.
        Assert.True(e.State.ClearedTrackIds.SetEquals(set.Where((_, i) => i != 7)));

        var effects = e.Send(new ReplaySet());
        Assert.Equal(new Phase.PlayingSnippet(0), e.State.Phase);
        Assert.Empty(e.State.Results);
        Assert.Equal(0, e.State.Index);
        Assert.Equal(1, e.State.SetNumber);
        Assert.True(Ids(e).ToHashSet().SetEquals(set));
        Assert.NotEqual(set, Ids(e)); // reshuffled (seed 42)
        Assert.Equal([new Effect.PlaySnippet(e.State.CurrentTrack!, 0, 5)], effects);
    }

    [Fact]
    public void TierEscalationFiveTenFifteenThenReveal()
    {
        var e = Engine();
        var track = e.State.CurrentTrack!;
        e.Send(new SnippetFinished());
        Assert.Empty(e.Send(WrongGuess)); // snippet already finished: nothing to stop
        Assert.Equal(new Phase.Wrong(0, WrongVerdict), e.State.Phase);
        Assert.Equal([new Effect.PlaySnippet(track, 0, 10)], e.Send(new Retry()));
        Assert.Equal(new Phase.PlayingSnippet(1), e.State.Phase);
        e.Send(new SnippetFinished());
        e.Send(WrongGuess);
        Assert.Equal([new Effect.PlaySnippet(track, 0, 15)], e.Send(new Retry()));
        e.Send(new SnippetFinished());
        Assert.Equal(new Phase.Guessing(2), e.State.Phase);
        Assert.Equal([new Effect.ContinuePlaying(), new Effect.RecordOutcome(track, new TrackOutcome.Missed(), 3, 0)],
            e.Send(WrongGuess));
        Assert.Equal(new Phase.Revealed(WrongVerdict), e.State.Phase);
        Assert.Equal([new TrackOutcome.Missed()], e.State.Results);
    }

    [Fact]
    public void RetryAfterTheLastTierIsIgnoredEvenIfTiersShrank()
    {
        var e = Engine();
        e.Send(WrongGuess);
        e.Send(new Configure(new GameConfig([5])));
        var before = e.State;
        Assert.Empty(e.Send(new Retry()));
        Assert.Same(before, e.State);
    }

    [Fact]
    public void CorrectAtLaterTierRecordsTier()
    {
        var e = Engine();
        e.Send(WrongGuess);
        e.Send(new Retry());
        e.Send(Right(e.State.CurrentTrack));
        Assert.Equal(new Phase.Correct(1), e.State.Phase);
        Assert.Equal([new TrackOutcome.Correct(1)], e.State.Results);
    }

    [Fact]
    public void SubmitMidSnippetStopsOnWrongAndContinuesOnRight()
    {
        var e = Engine();
        Assert.Equal([new Effect.Stop()], e.Send(WrongGuess));
        Assert.Equal(new Phase.Wrong(0, WrongVerdict), e.State.Phase);
        e.Send(new Retry());
        Assert.Equal([new Effect.ContinuePlaying(), new Effect.RecordOutcome(e.State.CurrentTrack!, new TrackOutcome.Correct(1), 1, 0)],
            e.Send(Right(e.State.CurrentTrack)));
    }

    [Fact]
    public void BlankGuessIsIgnoredButHalfBlankIsJudged()
    {
        var e = Engine();
        var before = e.State;
        Assert.Empty(e.Send(new Submit(new Guess("  ", "\n\t"))));
        Assert.Same(before, e.State);
        Assert.Equal([new Effect.Stop()], e.Send(new Submit(new Guess("", "a"))));
        Assert.Equal(new Phase.Wrong(0, new Verdict(false, true)), e.State.Phase);
    }

    [Fact]
    public void GiveUpRevealsWithLastWrongVerdictOrNull()
    {
        var e = Engine();
        Assert.Equal([new Effect.ContinuePlaying(), new Effect.RecordOutcome(e.State.CurrentTrack!, new TrackOutcome.Missed(), 0, 0)],
            e.Send(new GiveUp()));
        Assert.Equal(new Phase.Revealed(null), e.State.Phase);
        Assert.Equal([new TrackOutcome.Missed()], e.State.Results);

        e.Send(new Next());
        e.Send(WrongGuess);
        e.Send(new Retry()); // PlayingSnippet(1): verdict survives the retry
        e.Send(new GiveUp());
        Assert.Equal(new Phase.Revealed(WrongVerdict), e.State.Phase);

        e.Send(new Next()); // new track: previous verdict must not leak
        e.Send(new SnippetFinished());
        e.Send(new GiveUp());
        Assert.Equal(new Phase.Revealed(null), e.State.Phase);
    }

    [Fact]
    public void StaleSnippetFinishedIsIgnored()
    {
        var e = Engine();
        e.Send(WrongGuess);
        var wrongState = e.State;
        Assert.Empty(e.Send(new SnippetFinished()));
        Assert.Same(wrongState, e.State);

        e.Send(new Retry());
        e.Send(Right(e.State.CurrentTrack));
        var correctState = e.State;
        Assert.Empty(e.Send(new SnippetFinished()));
        Assert.Same(correctState, e.State);
    }

    [Fact]
    public void RestartWhilePlayingReplaysTheSameTierFromTheStart()
    {
        var e = Engine(config: new GameConfig([5, 10, 15], 20, 7));
        var track = e.State.CurrentTrack!;
        e.Send(WrongGuess);
        e.Send(new Retry()); // PlayingSnippet(1)
        var results = e.State.Results;

        Assert.Equal([new Effect.PlaySnippet(track, 7, 10)], e.Send(new Restart()));
        Assert.Equal(new Phase.PlayingSnippet(1), e.State.Phase);
        Assert.Same(results, e.State.Results);
        Assert.Equal(0, e.State.Index);
    }

    [Fact]
    public void RestartWhileGuessingReplaysWithoutUsingAnAttempt()
    {
        var e = Engine();
        var track = e.State.CurrentTrack!;
        e.Send(new SnippetFinished());
        Assert.Equal(new Phase.Guessing(0), e.State.Phase);

        Assert.Equal([new Effect.PlaySnippet(track, 0, 5)], e.Send(new Restart()));
        Assert.Equal(new Phase.PlayingSnippet(0), e.State.Phase);
        Assert.Empty(e.State.Results);
        Assert.Equal(0, e.State.Index);

        // As often as you like: still tier 0, still nothing recorded.
        e.Send(new SnippetFinished());
        Assert.Equal([new Effect.PlaySnippet(track, 0, 5)], e.Send(new Restart()));
        Assert.Equal(new Phase.PlayingSnippet(0), e.State.Phase);
        Assert.Empty(e.State.Results);

        // The replayed snippet finishing goes to Guessing at the same tier, and a wrong guess
        // still offers the 10 s retry: the replays cost nothing.
        e.Send(new SnippetFinished());
        Assert.Equal(new Phase.Guessing(0), e.State.Phase);
        e.Send(WrongGuess);
        Assert.Equal(new Phase.Wrong(0, WrongVerdict), e.State.Phase);
        Assert.Equal([new Effect.PlaySnippet(track, 0, 10)], e.Send(new Retry()));
    }

    /// The cancelled snippet's SnippetFinished can't reach the engine (the coordinator drops
    /// results of cancelled operations), but if one arrived it would only end the replay early:
    /// same tier, nothing recorded, and the next one is ignored.
    [Fact]
    public void StaleSnippetFinishedAfterRestartStaysAtTheSameTier()
    {
        var e = Engine();
        e.Send(new SnippetFinished());
        e.Send(new Restart());
        Assert.Empty(e.Send(new SnippetFinished()));
        Assert.Equal(new Phase.Guessing(0), e.State.Phase);
        var guessing = e.State;
        Assert.Empty(e.Send(new SnippetFinished()));
        Assert.Same(guessing, e.State);
        Assert.Empty(e.State.Results);
    }

    [Fact]
    public void RestartInCorrectOrRevealedRestartsTheSongAndKeepsThePhase()
    {
        var e = Engine();
        var track = e.State.CurrentTrack!;
        e.Send(Right(track));
        var correct = e.State;
        Assert.Equal([new Effect.RestartTrack(track)], e.Send(new Restart()));
        Assert.Same(correct, e.State);
        Assert.Equal(1, e.State.CelebrationCount); // no second celebration

        e.Send(new Next());
        var next = e.State.CurrentTrack!;
        e.Send(new GiveUp());
        var revealed = e.State;
        Assert.Equal([new Effect.RestartTrack(next)], e.Send(new Restart()));
        Assert.Same(revealed, e.State);
        Assert.Equal([new TrackOutcome.Correct(0), new TrackOutcome.Missed()], e.State.Results);
    }

    [Fact]
    public void RestartIsIgnoredInWrongAndEveryOtherPhase()
    {
        void AssertIgnored(GameEngine engine)
        {
            var before = engine.State;
            Assert.Empty(engine.Send(new Restart()));
            Assert.Same(before, engine.State);
        }

        AssertIgnored(Fresh()); // Idle
        var loading = Fresh();
        loading.Send(new Load(Ref));
        AssertIgnored(loading);

        var wrong = Engine();
        wrong.Send(WrongGuess);
        var wrongState = wrong.State;
        var replay = Assert.IsType<GameEffect.PlaySnippet>(Assert.Single(wrong.Send(new Restart())));
        Assert.Equal(5, replay.Seconds);
        Assert.Same(wrongState, wrong.State);            // still Wrong: Retry or Give up decide
        Assert.Empty(wrong.Send(new SnippetFinished())); // the replay's finish changes nothing
        Assert.Same(wrongState, wrong.State);

        var error = Engine();
        error.Send(new PlaybackFailed("x"));
        AssertIgnored(error);

        var complete = Engine(tracks: 20);
        PlaySet(complete);
        AssertIgnored(complete);

        var failed = Engine(tracks: 20);
        PlaySet(failed, missing: 0);
        AssertIgnored(failed);

        AssertIgnored(Engine(tracks: 10, cleared: Listing(10).Tracks.Select(t => t.Id).ToHashSet())); // Exhausted

        var shrunk = Engine();
        shrunk.Send(WrongGuess);
        shrunk.Send(new Retry());
        shrunk.Send(new SnippetFinished()); // Guessing(1)
        shrunk.Send(new Configure(new GameConfig([5])));
        AssertIgnored(shrunk); // that tier is gone
    }

    [Fact]
    public void SkipWhilePlayingPlaysTheNextTierAndRecordsNothing()
    {
        var e = Engine(config: new GameConfig([5, 10, 15], 20, 7));
        var track = e.State.CurrentTrack!;
        Assert.Equal([new Effect.PlaySnippet(track, 7, 10)], e.Send(new Skip()));
        Assert.Equal(new Phase.PlayingSnippet(1), e.State.Phase);
        Assert.Empty(e.State.Results);
        Assert.Equal(0, e.State.Index);
    }

    [Fact]
    public void SkipWhileGuessingPlaysTheNextTier()
    {
        var e = Engine();
        var track = e.State.CurrentTrack!;
        e.Send(new SnippetFinished());
        Assert.Equal([new Effect.PlaySnippet(track, 0, 10)], e.Send(new Skip()));
        Assert.Equal(new Phase.PlayingSnippet(1), e.State.Phase);
        Assert.Empty(e.State.Results);
    }

    [Fact]
    public void SkipFiveSkipTenThenCorrectAtFifteen()
    {
        var e = Engine();
        var track = e.State.CurrentTrack!;
        Assert.Equal([new Effect.PlaySnippet(track, 0, 10)], e.Send(new Skip()));  // mid-snippet
        e.Send(new SnippetFinished());
        Assert.Equal(new Phase.Guessing(1), e.State.Phase);
        Assert.Equal([new Effect.PlaySnippet(track, 0, 15)], e.Send(new Skip()));  // after it
        Assert.Equal(new Phase.PlayingSnippet(2), e.State.Phase);
        Assert.Equal([new Effect.ContinuePlaying(), new Effect.RecordOutcome(track, new TrackOutcome.Correct(2), 0, 2)],
            e.Send(Right(track)));
        Assert.Equal(new Phase.Correct(2), e.State.Phase);
        Assert.Equal([new TrackOutcome.Correct(2)], e.State.Results);
        Assert.Equal(1, e.State.CelebrationCount);
    }

    [Fact]
    public void SkipAtTheLastTierIsGiveUp()
    {
        var e = Engine();
        e.Send(new Skip());
        e.Send(new Skip()); // PlayingSnippet(2)
        Assert.Equal([new Effect.ContinuePlaying(), new Effect.RecordOutcome(e.State.CurrentTrack!, new TrackOutcome.Missed(), 0, 2)],
            e.Send(new Skip()));
        Assert.Equal(new Phase.Revealed(null), e.State.Phase);
        Assert.Equal([new TrackOutcome.Missed()], e.State.Results);

        // Exactly GiveUp, also after a wrong guess earlier on this track: same state, same effects.
        GameEngine AtLastTierAfterAWrongGuess()
        {
            var x = Engine();
            x.Send(WrongGuess);
            x.Send(new Retry());
            x.Send(new Skip()); // PlayingSnippet(2)
            x.Send(new SnippetFinished());
            return x;
        }
        var skipped = AtLastTierAfterAWrongGuess();
        var gaveUp = AtLastTierAfterAWrongGuess();
        Assert.Equal(gaveUp.Send(new GiveUp()), skipped.Send(new Skip()));
        Assert.Equal(gaveUp.State.Phase, skipped.State.Phase);
        Assert.Equal(new Phase.Revealed(WrongVerdict), skipped.State.Phase);
        Assert.Equal(gaveUp.State.Results, skipped.State.Results);
    }

    /// The coordinator drops the skipped snippet's result (AppCoordinatorTests); in the engine
    /// a SnippetFinished after a skip can only end the new tier, never go back, and one after a
    /// skip-reveal is ignored.
    [Fact]
    public void SnippetFinishedAfterASkipNeverGoesBackATier()
    {
        var e = Engine();
        e.Send(new Skip());
        e.Send(new SnippetFinished());
        Assert.Equal(new Phase.Guessing(1), e.State.Phase);
        var guessing = e.State;
        Assert.Empty(e.Send(new SnippetFinished()));
        Assert.Same(guessing, e.State);

        e.Send(new Skip());
        e.Send(new Skip()); // revealed
        var revealed = e.State;
        Assert.Empty(e.Send(new SnippetFinished()));
        Assert.Same(revealed, e.State);
    }

    [Fact]
    public void SkipIsIgnoredOutsidePlayingAndGuessing()
    {
        void AssertIgnored(GameEngine engine)
        {
            var before = engine.State;
            Assert.Empty(engine.Send(new Skip()));
            Assert.Same(before, engine.State);
        }

        AssertIgnored(Fresh());
        var loading = Fresh();
        loading.Send(new Load(Ref));
        AssertIgnored(loading);
        var wrong = Engine();
        wrong.Send(WrongGuess);
        AssertIgnored(wrong); // Retry is the skip here
        var correct = Engine();
        correct.Send(Right(correct.State.CurrentTrack));
        AssertIgnored(correct);
        var revealed = Engine();
        revealed.Send(new GiveUp());
        AssertIgnored(revealed);
        var error = Engine();
        error.Send(new PlaybackFailed("x"));
        AssertIgnored(error);
        var complete = Engine(tracks: 20);
        PlaySet(complete);
        AssertIgnored(complete);
        var failed = Engine(tracks: 20);
        PlaySet(failed, missing: 0);
        AssertIgnored(failed);
        AssertIgnored(Engine(tracks: 10, cleared: Listing(10).Tracks.Select(t => t.Id).ToHashSet()));
    }

    [Fact]
    public void NoRepeatsAcrossSetsThenSmallerFinalSetThenExhausted()
    {
        var e = Engine(tracks: 45);
        var seen = new List<string>();
        foreach (var expectedSize in new[] { 20, 20, 5 })
        {
            Assert.Equal(expectedSize, e.State.CurrentSet.Count);
            seen.AddRange(Ids(e));
            Assert.Equal([new Effect.Stop(), new Effect.PersistProgress()], PlaySet(e));
            var effects = e.Send(new NextSet());
            Assert.True(effects.Count == 0 || e.State.Phase == new Phase.PlayingSnippet(0));
        }
        Assert.Equal(45, seen.Count);
        Assert.Equal(45, seen.Distinct().Count());
        Assert.Equal(new Phase.Exhausted(), e.State.Phase);
        Assert.Equal(4, e.State.SetNumber);
        Assert.Equal(45, e.State.ClearedTrackIds.Count);
    }

    [Fact]
    public void DuplicateTracksInTheListingCollapseToOne()
    {
        var e = Fresh(seed: 3);
        e.Send(new Load(Ref));
        var tracks = Listing(3).Tracks;
        e.Send(new Loaded(new SourceListing(Ref, "Dupes", [.. tracks, .. tracks])));
        Assert.Equal(3, e.State.CurrentSet.Count);
        Assert.Equal(3, Ids(e).Distinct().Count());
    }

    [Fact]
    public void AlreadyClearedListingIsExhaustedOnLoad()
    {
        var all = Listing(10).Tracks.Select(t => t.Id).ToHashSet();
        var e = Engine(tracks: 10, cleared: all);
        Assert.Equal(new Phase.Exhausted(), e.State.Phase);
        Assert.Empty(e.State.CurrentSet);
    }

    [Fact]
    public void ClearedTracksAreSkippedOnLoad()
    {
        string[] cleared = ["t0", "t1", "t2", "t3", "t4"];
        var e = Engine(tracks: 25, cleared: cleared.ToHashSet());
        Assert.Equal(20, e.State.CurrentSet.Count);
        Assert.Empty(Ids(e).Intersect(cleared));
    }

    [Fact]
    public void SameSeedSameOrderDifferentSeedDifferentOrder()
    {
        var a = Ids(Engine(tracks: 50, seed: 7)).ToArray();
        var b = Ids(Engine(tracks: 50, seed: 7)).ToArray();
        var c = Ids(Engine(tracks: 50, seed: 8)).ToArray();
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.NotEqual(Listing(50).Tracks.Take(20).Select(t => t.Id), a); // actually shuffled
    }

    [Fact]
    public void SplitMix64MatchesReferenceOutput()
    {
        // Reference values for seed 0 from the published SplitMix64 algorithm (as in the Swift test).
        var rng = new SplitMix64(0);
        Assert.Equal(0xE220A8397B1DCDAFUL, rng.Next());
        Assert.Equal(0x6E789E6AA1B965F4UL, rng.Next());
    }

    [Fact]
    public void SplitMix64Seed42MatchesSwift()
    {
        // First outputs of the Swift SplitMix64(seed: 42), computed with a throwaway Swift
        // script on 2026-09-24 (also in /spec/shuffle-vectors.json).
        var rng = new SplitMix64(42);
        Assert.Equal(
            [13679457532755275413UL, 2949826092126892291UL, 5139283748462763858UL, 6349198060258255764UL, 701532786141963250UL],
            Enumerable.Range(0, 5).Select(_ => rng.Next()));
    }

    [Fact]
    public void PlaybackFailedThenNextCountsAsMissed()
    {
        var e = Engine();
        Assert.Empty(e.Send(new PlaybackFailed("no Spotify")));
        Assert.Equal(new Phase.Error("no Spotify"), e.State.Phase);
        var failed = e.State.CurrentTrack!;
        var effects = e.Send(new Next());
        Assert.Equal([new TrackOutcome.Missed()], e.State.Results);
        Assert.Equal(1, e.State.Index);
        Assert.Equal([new Effect.PlaySnippet(e.State.CurrentTrack!, 0, 5), new Effect.RecordOutcome(failed, new TrackOutcome.Missed(), 0, 0)],
            effects);
    }

    [Fact]
    public void PlaybackFailedAfterCorrectDoesNotDoubleCount()
    {
        var e = Engine();
        e.Send(Right(e.State.CurrentTrack));
        e.Send(new PlaybackFailed("x"));
        e.Send(new Next());
        Assert.Equal([new TrackOutcome.Correct(0)], e.State.Results);
    }

    [Fact]
    public void ConfigureTakesEffectAtNextSnippet()
    {
        var e = Engine();
        Assert.Empty(e.Send(new Configure(new GameConfig([2, 4, 6], 20, 30))));
        Assert.Equal(new Phase.PlayingSnippet(0), e.State.Phase);
        e.Send(WrongGuess);
        var track = e.State.CurrentTrack!;
        Assert.Equal([new Effect.PlaySnippet(track, 30, 4)], e.Send(new Retry()));
    }

    [Fact]
    public void EmptyTiersFallBackToTheDefaults()
    {
        var e = Engine(config: new GameConfig([]));
        Assert.Equal(new Phase.PlayingSnippet(0), e.State.Phase);
        e.Send(WrongGuess);
        Assert.Equal([new Effect.PlaySnippet(e.State.CurrentTrack!, 0, 10)], e.Send(new Retry()));
    }

    [Fact]
    public void ResetKeepsClearedTrackIds()
    {
        var e = Engine(tracks: 20);
        PlaySet(e);
        var cleared = e.State.ClearedTrackIds;
        Assert.Equal([new Effect.Stop()], e.Send(new Reset()));
        Assert.Equal(new Phase.Idle(), e.State.Phase);
        Assert.Null(e.State.Listing);
        Assert.Empty(e.State.CurrentSet);
        Assert.Empty(e.State.Results);
        Assert.True(e.State.ClearedTrackIds.SetEquals(cleared));
        Assert.Equal(20, cleared.Count);
    }

    [Fact]
    public void LoadMidGameRestarts()
    {
        var e = Engine();
        e.Send(Right(e.State.CurrentTrack));
        Assert.Equal([new Effect.Stop(), new Effect.Fetch(Ref)], e.Send(new Load(Ref)));
        Assert.Equal(new Phase.Loading(), e.State.Phase);
        Assert.Empty(e.State.CurrentSet);
        Assert.Empty(e.State.Results);
    }

    [Fact]
    public void CallerSetIsCopiedNotShared()
    {
        var mine = new HashSet<string>();
        var e = Engine(tracks: 20, cleared: mine);
        PlaySet(e);
        Assert.Empty(mine);
        Assert.Equal(20, e.State.ClearedTrackIds.Count);
    }

    [Fact]
    public void ActionsOutOfPhaseAreNoOps()
    {
        void AssertNoOps(GameEngine engine, params GameAction[] actions)
        {
            foreach (var action in actions)
            {
                var before = engine.State;
                Assert.Empty(engine.Send(action));
                Assert.Same(before, engine.State);
            }
        }

        AssertNoOps(Fresh(), new Loaded(Listing(3)), new SnippetFinished(), Right(null), new Retry(), new GiveUp(),
            new Next(), new NextSet(), new ReplaySet(), new StartSet(SetChoice.AllNew), new StartSet(SetChoice.KeepMisses),
            new StartSet(SetChoice.Replay), new LoadFailed("x"), new PlaybackFailed("x"));

        AssertNoOps(Engine(), new Retry(), new Next(), new NextSet(), new ReplaySet(), new StartSet(SetChoice.Replay),
            new StartSet(SetChoice.KeepMisses), new StartSet(SetChoice.AllNew), new Loaded(Listing(3)),
            new LoadFailed("x"));

        var correct = Engine();
        correct.Send(Right(correct.State.CurrentTrack));
        AssertNoOps(correct, Right(correct.State.CurrentTrack), new Retry(), new GiveUp(), new NextSet(), new ReplaySet(),
            new StartSet(SetChoice.AllNew));

        var complete = Engine(tracks: 20);
        PlaySet(complete);
        AssertNoOps(complete, new Next(), new GiveUp(), new PlaybackFailed("x"));
    }
}
