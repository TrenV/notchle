using Notchle.Core;
using Notchle.Windows.Island;

namespace Notchle.Windows.Tests;

/// Hard rule, checked on the real WPF tree: during PlayingSnippet, Guessing and Wrong the
/// title and artists appear nowhere in the island (collapsed or expanded, visible or hidden).
public class IslandSecrecyTests
{
    private static readonly Track Secret = new("t1", "spotify:track:t1", "Zanzibar Nights",
        ["Quill Ostrander", "Mabel Fitch"], 1, null);

    private static GameState State(GamePhase phase) => new()
    {
        Config = GameConfig.Default,
        Listing = new SourceListing(new SourceRef(SourceKind.Playlist, "p"), "List", [Secret]),
        CurrentSet = [Secret],
        Phase = phase,
    };

    public static TheoryData<string> SecretPhases => new() { "playing", "guessing", "wrong" };

    private static GamePhase Phase(string name) => name switch
    {
        "playing" => new GamePhase.PlayingSnippet(0),
        "guessing" => new GamePhase.Guessing(1),
        _ => new GamePhase.Wrong(1, new Verdict(true, false)),
    };

    private static string TextOf(GamePhase phase, bool expanded, bool quitArmed = false)
    {
        string joined = "";
        IslandSta.Run(() =>
        {
            var (root, _, _) = IslandSnapshots.Compose(new IslandSnapshots.Scenario("test", phase)
            {
                Expanded = expanded, State = State(phase), Title = "Paper", Artist = "Kites", QuitArmed = quitArmed,
            });
            joined = string.Join(" | ", IslandSta.AllText(root));
        });
        return joined;
    }

    [Theory]
    [MemberData(nameof(SecretPhases))]
    public void GuessPhasesNeverShowTheTrack(string name)
    {
        foreach (var (expanded, quitArmed) in new[] { (true, false), (false, false), (true, true) })
        {
            var text = TextOf(Phase(name), expanded, quitArmed);
            // Control: the walk does read the island (header / pill text is there).
            Assert.Contains("1/1", text);
            foreach (var secret in new[] { "Zanzibar", "Quill", "Mabel" })
                Assert.DoesNotContain(secret, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// Positive control: the same walk finds the answer where it is allowed.
    [Fact]
    public void CorrectShowsTheAnswer()
    {
        var text = TextOf(new GamePhase.Correct(0), expanded: true);
        Assert.Contains("Zanzibar Nights", text);
        Assert.Contains("Quill Ostrander, Mabel Fitch", text);
    }
}
