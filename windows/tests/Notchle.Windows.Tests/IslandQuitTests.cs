using System.Windows;
using System.Windows.Automation;
using Notchle.Core;
using Notchle.Windows.Island;

namespace Notchle.Windows.Tests;

/// The quit-playlist header button on the real WPF tree.
public class IslandQuitTests
{
    private static (bool Visible, bool Armed, string? Name, List<GameAction> Sent) Quit(string scenario, int clicks = 0)
    {
        var result = (false, false, (string?)null, new List<GameAction>());
        IslandSta.Run(() =>
        {
            var (root, view, session) = IslandSnapshots.Compose(IslandSnapshots.Scenarios.Single(s => s.Name == scenario));
            session.Send = result.Item4.Add;
            var button = Assert.Single(IslandSnapshots.Descendants<QuitButton>(root));
            for (var i = 0; i < clicks; i++)
            {
                button.Click();
                view.Update(view.Frame, IslandSnapshots.SnapshotNow, false); // what the window does on Changed
            }
            result = (button.Visibility == Visibility.Visible, button.Armed, AutomationProperties.GetName(button), result.Item4);
        });
        return result;
    }

    [Theory]
    [InlineData("04-loading")]
    [InlineData("07-guessing-t0")]
    [InlineData("08-wrong-t0")]
    [InlineData("10-correct-confetti")]
    [InlineData("15-set-complete")]
    [InlineData("16-set-failed")]
    [InlineData("18-error")]
    public void VisibleWheneverAListingIsLoaded(string scenario)
    {
        var (visible, armed, name, _) = Quit(scenario);
        Assert.Equal((true, false, "Quit playlist"), (visible, armed, name));
    }

    [Theory]
    [InlineData("02-idle")]
    [InlineData("17-exhausted")]
    public void HiddenOnTheLinkScreens(string scenario) => Assert.False(Quit(scenario).Visible);

    [Fact]
    public void ArmedSnapshotShowsTheQuestion()
    {
        var (visible, armed, name, sent) = Quit("35-quit-armed");
        Assert.Equal((true, true, "Quit playlist?"), (visible, armed, name));
        Assert.Empty(sent);
    }

    [Fact]
    public void FirstClickArmsSecondSendsReset()
    {
        var (_, armed, name, sent) = Quit("07-guessing-t0", clicks: 1);
        Assert.Equal((true, "Quit playlist?"), (armed, name));
        Assert.Empty(sent);
        Assert.Equal([new GameAction.Reset()], Quit("07-guessing-t0", clicks: 2).Sent);
    }
}
