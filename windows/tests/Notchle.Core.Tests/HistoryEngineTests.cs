using Notchle.Core;
using static Notchle.Core.GameAction;
using Effect = Notchle.Core.GameEffect;
using Phase = Notchle.Core.GamePhase;

namespace Notchle.Core.Tests;

/// GameEffect.RecordOutcome: exactly once per track, on every path to an outcome, with the
/// track's wrong-guess and skip counters. Mirrors Tests/NotchleCoreTests/HistoryEngineTests.swift.
public class HistoryEngineTests
{
    private sealed class IdJudge : IAnswerJudge
    {
        public Verdict Judge(Guess guess, Track track) => new(guess.Title == track.Id, guess.Artist == "a");
    }

    private static readonly SourceRef Ref = new(SourceKind.Playlist, "pl");
    private static readonly GameAction WrongGuess = new Submit(new Guess("nope", "a"));

    private static GameEngine Engine(int tracks = 3, int setSize = 20)
    {
        var e = new GameEngine(GameConfig.Default with { SetSize = setSize }, new HashSet<string>(), new IdJudge(), 7);
        e.Send(new Load(Ref));
        e.Send(new Loaded(new SourceListing(Ref, "Test", Enumerable.Range(0, tracks)
            .Select(i => new Track($"t{i}", $"spotify:track:t{i}", $"Song {i}", ["a"], 200_000, null)).ToArray())));
        return e;
    }

    private static GameAction Right(GameEngine e) => new Submit(new Guess(e.State.CurrentTrack!.Id, "a"));

    private static List<Effect.RecordOutcome> Records(IEnumerable<GameEffect> effects) => effects.OfType<Effect.RecordOutcome>().ToList();

    /// Sends the actions, collecting every RecordOutcome.
    private static List<Effect.RecordOutcome> Run(GameEngine e, params GameAction[] actions) =>
        actions.SelectMany(a => Records(e.Send(a))).ToList();

    [Fact]
    public void CorrectFirstTryRecordsOnceLast()
    {
        var e = Engine();
        var track = e.State.CurrentTrack!;
        var effects = e.Send(Right(e));
        Assert.Equal(new Effect.RecordOutcome(track, new TrackOutcome.Correct(0), 0, 0), effects[^1]);
        Assert.Single(Records(effects));
        // Restart, then Next: nothing more for this track.
        Assert.Empty(Run(e, new Restart(), new Next()));
    }

    [Fact]
    public void CorrectAfterWrongAndSkipCountsBoth()
    {
        var e = Engine();
        var track = e.State.CurrentTrack!;
        var records = Run(e, WrongGuess, new Retry(), new Skip(), Right(e));
        Assert.Equal([new Effect.RecordOutcome(track, new TrackOutcome.Correct(2), 1, 1)], records);
    }

    [Fact]
    public void WrongAtTheLastTierRecordsMissedWithThreeWrong()
    {
        var e = Engine();
        var track = e.State.CurrentTrack!;
        var records = Run(e, WrongGuess, new Retry(), WrongGuess, new Retry(), WrongGuess);
        Assert.IsType<Phase.Revealed>(e.State.Phase);
        Assert.Equal([new Effect.RecordOutcome(track, new TrackOutcome.Missed(), 3, 0)], records);
    }

    [Theory]
    [InlineData(0)] // PlayingSnippet
    [InlineData(1)] // Guessing
    [InlineData(2)] // Wrong
    public void GiveUpRecordsMissed(int where)
    {
        var e = Engine();
        var track = e.State.CurrentTrack!;
        if (where >= 1) e.Send(new SnippetFinished());
        if (where == 2) e.Send(WrongGuess);
        var records = Run(e, new GiveUp());
        Assert.Equal([new Effect.RecordOutcome(track, new TrackOutcome.Missed(), where == 2 ? 1 : 0, 0)], records);
    }

