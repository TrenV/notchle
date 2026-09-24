using System.Text;
using System.Text.Json;
using Notchle.Core;

namespace Notchle.Core.Tests;

/// The shared, data-driven spec in /spec (see spec/README.md). The Swift suite
/// (Tests/NotchleCoreTests/SpecJudgeCasesTests.swift) runs the same files against the reference
/// implementation, so green on both sides means the judges agree case for case.
public static class Spec
{
    public static string Path(string name, [System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        System.IO.Path.GetFullPath(System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(here)!, "..", "..", "..", "spec", name));

    public static JsonElement Load(string name) =>
        JsonDocument.Parse(File.ReadAllBytes(Path(name))).RootElement;

    public static readonly JsonElement Judge = Load("judge-cases.json");

    public static string[] Strings(this JsonElement array) =>
        array.EnumerateArray().Select(e => e.GetString()!).ToArray();

    /// One theory row per case: (index, readable label). Primitives keep every case a separate
    /// test in the runner; the test looks the case up by index.
    public static IEnumerable<object[]> Rows(string section, Func<JsonElement, string> label) =>
        Judge.GetProperty(section).EnumerateArray().Select((c, i) => new object[] { i, label(c) });

    public static JsonElement Case(string section, int index) => Judge.GetProperty(section)[index];
}

public class SpecJudgeCasesTests
{
    private readonly FuzzyAnswerJudge _judge = new();

    public static IEnumerable<object[]> TitleRows() => Spec.Rows("title", c =>
        $"\"{c.GetProperty("guess").GetString()}\" vs \"{c.GetProperty("title").GetString()}\" -> {c.GetProperty("expected").GetBoolean()}");

    public static IEnumerable<object[]> ArtistRows() => Spec.Rows("artist", c =>
        $"\"{c.GetProperty("guess").GetString()}\" vs [{string.Join(", ", c.GetProperty("artists").Strings())}] -> {c.GetProperty("expected").GetBoolean()}");

    public static IEnumerable<object[]> VerdictRows() => Spec.Rows("verdict", c => c.GetProperty("note").GetString()!);

    public static IEnumerable<object[]> TokensRows() => Spec.Rows("tokens", c =>
        $"\"{c.GetProperty("input").GetString()}\" ({c.GetProperty("note").GetString()})");

    [Fact]
    public void SpecFileHasEverySection()
    {
        Assert.Equal(60, Spec.Judge.GetProperty("title").GetArrayLength());
        Assert.Equal(44, Spec.Judge.GetProperty("artist").GetArrayLength());
        Assert.NotEqual(0, Spec.Judge.GetProperty("verdict").GetArrayLength());
        Assert.NotEqual(0, Spec.Judge.GetProperty("tokens").GetArrayLength());
    }

    [Theory, MemberData(nameof(TitleRows))]
    public void Title(int index, string label)
    {
        var c = Spec.Case("title", index);
        var track = new Track("i", "spotify:track:i", c.GetProperty("title").GetString()!, c.GetProperty("artists").Strings(), 1, null);
        Assert.True(c.GetProperty("expected").GetBoolean() == _judge.TitleMatches(c.GetProperty("guess").GetString()!, track), label);
    }

    [Theory, MemberData(nameof(ArtistRows))]
    public void Artist(int index, string label)
    {
        var c = Spec.Case("artist", index);
        Assert.True(c.GetProperty("expected").GetBoolean()
            == _judge.ArtistMatches(c.GetProperty("guess").GetString()!, c.GetProperty("artists").Strings()), label);
    }

    [Theory, MemberData(nameof(VerdictRows))]
    public void Verdict(int index, string label)
    {
        var c = Spec.Case("verdict", index);
        var track = new Track("i", "spotify:track:i", c.GetProperty("title").GetString()!, c.GetProperty("artists").Strings(), 1, null);
        var guess = new Guess(c.GetProperty("guessTitle").GetString()!, c.GetProperty("guessArtist").GetString()!);
        Assert.Equal(new Notchle.Core.Verdict(c.GetProperty("titleCorrect").GetBoolean(), c.GetProperty("artistCorrect").GetBoolean()),
            _judge.Judge(guess, track));
        Assert.NotNull(label);
    }

    /// Compared under canonical equivalence (NFC), which is how Swift compares Strings.
    [Theory, MemberData(nameof(TokensRows))]
    public void Tokens(int index, string label)
    {
        var c = Spec.Case("tokens", index);
        var expected = c.GetProperty("tokens").Strings().Select(t => t.Normalize(NormalizationForm.FormC));
        var actual = FuzzyAnswerJudge.Tokens(c.GetProperty("input").GetString()!).Select(t => t.Normalize(NormalizationForm.FormC));
        Assert.True(expected.SequenceEqual(actual), $"{label}: [{string.Join(", ", actual)}]");
    }
}
