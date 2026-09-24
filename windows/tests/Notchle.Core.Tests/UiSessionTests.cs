using Notchle.Core.Ui;

namespace Notchle.Core.Tests;

/// Port of Tests/NotchleMacTests/UIStateTests.swift for the Windows island, plus the Windows
/// rule that phase changes never expand it.
public class UiSessionTests
{
    private readonly UiManualClock _clock = new();
    private readonly List<GameAction> _sent = [];

    private IslandSession Make(GamePhase phase, out GameState state)
    {
        state = UiFixtures.State(phase);
        var s = new IslandSession(_clock) { Send = _sent.Add };
        s.StateDidChange(null, state);
        return s;
    }

    [Fact]
    public void NoConfettiOnLaunchEvenWithACount()
    {
        var s = new IslandSession(_clock);
        Assert.False(s.StateDidChange(null, UiFixtures.State(new GamePhase.Correct(0)) with { CelebrationCount = 7 }));
        Assert.Null(s.CelebrationStart);
    }

    [Fact]
    public void ConfettiOnlyWhenTheCountIncreases()
    {
        var s = Make(new GamePhase.Guessing(0), out var before);
        var seed = s.CelebrationSeed;
        var after = before with { Phase = new GamePhase.Correct(0), CelebrationCount = before.CelebrationCount + 1 };
        Assert.True(s.StateDidChange(before, after));
        Assert.Equal(_clock.Now, s.CelebrationStart);
        Assert.NotEqual(seed, s.CelebrationSeed);

        var next = after with { Phase = new GamePhase.PlayingSnippet(0) };
        Assert.False(s.StateDidChange(after, next));
        Assert.False(s.StateDidChange(next, next with { CelebrationCount = 0 }));
    }

    [Fact]
    public void PhaseChangesNeverExpandTheIsland()
    {
        var s = Make(new GamePhase.Idle(), out var state);
        foreach (var phase in UiFixtures.AllPhases)
        {
            var next = state with { Phase = phase, CelebrationCount = state.CelebrationCount + 1 };
            s.StateDidChange(state, next);
            state = next;
            Assert.False(s.Behavior.IsExpanded);
            Assert.False(s.Behavior.HasKeyboard);
        }
    }

    [Fact]
    public void ModeIsLipWhenIdleCompactInAGameExpandedWhenOpen()
    {
        var s = Make(new GamePhase.Idle(), out var state);
        Assert.Equal(IslandMode.Lip, s.Mode);
        s.StateDidChange(state, state with { Phase = new GamePhase.PlayingSnippet(0) });
        Assert.Equal(IslandMode.Compact, s.Mode);
        s.Behavior.PointerMoved(true);
        Assert.Equal(IslandMode.Expanded, s.Mode);
    }

    [Fact]
    public void NewTrackClearsFieldsAndFocusesTitle()
    {
        var s = Make(new GamePhase.Correct(0), out var state);
        s.TitleText = "old";
        s.ArtistText = "old";
        var token = s.FocusToken;
        s.StateDidChange(state, state with { Phase = new GamePhase.PlayingSnippet(0) });
        Assert.Equal(("", ""), (s.TitleText, s.ArtistText));
        Assert.Equal(IslandField.Title, s.RequestedFocus);
        Assert.NotEqual(token, s.FocusToken);
        Assert.Equal(_clock.Now, s.SnippetStart);
    }

    [Fact]
    public void RetryKeepsFieldsAndFocusesTheWrongHalf()
    {
        var s = Make(new GamePhase.Wrong(0, new Verdict(true, false)), out var state);
        s.TitleText = "T";
        s.ArtistText = "B";
        s.StateDidChange(state, state with { Phase = new GamePhase.PlayingSnippet(1) });
        Assert.Equal(("T", "B"), (s.TitleText, s.ArtistText));
        Assert.Equal(IslandField.Artist, s.RequestedFocus);
    }

