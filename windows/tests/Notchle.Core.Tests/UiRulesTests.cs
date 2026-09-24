using Notchle.Core.Ui;
using static Notchle.Core.Ui.IslandCommand;

namespace Notchle.Core.Tests;

/// Same cases as Tests/NotchleMacTests/UIRulesTests.swift, plus the Windows key mapping.
public class UiRulesTests
{
    private static IslandCommand? Key(IslandKey k, GamePhase p, IslandField? f = null, bool settings = false) =>
        IslandRules.Command(k, p, f, settings);

    [Fact]
    public void EnterMeansThePrimaryButton()
    {
        Assert.Equal(new Load(), Key(IslandKey.Enter, new GamePhase.Idle()));
        Assert.Equal(new Load(), Key(IslandKey.Enter, new GamePhase.Exhausted()));
        Assert.Null(Key(IslandKey.Enter, new GamePhase.Loading()));
        Assert.Equal(new SubmitGuess(), Key(IslandKey.Enter, new GamePhase.PlayingSnippet(0)));
        Assert.Equal(new SubmitGuess(), Key(IslandKey.Enter, new GamePhase.Guessing(2)));
        Assert.Equal(new Send(new GameAction.Retry()), Key(IslandKey.Enter, new GamePhase.Wrong(0, new Verdict(false, false))));
        Assert.Equal(new Send(new GameAction.Next()), Key(IslandKey.Enter, new GamePhase.Correct(0)));
        Assert.Equal(new Send(new GameAction.Next()), Key(IslandKey.Enter, new GamePhase.Revealed(null)));
        Assert.Equal(new Send(new GameAction.Next()), Key(IslandKey.Enter, new GamePhase.Error("e")));
    }

    private static IslandCommand? SetEndKey(IslandKey k, GamePhase p, int availableNew) =>
        IslandRules.Command(k, p, null, availableNew: availableNew);

    private static Send Start(SetChoice c) => new(new GameAction.StartSet(c));

    [Fact]
    public void SetEndEnterIsThePrimaryChoice()
    {
        Assert.Equal(Start(SetChoice.AllNew), SetEndKey(IslandKey.Enter, new GamePhase.SetComplete(20), 20));
        Assert.Equal(Start(SetChoice.KeepMisses), SetEndKey(IslandKey.Enter, new GamePhase.SetFailed(19), 20));
        Assert.Equal(Start(SetChoice.Replay), SetEndKey(IslandKey.Enter, new GamePhase.SetComplete(20), 0));
        Assert.Equal(Start(SetChoice.Replay), SetEndKey(IslandKey.Enter, new GamePhase.SetFailed(19), 0));
    }

    [Fact]
    public void SetEndCtrlShiftRReplaysAndCtrlShiftNDealsNew()
    {
        foreach (GamePhase p in new GamePhase[] { new GamePhase.SetComplete(20), new GamePhase.SetFailed(3) })
        {
            Assert.Equal(Start(SetChoice.Replay), SetEndKey(IslandKey.CtrlShiftR, p, 7));
            Assert.Equal(Start(SetChoice.Replay), SetEndKey(IslandKey.CtrlShiftR, p, 0));
            Assert.Equal(Start(SetChoice.AllNew), SetEndKey(IslandKey.CtrlShiftN, p, 7));
            Assert.Null(SetEndKey(IslandKey.CtrlShiftN, p, 0)); // the button is hidden at 0
        }
        foreach (var p in UiFixtures.AllPhases.Where(p => p is not (GamePhase.SetComplete or GamePhase.SetFailed)))
            Assert.Null(SetEndKey(IslandKey.CtrlShiftN, p, 20));
    }

    [Fact]
    public void CtrlShiftNNeverArmsQuitAndCtrlNStillDoes()
    {
        Assert.Equal(IslandKey.CtrlShiftN, IslandKeys.FromVirtualKey(IslandKeys.VkN, ctrl: true, alt: false, shift: true));
        Assert.Equal(IslandKey.CtrlN, IslandKeys.FromVirtualKey(IslandKeys.VkN, ctrl: true, alt: false, shift: false));
        Assert.Null(IslandKeys.FromVirtualKey(IslandKeys.VkN, ctrl: true, alt: true, shift: true));
        foreach (var p in UiFixtures.AllPhases)
            Assert.IsNotType<Quit>(SetEndKey(IslandKey.CtrlShiftN, p, 20));
        Assert.Equal(new Quit(), SetEndKey(IslandKey.CtrlN, new GamePhase.SetFailed(3), 20));
    }

