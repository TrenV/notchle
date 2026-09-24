using System.Reflection;
using Notchle.Core.Ui;

namespace Notchle.Core.Tests;

/// Hard rule: during PlayingSnippet, Guessing and Wrong the title and artists are never
/// rendered. Every string the island draws comes from IslandScreens / IslandIndicator, so
/// collecting every string they produce proves it headless (the WPF tree walk runs on Windows).
public class UiSecrecyTests
{
    private static readonly string[] Names = ["Zanzibar", "Quill", "Mabel"];

    public static TheoryData<GamePhase> SecretPhases => new()
    {
        new GamePhase.PlayingSnippet(0), new GamePhase.Guessing(1), new GamePhase.Wrong(0, new Verdict(false, false)),
        new GamePhase.Wrong(1, new Verdict(true, false)),
    };

    /// Every string reachable from the object: properties, nested records, lists.
    public static IEnumerable<string> Strings(object? o, int depth = 0)
    {
        if (o is null || depth > 6) yield break;
        if (o is string s) { yield return s; yield break; }
        if (o is System.Collections.IEnumerable list)
        {
            foreach (var item in list)
                foreach (var x in Strings(item, depth + 1)) yield return x;
            yield break;
        }
        var type = o.GetType();
        if (type.IsPrimitive || type.IsEnum || o is double or DateTimeOffset) yield break;
        foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (p.GetIndexParameters().Length > 0 || p.Name == "EqualityContract") continue;
            foreach (var x in Strings(p.GetValue(o), depth + 1)) yield return x;
        }
        foreach (var f in type.GetFields(BindingFlags.Public | BindingFlags.Static))
            if (f.IsLiteral && f.FieldType == typeof(string)) yield return (string)f.GetRawConstantValue()!;
    }

    private static List<string> AllDrawnStrings(GamePhase phase)
    {
        var state = UiFixtures.State(phase);
        var clock = new UiManualClock();
        var session = new IslandSession(clock);
        session.StateDidChange(null, state);
        // The player typed something wrong: the chips echo it, never the answer.
        session.TitleText = "Paper";
        session.ArtistText = "Kites";
        var strings = new List<string>();
        strings.AddRange(Strings(session.Screen(new AppSettings(), "Spotify Connect", false)));
        strings.AddRange(Strings(IslandScreens.Header(state)));
        strings.AddRange(Strings(session.Indicator()));
        return strings;
    }

    [Theory]
    [MemberData(nameof(SecretPhases))]
    public void NothingDrawnInAGuessPhaseNamesTheTrack(GamePhase phase)
    {
        var strings = AllDrawnStrings(phase);
        Assert.NotEmpty(strings);
        foreach (var s in strings)
            foreach (var name in Names)
                Assert.DoesNotContain(name, s, StringComparison.OrdinalIgnoreCase);
    }

    /// Control: the same collector does find the names where they are allowed, so the test
    /// above would fail if a guess screen carried them.
    [Fact]
    public void TheCollectorSeesTheAnswerInCorrectAndRevealed()
    {
        foreach (var phase in new GamePhase[] { new GamePhase.Correct(0), new GamePhase.Revealed(null) })
        {
            var joined = string.Join(" | ", AllDrawnStrings(phase));
            Assert.Contains("Zanzibar Nights", joined);
            Assert.Contains("Quill Ostrander, Mabel Fitch", joined);
        }
    }

    [Fact]
    public void RevealedAnswerIsNullOutsideCorrectAndRevealed()
    {
        foreach (var phase in UiFixtures.AllPhases.Where(p => p is not (GamePhase.Correct or GamePhase.Revealed)))
            Assert.Null(IslandRules.RevealedAnswer(UiFixtures.State(phase)));
        Assert.Null(IslandRules.RevealedAnswer(UiFixtures.State(new GamePhase.Correct(0)) with { Index = 5 }));
    }

    [Fact]
    public void SeveralArtistsDontRevealTheCount()
    {
        var guess = Assert.IsType<IslandScreen.Guess>(
            IslandScreens.Build(UiFixtures.State(new GamePhase.Guessing(0)), "", "", null, true));
        Assert.Equal("Artist(s)", guess.ArtistPlaceholder);
        var wrong = Assert.IsType<IslandScreen.Wrong>(
            IslandScreens.Build(UiFixtures.State(new GamePhase.Wrong(0, new Verdict(true, false))), "T", "Quill", null, true));
        Assert.Null(wrong.ArtistChip.Hint);
    }
}
