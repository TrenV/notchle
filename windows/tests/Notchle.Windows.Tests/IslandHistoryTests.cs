using System.Windows;
using Notchle.Core;
using Notchle.Core.Ui;
using Notchle.Windows.Island;

namespace Notchle.Windows.Tests;

/// The History tab and the album covers on the real WPF tree.
public class IslandHistoryTests
{
    private static IslandSnapshots.Scenario Scenario(string name) => IslandSnapshots.Scenarios.Single(s => s.Name == name);

    private static string Text(IslandSnapshots.Scenario scenario)
    {
        var text = "";
        IslandSta.Run(() => text = string.Join(" | ", IslandSta.AllText(IslandSnapshots.Compose(scenario).Root)));
        return text;
    }

    [Fact]
    public void ShowsEntriesStatsAndBadges()
    {
        var text = Text(Scenario("50-history-no-covers"));
        Assert.Contains("Harbour Lights — Clara Wynn", text);
        Assert.Contains("Tangerine Motel — The Low Suns, Ada Frost", text);
        Assert.Contains("Late Night Drive · 2d ago", text);
        Assert.Contains("1st try", text);
        Assert.Contains("3rd try", text);
        Assert.Contains("missed", text);
        Assert.Contains("Today", text);
        Assert.Contains("✓ 7/10", text);
        Assert.Contains("best streak", text);
        Assert.Contains(HistoryRules.ClearLabel, text);
        // Idle: no current set, so the demo tracks played last show too.
        Assert.Contains("Paper Lanterns", text);
    }

    [Fact]
    public void HidesTracksOfTheCurrentSetUntilItEnds()
    {
        var playing = Text(Scenario("44-history"));
        Assert.Contains("Harbour Lights", playing); // control: the list is drawn
        Assert.DoesNotContain("Paper Lanterns", playing);
        Assert.DoesNotContain("Glass Harbour", playing);
        Assert.DoesNotContain("Midnight Kites", playing);
        Assert.Contains("2 songs from this set show up when it ends.", playing);
        // The game underneath is not drawn either (no guess fields, no answer).
        Assert.DoesNotContain(IslandScreen.Guess.SubmitLabel, playing);

        var ended = Text(Scenario("46-history-clear-armed")); // SetFailed
        Assert.Contains("Paper Lanterns — The Midnight Kites", ended);
        Assert.Contains("Glass Harbour — Nova Reyes", ended);
    }

    [Fact]
    public void EmptyState() =>
        Assert.Contains(HistoryRules.EmptyText, Text(Scenario("45-history-empty")));

    [Fact]
    public void ClearIsTwoStepAndTheGameIsNotTouched()
    {
        IslandSta.Run(() =>
        {
            var (root, view, session) = IslandSnapshots.Compose(Scenario("50-history-no-covers"));
            var sent = new List<GameAction>();
            var cleared = 0;
            session.Send = sent.Add;
            session.ClearHistory = () => cleared++;
            HistoryView History() => Assert.Single(IslandSnapshots.Descendants<HistoryView>(root));

            History().ClearButton.Click();
            view.Update(view.Frame, IslandSnapshots.SnapshotNow, false);
            Assert.Equal(HistoryRules.ClearConfirmLabel, History().ClearButton.Label);
            Assert.Equal(0, cleared);

            History().ClearButton.Click();
            Assert.Equal(1, cleared);
            Assert.Empty(sent);
        });
    }

    [Fact]
    public void ArmedSnapshotShowsTheQuestion() =>
        Assert.Contains(HistoryRules.ClearConfirmLabel, Text(Scenario("46-history-clear-armed")));

    [Fact]
    public void HeaderSwitchSwapsTabsAndKeepsTheGame()
    {
        IslandSta.Run(() =>
        {
            var (root, view, session) = IslandSnapshots.Compose(Scenario("07-guessing-t0"));
            var sent = new List<GameAction>();
            session.Send = sent.Add;
            Assert.Empty(IslandSnapshots.Descendants<HistoryView>(root));

            view.Tabs.Press(IslandTab.History);
            view.Update(view.Frame, IslandSnapshots.SnapshotNow, false);
            Assert.True(session.ShowingHistory);
            Assert.Single(IslandSnapshots.Descendants<HistoryView>(root));
            Assert.Null(view.FieldFor(IslandField.Title)); // no text box to type a guess into

            view.Tabs.Press(IslandTab.Play);
            view.Update(view.Frame, IslandSnapshots.SnapshotNow, false);
            Assert.False(session.ShowingHistory);
            Assert.NotNull(view.FieldFor(IslandField.Title));
            Assert.Equal("Paper Lanterns", session.TitleText); // typing kept
            Assert.Empty(sent);
        });
    }

    // MARK: Album covers

    private static List<CoverImage> Covers(IslandSnapshots.Scenario scenario)
    {
        var covers = new List<CoverImage>();
        IslandSta.Run(() => covers = IslandSnapshots.Descendants<CoverImage>(IslandSnapshots.Compose(scenario).Root)
            .Where(c => c.HasImage && c.Visibility == Visibility.Visible).ToList());
        return covers;
    }

    [Theory]
    [InlineData("47-correct-cover")]
    [InlineData("48-revealed-cover")]
    public void AnswerScreensShowTheCover(string name) => Assert.Single(Covers(Scenario(name)));

    [Fact]
    public void OfflineAnswerHasNoCover() => Assert.Empty(Covers(Scenario("49-correct-cover-offline")));

    /// Even with a cover URL (stale, from the previous answer) and covers available, the guess
    /// phases never draw one.
    [Theory]
    [InlineData("playing")]
    [InlineData("guessing")]
    [InlineData("wrong")]
    public void GuessPhasesNeverShowACover(string name)
    {
        GamePhase phase = name switch
        {
            "playing" => new GamePhase.PlayingSnippet(0),
            "guessing" => new GamePhase.Guessing(1),
            _ => new GamePhase.Wrong(1, new Verdict(true, false)),
        };
        Assert.Empty(Covers(new IslandSnapshots.Scenario("test", phase) { Covers = true }));
        // Control: the same setup in Correct does draw it.
        Assert.Single(Covers(new IslandSnapshots.Scenario("test", new GamePhase.Correct(0)) { Covers = true }));
    }

    [Fact]
    public void HistoryRowsGetThumbnailsOrTheNote()
    {
        IslandSta.Run(() =>
        {
            var root = IslandSnapshots.Compose(Scenario("44-history")).Root;
            var thumbs = IslandSnapshots.Descendants<CoverImage>(root).ToList();
            Assert.Equal(6, thumbs.Count(c => c.HasImage));   // the visible entries with a cover URL
            Assert.Equal(2, thumbs.Count(c => !c.HasImage));  // no URL: placeholder note
        });
    }
}
