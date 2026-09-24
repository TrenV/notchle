using Notchle.Core;
using static Notchle.Core.GameAction;
using Effect = Notchle.Core.GameEffect;
using Phase = Notchle.Core.GamePhase;

namespace Notchle.Core.Tests;

/// End-of-set choices (Tren, 2026-09-24): replay the same 20, keep the misses and fill up with
/// new songs, or 20 new songs. Mirrors the Swift SetChoice tests.
public class EngineSetChoiceTests
{
    private sealed class IdJudge : IAnswerJudge
    {
        public Verdict Judge(Guess guess, Track track) => new(guess.Title == track.Id, guess.Artist == "a");
    }

    private static readonly SourceRef Ref = new(SourceKind.Playlist, "pl");

    private static SourceListing Listing(int count) =>
        new(Ref, "Test", Enumerable.Range(0, count)
            .Select(i => new Track($"t{i}", $"spotify:track:t{i}", $"Song {i}", ["a"], 200_000, null))
            .ToArray());

    private static GameEngine Engine(int tracks, ulong seed = 42, int setSize = 20)
    {
        var e = new GameEngine(GameConfig.Default with { SetSize = setSize }, new HashSet<string>(), new IdJudge(), seed);
        e.Send(new Load(Ref));
        e.Send(new Loaded(Listing(tracks)));
        return e;
    }

    /// Plays the set, missing the given indices; returns the ids missed.
    private static string[] PlaySet(GameEngine e, params int[] missing)
    {
        var missed = new List<string>();
        for (var i = 0; i < e.State.CurrentSet.Count; i++)
        {
            if (missing.Contains(i)) { missed.Add(e.State.CurrentTrack!.Id); e.Send(new GiveUp()); }
            else e.Send(new Submit(new Guess(e.State.CurrentTrack!.Id, "a")));
            e.Send(new Next());
        }
        return missed.ToArray();
    }

    private static string[] Ids(GameEngine e) => e.State.CurrentSet.Select(t => t.Id).ToArray();

    /// A finished set: complete (no misses) or failed (misses at 2, 5, 11).
    private static (GameEngine E, string[] Set, string[] Missed) Finished(bool complete, int tracks = 60, ulong seed = 42)
    {
        var e = Engine(tracks, seed);
        var set = Ids(e);
        var missed = complete ? PlaySet(e) : PlaySet(e, 2, 5, 11);
        Assert.IsType(complete ? typeof(Phase.SetComplete) : typeof(Phase.SetFailed), e.State.Phase);
        return (e, set, missed);
    }

    public static TheoryData<bool> BothEnds => new() { true, false };

    [Theory]
    [MemberData(nameof(BothEnds))]
    public void ReplayReshufflesTheSameTracksAndKeepsTheSetNumber(bool complete)
    {
        var (e, set, _) = Finished(complete);
        var effects = e.Send(new StartSet(SetChoice.Replay));
        Assert.Equal(new Phase.PlayingSnippet(0), e.State.Phase);
        Assert.True(Ids(e).ToHashSet().SetEquals(set));
        Assert.NotEqual(set, Ids(e));
        Assert.Empty(e.State.Results);
        Assert.Equal((0, 1), (e.State.Index, e.State.SetNumber));
        Assert.Equal([new Effect.PlaySnippet(e.State.CurrentTrack!, 0, 5)], effects);
    }

    [Theory]
    [MemberData(nameof(BothEnds))]
    public void AllNewDealsOnlyUnclearedTracksOutsideTheFinishedSet(bool complete)
    {
        var (e, set, _) = Finished(complete);
        var cleared = e.State.ClearedTrackIds.ToHashSet();
        e.Send(new StartSet(SetChoice.AllNew));
        Assert.Equal(new Phase.PlayingSnippet(0), e.State.Phase);
        Assert.Equal(20, e.State.CurrentSet.Count);
        Assert.Equal(20, Ids(e).Distinct().Count());
        Assert.Empty(Ids(e).Intersect(set));
        Assert.Empty(Ids(e).Where(cleared.Contains));
        Assert.Equal(2, e.State.SetNumber);
        Assert.Empty(e.State.Results);
    }

    [Fact]
    public void KeepMissesCarriesTheMissesAndFillsUpWithNewTracks()
    {
        var (e, set, missed) = Finished(complete: false);
        e.Send(new StartSet(SetChoice.KeepMisses));
        var next = Ids(e);
        Assert.Equal(20, next.Length);
        Assert.Equal(20, next.Distinct().Count());
        Assert.Subset(next.ToHashSet(), missed.ToHashSet());
        var fresh = next.Except(missed).ToArray();
        Assert.Equal(17, fresh.Length);
        Assert.Empty(fresh.Intersect(set));
        Assert.Empty(fresh.Where(e.State.ClearedTrackIds.Contains));
        Assert.Equal(2, e.State.SetNumber);
        Assert.Equal(new Phase.PlayingSnippet(0), e.State.Phase);
    }

    [Fact]
    public void KeepMissesShufflesTheCarriedMissesIn()
    {
        // Across seeds the misses do not always lead the new set.
        var leading = Enumerable.Range(1, 8).Select(seed =>
        {
            var (e, _, missed) = Finished(complete: false, seed: (ulong)seed);
            e.Send(new StartSet(SetChoice.KeepMisses));
            return Ids(e).Take(3).ToHashSet().SetEquals(missed);
        });
        Assert.Contains(false, leading);
    }

    [Fact]
    public void KeepMissesAtSetCompleteEqualsAllNew()
    {
        var (a, _, _) = Finished(complete: true, seed: 7);
        var (b, _, _) = Finished(complete: true, seed: 7);
        a.Send(new StartSet(SetChoice.KeepMisses));
        b.Send(new StartSet(SetChoice.AllNew));
        Assert.Equal(Ids(b), Ids(a));
        Assert.Equal(b.State.SetNumber, a.State.SetNumber);
    }