    [Fact]
    public void SetEndChoicesPerPhase()
    {
        Assert.Equal([SetChoice.AllNew, SetChoice.Replay], IslandRules.SetEndChoices(new GamePhase.SetComplete(20), 20));
        Assert.Equal([SetChoice.KeepMisses, SetChoice.Replay, SetChoice.AllNew], IslandRules.SetEndChoices(new GamePhase.SetFailed(3), 1));
        Assert.Equal([SetChoice.Replay], IslandRules.SetEndChoices(new GamePhase.SetFailed(3), 0));
        Assert.Empty(IslandRules.SetEndChoices(new GamePhase.Guessing(0), 20));
    }

    [Fact]
    public void EscapeGivesUpInGuessPhasesElseCollapses()
    {
        Assert.Equal(new Send(new GameAction.GiveUp()), Key(IslandKey.Escape, new GamePhase.PlayingSnippet(0)));
        Assert.Equal(new Send(new GameAction.GiveUp()), Key(IslandKey.Escape, new GamePhase.Guessing(1)));
        Assert.Equal(new Send(new GameAction.GiveUp()), Key(IslandKey.Escape, new GamePhase.Wrong(1, new Verdict(true, false))));
        foreach (var p in UiFixtures.AllPhases.Where(p => !IslandRules.IsGuessPhase(p)))
            Assert.Equal(new Collapse(), Key(IslandKey.Escape, p));
        Assert.Equal(new CloseSettings(), Key(IslandKey.Escape, new GamePhase.Guessing(0), IslandField.Title, settings: true));
        Assert.Null(Key(IslandKey.Enter, new GamePhase.Guessing(0), IslandField.Title, settings: true));
    }

    [Fact]
    public void CtrlROnlyRetriesFromWrong()
    {
        Assert.Equal(new Send(new GameAction.Retry()), Key(IslandKey.CtrlR, new GamePhase.Wrong(0, new Verdict(false, true))));
        foreach (var p in UiFixtures.AllPhases.Where(p => p is not GamePhase.Wrong))
            Assert.Null(Key(IslandKey.CtrlR, p));
    }

    [Fact]
    public void CtrlShiftRRestartsWhereverTheButtonShows()
    {
        GamePhase[] shown = [new GamePhase.PlayingSnippet(0), new GamePhase.Guessing(1), new GamePhase.Correct(0), new GamePhase.Revealed(null)];
        foreach (var p in shown)
        {
            Assert.True(IslandRules.ShowsRestart(p));
            Assert.Equal(new Restart(), Key(IslandKey.CtrlShiftR, p));
        }
        foreach (var p in UiFixtures.AllPhases.Where(p => !shown.Any(s => s.GetType() == p.GetType())))
        {
            Assert.False(IslandRules.ShowsRestart(p));
            // At a set end Ctrl+Shift+R replays the set instead (SetEndCtrlShiftR... below).
            if (p is GamePhase.SetComplete or GamePhase.SetFailed) continue;
            Assert.Null(Key(IslandKey.CtrlShiftR, p));
        }
        Assert.Null(Key(IslandKey.CtrlShiftR, new GamePhase.Wrong(1, new Verdict(false, false))));
        Assert.Null(Key(IslandKey.CtrlShiftR, new GamePhase.Guessing(0), IslandField.Title, settings: true));
        // Ctrl+R is still Retry, and only that.
        Assert.Null(Key(IslandKey.CtrlR, new GamePhase.Guessing(0)));
    }

    [Fact]
    public void CtrlShiftSSkipsOnlyWhileALongerTierIsLeft()
    {
        var skip = new Send(new GameAction.Skip());
        Assert.Equal(skip, Key(IslandKey.CtrlShiftS, new GamePhase.PlayingSnippet(0)));
        Assert.Equal(skip, Key(IslandKey.CtrlShiftS, new GamePhase.Guessing(1)));
        Assert.Null(Key(IslandKey.CtrlShiftS, new GamePhase.Guessing(2)));        // last tier: Give up only
        Assert.Null(Key(IslandKey.CtrlShiftS, new GamePhase.PlayingSnippet(2)));
        foreach (var p in UiFixtures.AllPhases.Where(p => p is not (GamePhase.PlayingSnippet or GamePhase.Guessing)))
            Assert.Null(Key(IslandKey.CtrlShiftS, p));
        Assert.Null(Key(IslandKey.CtrlShiftS, new GamePhase.Guessing(0), IslandField.Title, settings: true));
        // The configured tiers decide what "last" is.
        var two = new GameConfig([5, 10]);
        Assert.Null(IslandRules.Command(IslandKey.CtrlShiftS, new GamePhase.Guessing(1), null, false, two));
        Assert.Equal(skip, IslandRules.Command(IslandKey.CtrlShiftS, new GamePhase.Guessing(1), null, false, GameConfig.Default));
    }

