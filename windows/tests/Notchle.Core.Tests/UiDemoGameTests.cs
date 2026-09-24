using Notchle.Core.Ui;

namespace Notchle.Core.Tests;

/// The fake game behind --ui-demo reaches every screen.
public class UiDemoGameTests
{
    private GameState _state = new() { Config = GameConfig.Default };
    private readonly List<(TimeSpan Delay, Action Run)> _queue = [];
    private readonly IslandDemoGame _game;

    public UiDemoGameTests() => _game = new IslandDemoGame(() => _state, s => _state = s, (d, a) => _queue.Add((d, a)));

    private void RunScheduled()
    {
        var items = _queue.ToList();
        _queue.Clear();
        foreach (var (_, run) in items) run();
    }

    [Fact]
    public void LoadPlaysGuessesAndCelebrates()
    {
        _game.Send(new GameAction.Load(IslandDemoGame.DemoSource));
        Assert.IsType<GamePhase.Loading>(_state.Phase);
        RunScheduled();
        Assert.Equal(new GamePhase.PlayingSnippet(0), _state.Phase);
        Assert.Equal(20, _state.CurrentSet.Count);
        RunScheduled();
        Assert.Equal(new GamePhase.Guessing(0), _state.Phase);

        _game.Send(new GameAction.Submit(new Guess("paper lanterns", "nobody")));
        Assert.Equal(new GamePhase.Wrong(0, new Verdict(true, false)), _state.Phase);
        _game.Send(new GameAction.Retry());
        Assert.Equal(new GamePhase.PlayingSnippet(1), _state.Phase);
        _game.Send(new GameAction.Submit(new Guess("Paper Lanterns", "the midnight kites")));
        Assert.Equal(new GamePhase.Correct(1), _state.Phase);
        Assert.Equal(1, _state.CelebrationCount);
        RunScheduled(); // the stale snippet timer of tier 1 must not flip Correct to Guessing
        Assert.Equal(new GamePhase.Correct(1), _state.Phase);
    }

    [Fact]
    public void RestartReplaysTheSnippetAtTheSameTierAndIsIgnoredInWrong()
    {
        _state = IslandDemoGame.SampleState(new GamePhase.Guessing(1));
        _game.Send(new GameAction.Restart());
        Assert.Equal(new GamePhase.PlayingSnippet(1), _state.Phase);
        _game.Send(new GameAction.Restart()); // again mid-snippet: the first timer is stale now
        RunScheduled();
        Assert.Equal(new GamePhase.Guessing(1), _state.Phase);
        Assert.Empty(_queue);

        var wrong = IslandDemoGame.SampleState(new GamePhase.Wrong(0, new Verdict(true, false)));
        _state = wrong;
        _game.Send(new GameAction.Restart());
        Assert.Same(wrong, _state);

        var correct = IslandDemoGame.SampleState(new GamePhase.Correct(0));
        _state = correct;
        _game.Send(new GameAction.Restart());
        Assert.Same(correct, _state);
    }

    [Fact]
    public void SkipPlaysTheNextTierThenRevealsAtTheLast()
    {
        _state = IslandDemoGame.SampleState(new GamePhase.Guessing(0));
        _game.Send(new GameAction.Skip());
        Assert.Equal(new GamePhase.PlayingSnippet(1), _state.Phase);
        _game.Send(new GameAction.Skip());
        Assert.Equal(new GamePhase.PlayingSnippet(2), _state.Phase);
        Assert.Empty(_state.Results);
        _game.Send(new GameAction.Skip());
        Assert.Equal(new GamePhase.Revealed(null), _state.Phase);
        Assert.Equal([new TrackOutcome.Missed()], _state.Results);
        RunScheduled(); // stale snippet timers of tiers 1 and 2
        Assert.Equal(new GamePhase.Revealed(null), _state.Phase);
    }

    [Fact]
    public void SeveralArtistsAreAllNeededInAnyOrder()
    {
        _state = IslandDemoGame.SampleState(new GamePhase.Guessing(0), index: 5);
        _game.Send(new GameAction.Submit(new Guess("Northbound", "Tove Ahlberg")));
        Assert.Equal(new GamePhase.Wrong(0, new Verdict(true, false)), _state.Phase);
        _game.Send(new GameAction.Retry());
        _game.Send(new GameAction.Submit(new Guess("Northbound", "kasper ruud & tove ahlberg")));
        Assert.Equal(new GamePhase.Correct(1), _state.Phase);
    }

    [Fact]
    public void SetEndsExhaustionErrorAndReset()
    {
        _state = IslandDemoGame.SampleState(new GamePhase.Correct(0));
        _game.ForceSetEnd(complete: true);
        Assert.Equal(new GamePhase.SetComplete(20), _state.Phase);
        _game.Send(new GameAction.NextSet());
        Assert.IsType<GamePhase.Exhausted>(_state.Phase);
        _game.Send(new GameAction.Load(IslandDemoGame.ErrorSource));
        RunScheduled();
        Assert.IsType<GamePhase.Error>(_state.Phase);
        _game.Send(new GameAction.Reset());
        Assert.IsType<GamePhase.Idle>(_state.Phase);
        Assert.Empty(_state.CurrentSet);
    }

    [Fact]
    public void DemoLinkParser()
    {
        Assert.Equal(IslandDemoGame.DemoSource, IslandDemoGame.Parse("https://open.spotify.com/playlist/x"));
        Assert.Equal(IslandDemoGame.ErrorSource, IslandDemoGame.Parse(" Error "));
        Assert.Null(IslandDemoGame.Parse("https://example.com"));
    }
}