    [Fact]
    public void KeepMissesWithFewNewTracksGivesASmallerSet()
    {
        var e = Engine(tracks: 22);
        var missed = PlaySet(e, 0, 1, 2, 3);
        e.Send(new StartSet(SetChoice.KeepMisses));
        Assert.Equal(6, e.State.CurrentSet.Count); // 4 misses + the 2 tracks never dealt
        Assert.Subset(Ids(e).ToHashSet(), missed.ToHashSet());
    }

    [Fact]
    public void KeepMissesWithNoNewTracksIsJustTheMisses()
    {
        var e = Engine(tracks: 20);
        var missed = PlaySet(e, 4, 9);
        Assert.Equal(0, GameEngine.AvailableNewCount(e.State));
        e.Send(new StartSet(SetChoice.KeepMisses));
        Assert.True(Ids(e).ToHashSet().SetEquals(missed));
        Assert.Equal(new Phase.PlayingSnippet(0), e.State.Phase);
    }

    [Fact]
    public void AllNewWithFewerLeftGivesASmallerSet()
    {
        var e = Engine(tracks: 27);
        PlaySet(e, 0);
        Assert.Equal(7, GameEngine.AvailableNewCount(e.State));
        e.Send(new StartSet(SetChoice.AllNew));
        Assert.Equal(7, e.State.CurrentSet.Count);
    }

    [Theory]
    [MemberData(nameof(BothEnds))]
    public void AllNewWithNothingLeftIsExhausted(bool complete)
    {
        var e = Engine(tracks: 20);
        if (complete) PlaySet(e); else PlaySet(e, 3);
        Assert.Equal(0, GameEngine.AvailableNewCount(e.State));
        Assert.Empty(e.Send(new StartSet(SetChoice.AllNew)));
        Assert.Equal(new Phase.Exhausted(), e.State.Phase);
        Assert.Empty(e.State.CurrentSet);
    }

    [Fact]
    public void FailedSetsGrowClearedAndPersist()
    {
        var e = Engine(tracks: 60);
        var set = Ids(e);
        e.Send(new Submit(new Guess(e.State.CurrentTrack!.Id, "a")));
        e.Send(new Next());
        for (var i = 1; i < 20; i++) { e.Send(new GiveUp()); var last = e.Send(new Next()); if (i == 19) Assert.Equal([new Effect.Stop(), new Effect.PersistProgress()], last); }
        Assert.Equal(new Phase.SetFailed(1), e.State.Phase);
        Assert.Equal([set[0]], e.State.ClearedTrackIds.ToArray());

        // A replay of the same set, now 3 right: cleared grows to those, never shrinks.
        e.Send(new StartSet(SetChoice.Replay));
        var replay = Ids(e);
        PlaySet(e, Enumerable.Range(3, 17).ToArray());
        Assert.True(e.State.ClearedTrackIds.SetEquals(replay.Take(3).Append(set[0])));
    }

    [Fact]
    public void AvailableNewCountIsCappedAtSetSizeAndExcludesTheSet()
    {
        var e = Engine(tracks: 60);
        Assert.Equal(20, GameEngine.AvailableNewCount(e.State)); // 40 outside the set, capped
        PlaySet(e);
        Assert.Equal(20, GameEngine.AvailableNewCount(e.State));
        Assert.Equal(0, GameEngine.AvailableNewCount(e.State with { Listing = null }));
    }

    [Theory]
    [InlineData(SetChoice.Replay)]
    [InlineData(SetChoice.KeepMisses)]
    [InlineData(SetChoice.AllNew)]
    public void SameSeedSameChoicesSameSets(SetChoice choice)
    {
        string[] Run()
        {
            var (e, _, _) = Finished(complete: false, seed: 99);
            e.Send(new StartSet(choice));
            return Ids(e);
        }
        Assert.Equal(Run(), Run());
    }

    [Theory]
    [MemberData(nameof(BothEnds))]
    public void AliasesMatchTheirChoices(bool complete)
    {
        var (a, _, _) = Finished(complete, seed: 5);
        var (b, _, _) = Finished(complete, seed: 5);
        Assert.Equal(b.Send(new StartSet(SetChoice.AllNew)), a.Send(new NextSet()));
        Assert.Equal(Ids(b), Ids(a));
        Assert.Equal((b.State.SetNumber, b.State.Phase), (a.State.SetNumber, a.State.Phase));

        var (c, _, _) = Finished(complete, seed: 5);
        var (d, _, _) = Finished(complete, seed: 5);
        Assert.Equal(d.Send(new StartSet(SetChoice.Replay)), c.Send(new ReplaySet()));
        Assert.Equal(Ids(d), Ids(c));
    }

    [Fact]
    public void NoDuplicatesAcrossRepeatedKeepMisses()
    {
        var e = Engine(tracks: 100);
        for (var round = 0; round < 5; round++)
        {
            PlaySet(e, 0, 7);
            e.Send(new StartSet(SetChoice.KeepMisses));
            Assert.Equal(Ids(e).Length, Ids(e).Distinct().Count());
            Assert.Empty(Ids(e).Where(e.State.ClearedTrackIds.Contains));
        }
    }

    [Fact]
    public void StartSetIsIgnoredOutsideTheSetEnd()
    {
        var e = Engine(tracks: 30);
        foreach (var choice in Enum.GetValues<SetChoice>())
        {
            var before = e.State;
            Assert.Empty(e.Send(new StartSet(choice)));
            Assert.Same(before, e.State);
        }
    }
}
