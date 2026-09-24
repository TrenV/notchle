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
        Assert.Equal(new Send(new GameAction.NextSet()), Key(IslandKey.Enter, new GamePhase.SetComplete(20)));
        Assert.Equal(new Send(new GameAction.ReplaySet()), Key(IslandKey.Enter, new GamePhase.SetFailed(19)));
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
        Assert.Equal("3 artists, any order", IslandRules.ArtistPlaceholder(3));
        Assert.Equal("need all 3", IslandRules.ArtistHint(new Verdict(true, false), 3));
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
