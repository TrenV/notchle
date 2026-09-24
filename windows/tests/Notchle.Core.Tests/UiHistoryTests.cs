using Notchle.Core;
using Notchle.Core.Ui;

namespace Notchle.Core.Tests;

/// The History tab: spoiler filter, the screen data, the tab keys and the two-step clear.
public class UiHistoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    private static HistoryEntry Entry(string trackId, bool correct, int? tier, DateTimeOffset date, int wrong = 0, int skips = 0) =>
        new(Guid.NewGuid(), date, trackId, $"Title {trackId}", ["Ann", "Bo"], "Road trip",
            new SourceRef(SourceKind.Playlist, "p"), correct, correct ? tier : null, wrong, skips);

    // UiFixtures.Secret is "t1" (the current set), Other is "t2".
    private static readonly HistoryEntry OldSecret = Entry("t1", true, 0, Now.AddDays(-3));
    private static readonly HistoryEntry OtherTrack = Entry("t2", false, null, Now.AddHours(-2), wrong: 1, skips: 2);
    private static readonly HistoryEntry[] History = [OldSecret, OtherTrack];

    public static TheoryData<GamePhase> PlayingPhases => new()
    {
        new GamePhase.Loading(), new GamePhase.PlayingSnippet(0), new GamePhase.Guessing(1),
        new GamePhase.Wrong(0, new Verdict(true, false)), new GamePhase.Correct(0), new GamePhase.Revealed(null),
        new GamePhase.Error("x"),
    };

    [Theory]
    [MemberData(nameof(PlayingPhases))]
    public void TracksOfTheCurrentSetStayHiddenWhileItIsPlayed(GamePhase phase)
    {
        var state = UiFixtures.State(phase, UiFixtures.Secret);
        Assert.Equal([OtherTrack], HistoryRules.Visible(History, state));
        var screen = HistoryRules.Screen(History, state, Now, false, TimeZoneInfo.Utc);
        Assert.Equal(1, screen.HiddenCount);
        Assert.DoesNotContain(UiSecrecyTests.Strings(screen), s => s.Contains("Title t1"));
        Assert.Equal("1 song from this set shows up when it ends.", screen.HiddenNote);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EverythingShowsOnceTheSetEnds(bool complete)
    {
        var state = UiFixtures.State(complete ? new GamePhase.SetComplete(1) : new GamePhase.SetFailed(0), UiFixtures.Secret);
        Assert.Equal(History, HistoryRules.Visible(History, state));
        Assert.Equal(0, HistoryRules.Screen(History, state, Now, false, TimeZoneInfo.Utc).HiddenCount);
    }

    [Fact]
    public void EverythingShowsAfterQuitAndHidesAgainOnReplay()
    {
        var quit = new GameState { Config = GameConfig.Default }; // Reset: Idle, empty set
        Assert.Equal(History, HistoryRules.Visible(History, quit));
        // SetFailed → ReplaySet: the same tracks play again, so they hide again.
        Assert.Equal([OtherTrack], HistoryRules.Visible(History, UiFixtures.State(new GamePhase.PlayingSnippet(0), UiFixtures.Secret)));
    }

    [Fact]
    public void ScreenGroupsByDayNewestFirstWithStats()
    {
        HistoryEntry[] h =
        [
            Entry("a", true, 0, Now.AddDays(-9)),
            Entry("b", true, 2, Now.AddDays(-1).AddHours(-1), wrong: 2),
            Entry("c", false, null, Now.AddMinutes(-30), skips: 1),
            Entry("d", true, 1, Now.AddSeconds(-20)),
        ];
        var screen = HistoryRules.Screen(h, new GameState { Config = GameConfig.Default }, Now, false, TimeZoneInfo.Utc);
        Assert.Equal(["Today", "Yesterday", "Tue 15 Sep"], screen.Days.Select(d => d.Heading));
        Assert.Equal(["Title d", "Title c"], screen.Days[0].Rows.Select(r => r.Title));
        var d = screen.Days[0].Rows[0];
        Assert.Equal(("Ann, Bo", "Road trip", "just now", "2nd try", true), (d.Artists, d.Listing, d.When, d.Badge, d.Correct));
        var c = screen.Days[0].Rows[1];
        Assert.Equal(("30m ago", "missed", false, 1, 0), (c.When, c.Badge, c.Correct, c.Skips, c.WrongGuesses));
        Assert.Equal(("1d ago", "3rd try", 2), (screen.Days[1].Rows[0].When, screen.Days[1].Rows[0].Badge, screen.Days[1].Rows[0].WrongGuesses));
        Assert.Equal("15 Sep", screen.Days[2].Rows[0].When);
        Assert.Equal(new HistoryStatsRow("✓ 3/4", "75%", "avg 2.0 tries", "best streak 2"), screen.Stats);
        Assert.Null(screen.EmptyText);
        Assert.True(screen.CanClear);
    }

    [Fact]
    public void EmptyState()
    {
        var screen = HistoryRules.Screen([], new GameState { Config = GameConfig.Default }, Now, false, TimeZoneInfo.Utc);
        Assert.Equal("No songs yet: play a set and your results show up here.", screen.EmptyText);
        Assert.Null(screen.Stats);
        Assert.Empty(screen.Days);
        Assert.False(screen.CanClear);
    }

    [Fact]
    public void OnlyHiddenEntriesStillOfferClearButShowTheEmptyText()
    {
        var screen = HistoryRules.Screen([OldSecret], UiFixtures.State(new GamePhase.Guessing(0)), Now, false, TimeZoneInfo.Utc);
        Assert.NotNull(screen.EmptyText);
        Assert.True(screen.CanClear);
        Assert.Equal(1, screen.HiddenCount);
    }

    [Fact]
    public void DayGroupsFollowTheTimeZone()
    {
        var late = Entry("x", true, 0, new DateTimeOffset(2026, 9, 23, 23, 30, 0, TimeSpan.Zero));
        var plus2 = TimeZoneInfo.CreateCustomTimeZone("+2", TimeSpan.FromHours(2), "+2", "+2");
        var empty = new GameState { Config = GameConfig.Default };
        Assert.Equal("Yesterday", HistoryRules.Screen([late], empty, Now, false, TimeZoneInfo.Utc).Days[0].Heading);
        Assert.Equal("Today", HistoryRules.Screen([late], empty, Now, false, plus2).Days[0].Heading);
    }

    [Fact]
    public void ListIsCappedButStatsCoverEverything()
    {
        var many = Enumerable.Range(0, HistoryRules.MaxRows + 50).Select(i => Entry($"x{i}", true, 0, Now.AddMinutes(-i))).ToArray();
        var screen = HistoryRules.Screen(many, new GameState { Config = GameConfig.Default }, Now, false, TimeZoneInfo.Utc);
        Assert.Equal(HistoryRules.MaxRows, screen.Days.Sum(d => d.Rows.Count));
        Assert.Equal($"✓ {many.Length}/{many.Length}", screen.Stats!.Correct);
    }

    [Theory]
    [InlineData(1, "1st")]
    [InlineData(2, "2nd")]
    [InlineData(3, "3rd")]
    [InlineData(4, "4th")]
    [InlineData(11, "11th")]
    [InlineData(12, "12th")]
    [InlineData(21, "21st")]
    public void Ordinals(int n, string expected) => Assert.Equal(expected, HistoryRules.Ordinal(n));

    [Theory]
    [InlineData(59, "just now")]
    [InlineData(60, "1m ago")]
    [InlineData(3599, "59m ago")]
    [InlineData(3600, "1h ago")]
    [InlineData(86399, "23h ago")]
    [InlineData(86400, "1d ago")]
    [InlineData(6 * 86400, "6d ago")]
    [InlineData(7 * 86400, "17 Sep")]
    public void RelativeTimes(int secondsAgo, string expected) =>
        Assert.Equal(expected, HistoryRules.RelativeTime(Now.AddSeconds(-secondsAgo), Now, TimeZoneInfo.Utc));

    [Fact]
    public void DayHeadingShowsTheYearOnlyWhenItDiffers()
    {
        var today = new DateOnly(2026, 9, 24);
        Assert.Equal("Wed 24 Dec 2025", HistoryRules.DayHeading(new DateOnly(2025, 12, 24), today));
        Assert.Equal("Mon 21 Sep", HistoryRules.DayHeading(new DateOnly(2026, 9, 21), today));
    }

    // MARK: Keys and the session

    public static TheoryData<GamePhase> AllPhases => new(UiFixtures.AllPhases);

    [Theory]
    [InlineData(IslandKeys.Vk1, IslandKey.Ctrl1)]
    [InlineData(IslandKeys.VkNumpad1, IslandKey.Ctrl1)]
    [InlineData(IslandKeys.Vk2, IslandKey.Ctrl2)]
    [InlineData(IslandKeys.VkNumpad2, IslandKey.Ctrl2)]
    public void CtrlDigitsAreTheTabKeys(int vk, IslandKey key)
    {
        Assert.Equal(key, IslandKeys.FromVirtualKey(vk, ctrl: true, alt: false, shift: false));
        Assert.Null(IslandKeys.FromVirtualKey(vk, ctrl: false, alt: false, shift: false)); // typing digits
        Assert.Null(IslandKeys.FromVirtualKey(vk, ctrl: true, alt: true, shift: false));
        Assert.Null(IslandKeys.FromVirtualKey(vk, ctrl: true, alt: false, shift: true));
    }

    [Theory]
    [MemberData(nameof(AllPhases))]
    public void TabKeysWorkInEveryPhaseEvenOverSettings(GamePhase phase)
    {
        Assert.Equal(new IslandCommand.ShowTab(IslandTab.Play), IslandRules.Command(IslandKey.Ctrl1, phase, null));
        Assert.Equal(new IslandCommand.ShowTab(IslandTab.History), IslandRules.Command(IslandKey.Ctrl2, phase, null, settingsOpen: true));
    }

    [Theory]
    [MemberData(nameof(AllPhases))]
    public void InHistoryOnlyEscAndTheTabAndQuitKeysAct(GamePhase phase)
    {
        Assert.Equal(new IslandCommand.ShowTab(IslandTab.Play), IslandRules.Command(IslandKey.Escape, phase, null, historyShown: true));
        foreach (var key in new[] { IslandKey.Enter, IslandKey.Tab, IslandKey.BackTab, IslandKey.CtrlR, IslandKey.CtrlShiftR, IslandKey.CtrlShiftS })
            Assert.Null(IslandRules.Command(key, phase, IslandField.Title, historyShown: true));
        Assert.Equal(IslandRules.ShowsQuit(phase) ? new IslandCommand.Quit() : null,
            IslandRules.Command(IslandKey.CtrlN, phase, null, historyShown: true));
    }

    private static (IslandSession Session, List<GameAction> Sent, UiManualClock Clock, List<int> Cleared) Session(GamePhase phase)
    {
        var clock = new UiManualClock();
        var sent = new List<GameAction>();
        var cleared = new List<int>();
        var session = new IslandSession(clock) { Send = sent.Add };
        session.ClearHistory = () => cleared.Add(1);
        session.StateDidChange(null, UiFixtures.State(phase));
        session.Behavior.PointerMoved(true);
        return (session, sent, clock, cleared);
    }

    [Fact]
    public void Ctrl2ShowsHistoryAndTheGameKeepsRunning()
    {
        var (s, sent, _, _) = Session(new GamePhase.Guessing(0));
        s.TitleText = "Paper";
        Assert.True(s.HandleKey(IslandKey.Ctrl2));
        Assert.True(s.ShowingHistory);
        Assert.IsType<IslandScreen.History>(s.Screen(new AppSettings(), "p", true, History));
        // Enter does not submit, Esc does not give up: it goes back to Play.
        Assert.False(s.HandleKey(IslandKey.Enter));
        Assert.True(s.HandleKey(IslandKey.Escape));
        Assert.Empty(sent);
        Assert.Equal(IslandTab.Play, s.Tab);
        Assert.Equal("Paper", s.TitleText); // typing survives the detour
        Assert.Equal(IslandField.Title, s.RequestedFocus);
        Assert.IsType<IslandScreen.Guess>(s.Screen(new AppSettings(), "p", true, History));
        // A new track while History is shown still resets the fields.
        s.HandleKey(IslandKey.Ctrl2);
        s.StateDidChange(s.State, s.State with { Phase = new GamePhase.Revealed(null) });
        Assert.True(s.ShowingHistory);
    }

    [Fact]
    public void HistoryClosesSettingsAndSettingsCoverHistory()
    {
        var (s, _, _, _) = Session(new GamePhase.Idle());
        s.ShowingSettings = true;
        s.ShowTab(IslandTab.History);
        Assert.False(s.ShowingSettings);
        Assert.True(s.ShowingHistory);
        s.ShowingSettings = true;
        Assert.False(s.ShowingHistory);
        Assert.IsType<IslandScreen.Settings>(s.Screen(new AppSettings(), "p", true));
        Assert.True(s.HandleKey(IslandKey.Ctrl1));
        Assert.Equal((IslandTab.Play, false, IslandField.Url), (s.Tab, s.ShowingSettings, s.RequestedFocus));
    }

    [Fact]
    public void CollapsingReturnsToPlay()
    {
        var (s, _, clock, _) = Session(new GamePhase.Guessing(0));
        s.ShowTab(IslandTab.History);
        s.PressClearHistory();
        s.Perform(new IslandCommand.Collapse());
        clock.Advance(5);
        s.Behavior.Tick();
        Assert.Equal(IslandTab.Play, s.Tab);
        Assert.False(s.ClearHistoryArmed);
    }

    [Fact]
    public void ClearHistoryIsTwoStep()
    {
        var (s, sent, clock, cleared) = Session(new GamePhase.Guessing(0));
        s.ShowTab(IslandTab.History);
        Assert.False(s.PressClearHistory());
        Assert.True(s.ClearHistoryArmed);
        Assert.True(((IslandScreen.History)s.Screen(new AppSettings(), "p", true, History)).ClearArmed);
        Assert.Empty(cleared);
        Assert.True(s.PressClearHistory());
        Assert.Single(cleared);
        Assert.False(s.ClearHistoryArmed);
        Assert.Empty(sent); // the game is not touched

        // Unconfirmed, it reverts by itself; leaving the tab disarms it.
        s.PressClearHistory();
        clock.Advance(QuitConfirmation.Window.TotalSeconds + 0.1);
        Assert.False(s.ClearHistoryArmed);
        s.PressClearHistory();
        s.ShowTab(IslandTab.Play);
        s.ShowTab(IslandTab.History);
        Assert.False(s.PressClearHistory());
        Assert.Single(cleared);
    }

    [Fact]
    public void ClearDoesNothingOutsideHistory()
    {
        var (s, _, _, cleared) = Session(new GamePhase.Idle());
        s.PressClearHistory();
        s.PressClearHistory();
        Assert.Empty(cleared);
    }

    [Fact]
    public void HistoryScreenIsReusedUntilSomethingChanges()
    {
        var (s, _, clock, _) = Session(new GamePhase.Idle());
        s.ShowTab(IslandTab.History);
        var a = s.Screen(new AppSettings(), "p", true, History);
        Assert.Same(a, s.Screen(new AppSettings(), "p", true, History));
        Assert.NotSame(a, s.Screen(new AppSettings(), "p", true, [.. History]));
        clock.Advance(61);
        Assert.NotSame(a, s.Screen(new AppSettings(), "p", true, History));
    }
}
