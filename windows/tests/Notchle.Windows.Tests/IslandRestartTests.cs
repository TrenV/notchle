using System.Windows.Automation;
using Notchle.Core;
using Notchle.Core.Ui;
using Notchle.Windows.Island;

namespace Notchle.Windows.Tests;

/// The ↺ button on the real WPF tree: where it shows, what it is called, what a click sends.
public class IslandRestartTests
{
    public static TheoryData<string, string> Shown => new()
    {
        { "06-playing-t0", "Replay snippet" },
        { "07-guessing-t0", "Replay snippet" },
        { "10-correct-confetti", "Restart song" },
        { "13-revealed-gave-up", "Restart song" },
    };

    [Theory]
    [MemberData(nameof(Shown))]
    public void ShowsInGuessAndAnswerScreensWithItsTooltip(string scenario, string label)
    {
        string? name = null, tooltip = null, accelerator = null;
        IslandSta.Run(() =>
        {
            var (root, _, _) = IslandSnapshots.Compose(IslandSnapshots.Scenarios.Single(s => s.Name == scenario));
            var button = Assert.Single(IslandSnapshots.Descendants<IslandIconButton>(root));
            (name, tooltip, accelerator) = (AutomationProperties.GetName(button), button.ToolTip as string,
                AutomationProperties.GetAcceleratorKey(button));
        });
        Assert.Equal((label, label, "Ctrl+Shift+R"), (name, tooltip, accelerator));
    }

    [Theory]
    [InlineData("08-wrong-t0")]
    [InlineData("23-wrong-two-artists")]
    [InlineData("02-idle")]
    [InlineData("16-set-failed")]
    [InlineData("18-error")]
    [InlineData("19-settings")]
    public void HiddenEverywhereElse(string scenario)
    {
        var count = -1;
        IslandSta.Run(() =>
        {
            var (root, _, _) = IslandSnapshots.Compose(IslandSnapshots.Scenarios.Single(s => s.Name == scenario));
            count = IslandSnapshots.Descendants<IslandIconButton>(root).Count();
        });
        Assert.Equal(0, count);
    }

    [Fact]
    public void ClickInAGuessPhaseSendsRestartAndKeepsTheTypedText()
    {
        var sent = new List<GameAction>();
        (string, string) typed = ("", "");
        IslandSta.Run(() =>
        {
            var (root, _, session) = IslandSnapshots.Compose(IslandSnapshots.Scenarios.Single(s => s.Name == "07-guessing-t0"));
            session.Send = sent.Add;
            Assert.Single(IslandSnapshots.Descendants<IslandIconButton>(root)).Click();
            typed = (session.TitleText, session.ArtistText);
        });
        Assert.Equal([new GameAction.Restart()], sent);
        Assert.Equal(("Paper Lanterns", "Midnight"), typed);
    }

    [Fact]
    public void ClickInCorrectSendsRestart()
    {
        var sent = new List<GameAction>();
        IslandSta.Run(() =>
        {
            var (root, _, session) = IslandSnapshots.Compose(IslandSnapshots.Scenarios.Single(s => s.Name == "11-correct-preview-hint"));
            session.Send = sent.Add;
            Assert.Single(IslandSnapshots.Descendants<IslandIconButton>(root)).Click();
        });
        Assert.Equal([new GameAction.Restart()], sent);
    }

    [Fact]
    public void TheButtonIsNotFocusableSoTypingFocusStays()
    {
        var focusable = true;
        IslandSta.Run(() =>
        {
            var (root, _, _) = IslandSnapshots.Compose(IslandSnapshots.Scenarios.Single(s => s.Name == "07-guessing-t0"));
            focusable = Assert.Single(IslandSnapshots.Descendants<IslandIconButton>(root)).Focusable;
        });
        Assert.False(focusable);
    }
}