    [Fact]
    public void SkipLabelNamesTheNextTier()
    {
        var c = GameConfig.Default;
        Assert.Equal("Skip · 10s", IslandRules.SkipLabel(new GamePhase.PlayingSnippet(0), c));
        Assert.Equal("Skip · 15s", IslandRules.SkipLabel(new GamePhase.Guessing(1), c));
        Assert.Null(IslandRules.SkipLabel(new GamePhase.Guessing(2), c));
        Assert.Null(IslandRules.SkipLabel(new GamePhase.Wrong(0, new Verdict(false, false)), c));
        Assert.Equal("Skip · 2.5s", IslandRules.SkipLabel(new GamePhase.Guessing(0), new GameConfig([1, 2.5])));
    }

    [Fact]
    public void RestartTooltipFollowsThePhase()
    {
        Assert.Equal("Replay snippet", IslandRules.RestartLabel(new GamePhase.PlayingSnippet(2)));
        Assert.Equal("Replay snippet", IslandRules.RestartLabel(new GamePhase.Guessing(0)));
        Assert.Equal("Restart song", IslandRules.RestartLabel(new GamePhase.Correct(1)));
        Assert.Equal("Restart song", IslandRules.RestartLabel(new GamePhase.Revealed(new Verdict(false, true))));
        Assert.Equal("Replay snippet", IslandRules.RestartLabel(new GamePhase.Wrong(0, new Verdict(true, false))));
        Assert.Null(IslandRules.RestartLabel(new GamePhase.Idle()));
    }

    [Fact]
    public void TabMovesBetweenTitleAndArtist()
    {
        var p = new GamePhase.Guessing(0);
        Assert.Equal(new Focus(IslandField.Artist), Key(IslandKey.Tab, p, IslandField.Title));
        Assert.Equal(new Focus(IslandField.Title), Key(IslandKey.Tab, p, IslandField.Artist));
        Assert.Equal(new Focus(IslandField.Title), Key(IslandKey.BackTab, new GamePhase.PlayingSnippet(0), IslandField.Artist));
        Assert.Null(Key(IslandKey.Tab, new GamePhase.Idle(), IslandField.Url));
    }

    [Fact]
    public void VirtualKeysMapToIslandKeys()
    {
        Assert.Equal(IslandKey.Enter, IslandKeys.FromVirtualKey(0x0D, false, false, false));
        Assert.Equal(IslandKey.Escape, IslandKeys.FromVirtualKey(0x1B, false, false, false));
        Assert.Equal(IslandKey.Tab, IslandKeys.FromVirtualKey(0x09, false, false, false));
        Assert.Equal(IslandKey.BackTab, IslandKeys.FromVirtualKey(0x09, false, false, true));
        Assert.Equal(IslandKey.CtrlR, IslandKeys.FromVirtualKey(0x52, true, false, false));
        Assert.Equal(IslandKey.CtrlShiftR, IslandKeys.FromVirtualKey(0x52, true, false, true));
        Assert.Null(IslandKeys.FromVirtualKey(0x52, false, false, true));    // typing "R"
        Assert.Null(IslandKeys.FromVirtualKey(0x52, true, true, true));      // Ctrl+Alt+Shift+R
        Assert.Equal(IslandKey.CtrlShiftS, IslandKeys.FromVirtualKey(0x53, true, false, true));
        Assert.Null(IslandKeys.FromVirtualKey(0x53, true, false, false));    // Ctrl+S
        Assert.Null(IslandKeys.FromVirtualKey(0x53, false, false, true));    // typing "S"
        Assert.Null(IslandKeys.FromVirtualKey(0x53, true, true, true));      // AltGr+Shift+S
        Assert.Null(IslandKeys.FromVirtualKey(0x52, false, false, false));   // typing "r"
        Assert.Null(IslandKeys.FromVirtualKey(0x52, true, true, false));     // Ctrl+Alt+R (AltGr)
        Assert.Null(IslandKeys.FromVirtualKey(0x0D, false, false, true));    // Shift+Enter
        Assert.Null(IslandKeys.FromVirtualKey(0x09, true, false, false));    // Ctrl+Tab
    }

