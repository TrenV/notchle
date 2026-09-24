using Notchle.Core.Ui;

namespace Notchle.Core.Tests;

/// Two-step quit-playlist (header button / Ctrl+N): arm, confirm within 3 s, else revert.
public class UiQuitTests
{
    private readonly UiManualClock _clock = new();
    private readonly List<GameAction> _sent = [];

    private IslandSession Make(GamePhase phase, out GameState state)
    {
        state = UiFixtures.State(phase);
        var s = new IslandSession(_clock) { Send = _sent.Add };
        s.StateDidChange(null, state);
        s.Behavior.Click(); // the island has the keyboard
        return s;
    }

    [Fact]
    public void ConfirmationArmsThenConfirmsInsideTheWindow()
    {
        var q = new QuitConfirmation(_clock);
        Assert.False(q.IsArmed);
        Assert.False(q.Press());
        Assert.True(q.IsArmed);
        _clock.Advance(2.9);
        Assert.True(q.IsArmed);
        Assert.True(q.Press());
        Assert.False(q.IsArmed); // consumed
    }

    [Fact]
    public void ConfirmationRevertsAfterThreeSeconds()
    {
        var q = new QuitConfirmation(_clock);
        q.Press();
        _clock.Advance(3);
        Assert.False(q.IsArmed);
        Assert.False(q.Press()); // a late second press only arms again
        Assert.True(q.IsArmed);
    }

    [Fact]
    public void ShownWheneverAListingIsLoadedHiddenInIdleAndExhausted()
    {
        foreach (var p in UiFixtures.AllPhases)
            Assert.Equal(p is not (GamePhase.Idle or GamePhase.Exhausted), IslandRules.ShowsQuit(p));
        Assert.Null(Make(new GamePhase.Idle(), out _).QuitLabel);
        Assert.Null(Make(new GamePhase.Exhausted(), out _).QuitLabel);
        Assert.Equal("Quit playlist", Make(new GamePhase.Loading(), out _).QuitLabel);
    }

    [Fact]
    public void ClickTwiceSendsResetAndEmptiesTheLinkField()
    {
        var s = Make(new GamePhase.Guessing(1), out var state);
        s.UrlText = "https://open.spotify.com/playlist/old";
        s.PressQuit();
        Assert.True(s.QuitArmed);
        Assert.Equal("Quit playlist?", s.QuitLabel);
        Assert.Empty(_sent);

        _clock.Advance(1);
        s.PressQuit();
        Assert.Equal([new GameAction.Reset()], _sent);
        Assert.False(s.QuitArmed);
        Assert.Equal("", s.UrlText);

        // The engine's Idle state arrives: the link field is asked for focus.
        var token = s.FocusToken;
        s.StateDidChange(state, state with { Phase = new GamePhase.Idle(), Listing = null, CurrentSet = [] });
        Assert.Equal(IslandField.Url, s.RequestedFocus);
        Assert.NotEqual(token, s.FocusToken);
        Assert.Null(s.QuitLabel);
    }

    [Fact]
    public void TimeoutRevertsWithoutSending()
    {
        var s = Make(new GamePhase.Correct(0), out _);
        s.PressQuit();
        _clock.Advance(3.1);
        Assert.False(s.QuitArmed);
        Assert.Equal("Quit playlist", s.QuitLabel);
        s.PressQuit(); // arms again, doesn't quit
        Assert.Empty(_sent);
        Assert.True(s.QuitArmed);
    }

    [Fact]
    public void CtrlNArmsAndASecondCtrlNOrAClickConfirms()
    {
        var s = Make(new GamePhase.PlayingSnippet(0), out _);
        Assert.True(s.HandleKey(IslandKey.CtrlN));
        Assert.True(s.QuitArmed);
        Assert.True(s.HandleKey(IslandKey.CtrlN));
        Assert.Equal([new GameAction.Reset()], _sent);

        var t = Make(new GamePhase.Wrong(0, new Verdict(false, false)), out _);
        Assert.True(t.HandleKey(IslandKey.CtrlN));
        t.PressQuit(); // the capsule clicked
        Assert.Equal([new GameAction.Reset(), new GameAction.Reset()], _sent);
    }

    [Fact]
    public void EscDisarmsInsteadOfGivingUpOrCollapsing()
    {
        var s = Make(new GamePhase.Guessing(0), out _);
        s.HandleKey(IslandKey.CtrlN);
        Assert.True(s.HandleKey(IslandKey.Escape));
        Assert.False(s.QuitArmed);
        Assert.Empty(_sent);                   // no GiveUp
        Assert.True(s.Behavior.IsExpanded);    // no collapse either
        Assert.True(s.HandleKey(IslandKey.Escape));
        Assert.Equal([new GameAction.GiveUp()], _sent); // disarmed: Esc is Give up again
    }

    [Fact]
    public void CtrlNDoesNothingWithoutAListing()
    {
        var s = Make(new GamePhase.Idle(), out _);
        Assert.False(s.HandleKey(IslandKey.CtrlN));
        s.PressQuit();
        s.PressQuit();
        Assert.Empty(_sent);
        Assert.False(s.QuitArmed);
    }

    [Fact]
    public void CollapsingDisarms()
    {
        var s = Make(new GamePhase.Guessing(0), out _);
        s.PressQuit();
        s.Behavior.Collapse();
        Assert.False(s.QuitArmed);
        s.Behavior.Click();
        s.PressQuit();
        Assert.Empty(_sent);
    }

    [Fact]
    public void KeyMapping()
    {
        Assert.Equal(IslandKey.CtrlN, IslandKeys.FromVirtualKey(0x4E, true, false, false));
        Assert.Null(IslandKeys.FromVirtualKey(0x4E, true, true, false));   // Ctrl+Alt+N: the global hotkey
        Assert.Null(IslandKeys.FromVirtualKey(0x4E, false, false, false)); // typing "n"
        Assert.Null(IslandKeys.FromVirtualKey(0x4E, true, false, true));   // Ctrl+Shift+N
        Assert.Equal(new IslandCommand.Quit(), IslandRules.Command(IslandKey.CtrlN, new GamePhase.SetFailed(3), null));
        Assert.Equal(new IslandCommand.Quit(), IslandRules.Command(IslandKey.CtrlN, new GamePhase.Error("x"), null, settingsOpen: true));
        Assert.Null(IslandRules.Command(IslandKey.CtrlN, new GamePhase.Exhausted(), null));
        Assert.Equal(new IslandCommand.DisarmQuit(), IslandRules.Command(IslandKey.Escape, new GamePhase.Guessing(0), null, quitArmed: true));
        Assert.Equal(new IslandCommand.Collapse(), IslandRules.Command(IslandKey.Escape, new GamePhase.Correct(0), null));
    }

    /// The quit copy never names the track (the secrecy tests cover the screens; this covers the header).
    [Fact]
    public void QuitCopyIsNotASecret()
    {
        foreach (var label in new[] { IslandHeader.QuitLabel, IslandHeader.QuitConfirmLabel })
            foreach (var name in new[] { "Zanzibar", "Quill", "Mabel" })
                Assert.DoesNotContain(name, label);
    }
}
