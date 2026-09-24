using System.Windows;
using Notchle.Core;
using Notchle.Windows.Island;

namespace Notchle.Windows.Tests;

/// The Skip button on the real WPF tree: "Skip · <next tier>" until the last tier, then gone.
public class IslandSkipTests
{
    private static (string? Label, bool Visible, List<GameAction> Sent) SkipButton(string scenario, bool click = false)
    {
        string? label = null;
        var visible = false;
        var sent = new List<GameAction>();
        IslandSta.Run(() =>
        {
            var (root, _, session) = IslandSnapshots.Compose(IslandSnapshots.Scenarios.Single(s => s.Name == scenario));
            session.Send = sent.Add;
            var skip = IslandSnapshots.Descendants<IslandButton>(root).SingleOrDefault(b => b.Hint == "Ctrl+Shift+S");
            if (skip is null) return;
            (label, visible) = (skip.Label, skip.Visibility == Visibility.Visible);
            if (click && visible) skip.Click();
        });
        return (label, visible, sent);
    }

    [Theory]
    [InlineData("33-guessing-t0-skip", "Skip · 10s")]
    [InlineData("06-playing-t0", "Skip · 10s")]
    [InlineData("09-playing-t1", "Skip · 15s")]
    public void ShowsTheNextTiersLength(string scenario, string label)
    {
        var (actual, visible, _) = SkipButton(scenario);
        Assert.Equal((label, true), (actual, visible));
    }

    [Fact]
    public void HiddenAtTheLastTier() => Assert.False(SkipButton("34-guessing-t2-no-skip").Visible);

    [Theory]
    [InlineData("08-wrong-t0")]
    [InlineData("10-correct-confetti")]
    public void NotInWrongOrAnswerScreens(string scenario) => Assert.Null(SkipButton(scenario).Label);

    [Fact]
    public void ClickSendsSkip() =>
        Assert.Equal([new GameAction.Skip()], SkipButton("33-guessing-t0-skip", click: true).Sent);
}