    [Fact]
    public void SkipAtTheLastTierRecordsMissedOnceAndDoesNotCountAsSkip()
    {
        var e = Engine();
        var track = e.State.CurrentTrack!;
        Assert.Empty(Run(e, new Skip(), new Skip()));
        Assert.Equal([new Effect.RecordOutcome(track, new TrackOutcome.Missed(), 0, 2)], Run(e, new Skip()));
        Assert.Empty(Run(e, new Skip(), new GiveUp())); // ignored in Revealed
    }

    [Fact]
    public void NextFromAnErrorMidTrackRecordsMissedLast()
    {
        var e = Engine();
        var track = e.State.CurrentTrack!;
        e.Send(WrongGuess);
        e.Send(new PlaybackFailed("x"));
        var effects = e.Send(new Next());
        Assert.Equal(new Effect.RecordOutcome(track, new TrackOutcome.Missed(), 1, 0), effects[^1]);
        Assert.Single(Records(effects));
    }

    [Fact]
    public void ErrorAfterAnOutcomeDoesNotRecordAgain()
    {
        var e = Engine();
        Assert.Single(Run(e, Right(e)));
        Assert.Empty(Run(e, new PlaybackFailed("x"), new Next()));
    }

    [Fact]
    public void ErrorWhileLoadingRecordsNothing()
    {
        var e = new GameEngine(GameConfig.Default, new HashSet<string>(), new IdJudge(), 7);
        e.Send(new Load(Ref));
        Assert.Empty(Run(e, new LoadFailed("x"), new Next()));
    }

    [Fact]
    public void NonOutcomeActionsNeverRecord()
    {
        var e = Engine();
        Assert.Empty(Run(e, new SnippetFinished(), new Restart(), new Skip(), new SnippetFinished(), WrongGuess,
            new Restart(), new Retry(), new Configure(GameConfig.Default)));
    }

    [Fact]
    public void ExactlyOncePerTrackOverAWholeSetAndCountersResetPerTrack()
    {
        var e = Engine(tracks: 3);
        var records = new List<Effect.RecordOutcome>();
        // Track 0: wrong then right. Track 1: skip then give up. Track 2: right first try.
        records.AddRange(Run(e, WrongGuess, new Retry(), Right(e), new Next()));
        records.AddRange(Run(e, new Skip(), new GiveUp(), new Next()));
        records.AddRange(Run(e, Right(e), new Next()));
        Assert.IsType<Phase.SetFailed>(e.State.Phase);
        Assert.Equal(e.State.CurrentSet.Select(t => t.Id), records.Select(r => r.Track.Id));
        Assert.Equal([(1, 0), (0, 1), (0, 0)], records.Select(r => (r.WrongGuesses, r.Skips)));
        Assert.Equal([new TrackOutcome.Correct(1), new TrackOutcome.Missed(), new TrackOutcome.Correct(0)], records.Select(r => r.Outcome));
    }

    [Fact]
    public void ReplaySetRecordsEveryTrackAgainWithFreshCounters()
    {
        var e = Engine(tracks: 2);
        Run(e, WrongGuess, new GiveUp(), new Next(), new Skip(), new GiveUp(), new Next());
        Assert.IsType<Phase.SetFailed>(e.State.Phase);
        Assert.Empty(Run(e, new ReplaySet()));
        var again = Run(e, Right(e), new Next());
        again.AddRange(Run(e, Right(e), new Next())); // Right(e) reads the current track: one step at a time
        Assert.Equal(2, again.Count);
        Assert.All(again, r => Assert.Equal((0, 0, (TrackOutcome)new TrackOutcome.Correct(0)), (r.WrongGuesses, r.Skips, r.Outcome)));
    }

    [Fact]
    public void ReloadResetsTheCounters()
    {
        var e = Engine();
        e.Send(WrongGuess);
        e.Send(new Retry());
        e.Send(new Skip());
        e.Send(new Load(Ref));
        e.Send(new Loaded(new SourceListing(Ref, "Test", [new Track("z", "spotify:track:z", "Z", ["a"], 1, null)])));
        Assert.Equal([new Effect.RecordOutcome(e.State.CurrentTrack!, new TrackOutcome.Correct(0), 0, 0)], Run(e, Right(e)));
    }
}