    [Fact]
    public void FieldsResetForANewTrackAndKeepForARetry()
    {
        Assert.Equal(FieldTransitionKind.ClearAndFocusTitle,
            IslandRules.FieldTransitionFor(new GamePhase.Correct(0), new GamePhase.PlayingSnippet(0)).Kind);
        Assert.Equal(FieldTransitionKind.ClearAndFocusTitle,
            IslandRules.FieldTransitionFor(new GamePhase.Loading(), new GamePhase.PlayingSnippet(0)).Kind);
        Assert.Equal(FieldTransitionKind.ClearAndFocusTitle,
            IslandRules.FieldTransitionFor(new GamePhase.Error("e"), new GamePhase.Guessing(0)).Kind);
        Assert.Equal(FieldTransition.None,
            IslandRules.FieldTransitionFor(new GamePhase.PlayingSnippet(0), new GamePhase.Guessing(0)));
        Assert.Equal(new FieldTransition(FieldTransitionKind.KeepAndFocus, IslandField.Artist),
            IslandRules.FieldTransitionFor(new GamePhase.Wrong(0, new Verdict(true, false)), new GamePhase.PlayingSnippet(1)));
        Assert.Equal(new FieldTransition(FieldTransitionKind.KeepAndFocus, IslandField.Title),
            IslandRules.FieldTransitionFor(new GamePhase.Wrong(1, new Verdict(false, true)), new GamePhase.PlayingSnippet(2)));
        Assert.Equal(FieldTransitionKind.FocusUrl, IslandRules.FieldTransitionFor(null, new GamePhase.Idle()).Kind);
        Assert.Equal(FieldTransition.None, IslandRules.FieldTransitionFor(new GamePhase.Guessing(0), new GamePhase.Guessing(0)));
        // A skip is the same track at the next tier: keep text and focus.
        Assert.Equal(FieldTransition.None,
            IslandRules.FieldTransitionFor(new GamePhase.PlayingSnippet(0), new GamePhase.PlayingSnippet(1)));
        Assert.Equal(FieldTransition.None,
            IslandRules.FieldTransitionFor(new GamePhase.Guessing(1), new GamePhase.PlayingSnippet(2)));
        // A replay (Restart from Guessing) is the same track at the same tier: keep everything.
        Assert.Equal(FieldTransition.None,
            IslandRules.FieldTransitionFor(new GamePhase.Guessing(0), new GamePhase.PlayingSnippet(0)));
        Assert.Equal(FieldTransition.None,
            IslandRules.FieldTransitionFor(new GamePhase.Guessing(2), new GamePhase.PlayingSnippet(2)));
    }

    [Fact]
    public void Labels()
    {
        var c = GameConfig.Default;
        Assert.Equal(10, IslandRules.RetrySeconds(0, c));
        Assert.Equal(15, IslandRules.RetrySeconds(1, c));
        Assert.Equal(15, IslandRules.RetrySeconds(5, c));
        Assert.Equal("5s", IslandRules.SecondsLabel(5));
        Assert.Equal("2.5s", IslandRules.SecondsLabel(2.5));
        Assert.Equal("Artist(s)", IslandRules.ArtistPlaceholder(1));
        // The number of artists is never shown: knowing them is part of the game.
        Assert.Equal("Artist(s)", IslandRules.ArtistPlaceholder(3));
        Assert.Null(IslandRules.ArtistHint(new Verdict(true, false), 3));
        Assert.Null(IslandRules.ArtistHint(new Verdict(true, false), 1));
        Assert.Null(IslandRules.ArtistHint(new Verdict(false, true), 3));
    }

    [Fact]
    public void ProgressText()
    {
        var s = UiFixtures.State(new GamePhase.Guessing(0), UiFixtures.Secret, UiFixtures.Other) with { Index = 1 };
        Assert.Equal("2/2", IslandRules.ProgressText(s));
        Assert.Equal("1/2", IslandRules.ProgressText(s with { Phase = new GamePhase.SetFailed(1) }));
        Assert.Equal("–", IslandRules.ProgressText(s with { Phase = new GamePhase.Loading() }));
        Assert.Equal("–", IslandRules.ProgressText(s with { CurrentSet = Array.Empty<Track>() }));
    }

    [Fact]
    public void HidesOnlyForFullscreenGamesAndPresentations()
    {
        // QUNS_NOT_PRESENT 1, BUSY 2, RUNNING_D3D_FULL_SCREEN 3, PRESENTATION_MODE 4,
        // ACCEPTS_NOTIFICATIONS 5, QUIET_TIME 6, APP 7.
        Assert.Equal([2, 3, 4], Enumerable.Range(0, 9).Where(IslandRules.HidesForNotificationState));
    }

    [Fact]
    public void PlayerModeChangeOnlyWhenDifferent()
    {
        var s = new AppSettings();
        Assert.Null(IslandRules.WithPlayerMode(s, PlayerMode.Preview));
        Assert.Equal(PlayerMode.SpotifyConnect, IslandRules.WithPlayerMode(s, PlayerMode.SpotifyConnect)!.PlayerMode);
    }
}
