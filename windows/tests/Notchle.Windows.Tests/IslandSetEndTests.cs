using System.Windows;
using Notchle.Core;
using Notchle.Windows.Island;

namespace Notchle.Windows.Tests;

/// The set-end choices on the real WPF tree: labels, which ones show, and that each click goes
/// through the session (IslandContext.Send → Session.Send) as StartSet(choice).
public class IslandSetEndTests
{
    private sealed record Button(string Label, string Hint, bool Visible);

    private static (List<Button> Buttons, List<GameAction> Sent) SetEnd(string scenario, string? click = null)
    {
        var buttons = new List<Button>();
        var sent = new List<GameAction>();
        IslandSta.Run(() =>
        {
            var (root, _, session) = IslandSnapshots.Compose(IslandSnapshots.Scenarios.Single(s => s.Name == scenario));
            session.Send = sent.Add;
            var view = IslandSnapshots.Descendants<SetEndView>(root).Single();
            var found = IslandSnapshots.Descendants<IslandButton>(view).ToList();
            buttons.AddRange(found.Select(b => new Button(b.Label, b.Hint, b.Visibility == Visibility.Visible)));
            if (click is not null) found.Single(b => b.Label == click).Click();
        });
        return (buttons, sent);
    }

    private static string[] Labels(string scenario) =>
        SetEnd(scenario).Buttons.Where(b => b.Visible).Select(b => b.Label).Order().ToArray();

    [Fact]
    public void FailedSetOffersKeepMissesReplayAndNewSongs() =>
        Assert.Equal(new[] { "20 new songs", "Keep misses + new", "Replay these 20" }, Labels("16-set-failed"));

    [Fact]
    public void FailedSetShowsTheLiveNewCount() =>
        Assert.Contains("7 new songs", Labels("37-set-failed-7-new"));

    [Fact]
    public void CompleteSetOffersNewSongsAndReplay() =>
        Assert.Equal(new[] { "20 new songs", "Replay these 20" }, Labels("15-set-complete"));

    [Theory]
    [InlineData("38-set-failed-no-new")]
    [InlineData("39-set-complete-no-new")]
    public void NoNewSongsLeavesOnlyReplay(string scenario) =>
        Assert.Equal(new[] { "Replay these 20" }, Labels(scenario));

    [Theory]
    [InlineData("16-set-failed", "Keep misses + new")]
    [InlineData("15-set-complete", "20 new songs")]
    [InlineData("38-set-failed-no-new", "Replay these 20")]
    public void ThePrimaryCarriesEnter(string scenario, string primary) =>
        Assert.Equal(primary, SetEnd(scenario).Buttons.Single(b => b.Hint == "Enter").Label);

    [Theory]
    [InlineData("16-set-failed", "Keep misses + new", SetChoice.KeepMisses)]
    [InlineData("16-set-failed", "Replay these 20", SetChoice.Replay)]
    [InlineData("16-set-failed", "20 new songs", SetChoice.AllNew)]
    [InlineData("15-set-complete", "20 new songs", SetChoice.AllNew)]
    [InlineData("15-set-complete", "Replay these 20", SetChoice.Replay)]
    [InlineData("38-set-failed-no-new", "Replay these 20", SetChoice.Replay)]
    public void ClickSendsTheChoiceThroughTheSession(string scenario, string label, SetChoice choice) =>
        Assert.Equal([new GameAction.StartSet(choice)], SetEnd(scenario, click: label).Sent);
}