    [Fact]
    public void ReplayWhileGuessingKeepsTextAndFocusAndRestartsTheRing()
    {
        var s = Make(new GamePhase.Guessing(0), out var state);
        s.TitleText = "Paper";
        s.ArtistText = "Kit";
        s.FocusedField = IslandField.Artist;
        var token = s.FocusToken;
        _clock.Advance(7);

        Assert.True(s.HandleKey(IslandKey.CtrlShiftR));
        Assert.Equal([new GameAction.Restart()], _sent);
        Assert.Equal(_clock.Now, s.SnippetStart);

        _clock.Advance(0.05); // the engine's new state arrives a moment later
        s.StateDidChange(state, state with { Phase = new GamePhase.PlayingSnippet(0) });
        Assert.Equal(("Paper", "Kit"), (s.TitleText, s.ArtistText));
        Assert.Equal(token, s.FocusToken); // no focus request: focus stays in Artist
        Assert.Equal(IslandField.Artist, s.FocusedField);
    }

    [Fact]
    public void ReplayMidSnippetRestartsTheRingAlthoughThePhaseStaysTheSame()
    {
        var s = Make(new GamePhase.PlayingSnippet(1), out _);
        var started = s.SnippetStart;
        _clock.Advance(4);
        s.Perform(new IslandCommand.Restart());
        Assert.Equal([new GameAction.Restart()], _sent);
        Assert.NotEqual(started, s.SnippetStart);
        Assert.Equal(_clock.Now, s.SnippetStart);
        Assert.Equal(0, s.Indicator().Progress);
    }

    [Fact]
    public void RestartInCorrectSendsRestartAndLeavesTheSnippetClockAlone()
    {
        var s = Make(new GamePhase.Revealed(null), out _);
        var started = s.SnippetStart;
        _clock.Advance(3);
        Assert.True(s.HandleKey(IslandKey.CtrlShiftR));
        Assert.Equal([new GameAction.Restart()], _sent);
        Assert.Equal(started, s.SnippetStart);
    }

    [Fact]
    public void SkipKeepsTextAndFocusAndStartsTheNewSnippetsRing()
    {
        var s = Make(new GamePhase.Guessing(0), out var state);
        s.TitleText = "Paper";
        s.ArtistText = "Kit";
        s.FocusedField = IslandField.Title;
        var token = s.FocusToken;
        _clock.Advance(6);

        Assert.True(s.HandleKey(IslandKey.CtrlShiftS));
        Assert.Equal([new GameAction.Skip()], _sent);
        s.StateDidChange(state, state with { Phase = new GamePhase.PlayingSnippet(1) });
        Assert.Equal(("Paper", "Kit"), (s.TitleText, s.ArtistText));
        Assert.Equal(token, s.FocusToken);
        Assert.Equal(IslandField.Title, s.FocusedField);
        Assert.Equal(_clock.Now, s.SnippetStart);
    }

    [Fact]
    public void NoSkipShortcutAtTheLastTier()
    {
        var s = Make(new GamePhase.Guessing(2), out _);
        Assert.False(s.HandleKey(IslandKey.CtrlShiftS));
        Assert.Empty(_sent);
    }

    [Fact]
    public void NoRestartInWrong()
    {
        var s = Make(new GamePhase.Wrong(0, new Verdict(true, false)), out _);
        Assert.False(s.HandleKey(IslandKey.CtrlShiftR));
        s.Restart();
        Assert.Empty(_sent);
    }

    [Fact]
    public void UnsupportedLinkShowsAMessage()
    {
        var s = Make(new GamePhase.Idle(), out _);
        s.Load();
        Assert.Equal("Paste a Spotify playlist, album or artist link", s.UrlMessage);
        s.UrlText = "https://example.com/x";
        s.ParseSource = _ => null;
        s.Load();
        Assert.Equal("Unsupported link", s.UrlMessage);
        Assert.Empty(_sent);
        s.ParseSource = _ => new SourceRef(SourceKind.Album, "x");
        s.Load();
        Assert.Null(s.UrlMessage);
        Assert.Equal([new GameAction.Load(new SourceRef(SourceKind.Album, "x"))], _sent);
    }

    [Fact]
    public void AnyPhaseChangeClearsTheUrlMessage()
    {
        var s = Make(new GamePhase.Idle(), out var state);
        s.UrlMessage = "Unsupported link";
        s.StateDidChange(state, state with { Phase = new GamePhase.Loading() });
        Assert.Null(s.UrlMessage);
    }

    [Fact]
    public void SubmitNeedsBothHalves()
    {
        var s = Make(new GamePhase.Guessing(0), out _);
        s.TitleText = "T";
        s.SubmitGuess();
        Assert.Empty(_sent);
        Assert.Equal(IslandField.Artist, s.RequestedFocus);
        s.ArtistText = " A ";
        Assert.True(s.CanSubmitGuess);
        s.SubmitGuess();
        Assert.Equal([new GameAction.Submit(new Guess("T", "A"))], _sent);
    }

    [Fact]
    public void KeysRouteThroughTheRules()
    {
        var s = Make(new GamePhase.Wrong(0, new Verdict(false, false)), out _);
        Assert.True(s.HandleKey(IslandKey.CtrlR));
        Assert.True(s.HandleKey(IslandKey.Escape));
        Assert.Equal([new GameAction.Retry(), new GameAction.GiveUp()], _sent);
        Assert.False(s.HandleKey(IslandKey.Tab)); // no fields in Wrong: the text box gets it
    }

    [Fact]
    public void EscapeOutsideAGuessCollapsesAndClosesSettings()
    {
        var s = Make(new GamePhase.Correct(0), out _);
        s.Behavior.Click();
        s.ShowingSettings = true;
        Assert.True(s.HandleKey(IslandKey.Escape)); // closes settings first
        Assert.False(s.ShowingSettings);
        Assert.True(s.Behavior.IsExpanded);
        Assert.True(s.HandleKey(IslandKey.Escape));
        Assert.False(s.Behavior.IsExpanded);
        Assert.False(s.Behavior.HasKeyboard);
        Assert.Null(s.RequestedFocus);
    }

    [Fact]
    public void AnAutoCollapseClosesSettings()
    {
        var s = Make(new GamePhase.Guessing(0), out _);
        s.Behavior.PointerMoved(true);
        s.ShowingSettings = true;
        s.Behavior.PointerMoved(false);
        _clock.Advance(0.5);
        s.Behavior.Tick();
        Assert.False(s.ShowingSettings);
    }

    [Fact]
    public void VisibleFieldTextFollowsThePhase()
    {
        var s = Make(new GamePhase.Guessing(0), out var state);
        s.UrlText = "https://open.spotify.com/x";
        Assert.False(s.HasTextInVisibleFields);
        s.ArtistText = "a";
        Assert.True(s.HasTextInVisibleFields);
        s.StateDidChange(state, state with { Phase = new GamePhase.Idle() });
        s.ArtistText = "a";
        Assert.True(s.HasTextInVisibleFields); // the URL text now counts
        s.UrlText = " ";
        Assert.False(s.HasTextInVisibleFields);
    }

    [Fact]
    public void SetEndIsTimestampedForTheFlashButNotAtLaunch()
    {
        var s = new IslandSession(_clock);
        s.StateDidChange(null, UiFixtures.State(new GamePhase.SetComplete(1)));
        Assert.Null(s.SetEndedAt);
        var s2 = Make(new GamePhase.Correct(0), out var state);
        s2.StateDidChange(state, state with { Phase = new GamePhase.SetComplete(1) });
        Assert.Equal(_clock.Now, s2.SetEndedAt);
    }

    [Theory]
    [InlineData(false, 40, SetChoice.KeepMisses)]
    [InlineData(true, 40, SetChoice.AllNew)]
    [InlineData(false, 0, SetChoice.Replay)]
    public void SetEndKeysUseTheLiveNewCount(bool complete, int newAvailable, SetChoice enter)
    {
        var s = new IslandSession(_clock) { Send = _sent.Add };
        s.StateDidChange(null, IslandDemoGame.SetEndState(complete, newAvailable));
        Assert.True(s.HandleKey(IslandKey.Enter));
        Assert.True(s.HandleKey(IslandKey.CtrlShiftR));
        Assert.Equal(newAvailable > 0, s.HandleKey(IslandKey.CtrlShiftN));
        GameAction[] expected = newAvailable > 0
            ? [new GameAction.StartSet(enter), new GameAction.StartSet(SetChoice.Replay), new GameAction.StartSet(SetChoice.AllNew)]
            : [new GameAction.StartSet(enter), new GameAction.StartSet(SetChoice.Replay)];
        Assert.Equal(expected, _sent);
    }
}
