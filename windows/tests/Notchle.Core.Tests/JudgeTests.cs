using Notchle.Core;

namespace Notchle.Core.Tests;

/// The non-table judge tests from Tests/NotchleCoreTests/JudgeTests.swift. The table cases
/// live in /spec/judge-cases.json (SpecJudgeCasesTests).
public class JudgeTests
{
    private readonly FuzzyAnswerJudge _judge = new();

    private static Track Track(string title, params string[] artists) =>
        new("id", "spotify:track:id", title, artists, 1, null);

    [Fact]
    public void WholeStringGuardStopsTyposPilingUp()
    {
        // Each word is within its own tolerance, but together too much is wrong.
        Assert.True(FuzzyAnswerJudge.WordsAlign(["dia", "dia"], ["dai", "dai"]));
        Assert.False(_judge.TitleMatches("Dia Dia", Track("Dai Dai", "X")));
        Assert.True(new FuzzyAnswerJudge(0.6).TitleMatches("Dia Dia", Track("Dai Dai", "X")));
    }

    [Fact]
    public void TolerancePerWordLength() =>
        Assert.Equal([0, 0, 1, 1, 2, 2, 3, 3], new[] { 1, 2, 3, 5, 6, 9, 10, 20 }.Select(FuzzyAnswerJudge.Tolerance));

    [Fact]
    public void DamerauLevenshtein()
    {
        Assert.Equal(3, FuzzyAnswerJudge.DamerauLevenshtein("kitten", "sitting"));
        Assert.Equal(1, FuzzyAnswerJudge.DamerauLevenshtein("grande", "grnade"));
        Assert.Equal(3, FuzzyAnswerJudge.DamerauLevenshtein("", "abc"));
        Assert.Equal(0, FuzzyAnswerJudge.DamerauLevenshtein("abc", "abc"));
    }

    [Fact]
    public void DistancesCountGraphemeClustersLikeSwiftCharacters()
    {
        // "e" + combining acute is one Character in Swift; a UTF-16 port would count two.
        Assert.Equal(1, FuzzyAnswerJudge.DamerauLevenshtein("e\u0301", "a"));
        Assert.Equal(1, AnswerText.Length("\u0928\u093F"));
        Assert.Equal(1, AnswerText.Length("\U0001F44D\U0001F3FD"));
    }

    [Fact]
    public void FoldingKeepsNonLatinMarksLikeFoundation()
    {
        // A plain "strip every non-spacing mark" would turn ポ into ホ and drop Hebrew points.
        Assert.Equal(["\u30DD\u30B1\u30E2\u30F3"], FuzzyAnswerJudge.Tokens("\u30DD\u30B1\u30E2\u30F3"));
        Assert.NotEqual(FuzzyAnswerJudge.Tokens("\u30DB"), FuzzyAnswerJudge.Tokens("\u30DD"));
        Assert.Equal(["cafe"], FuzzyAnswerJudge.Tokens("Cafe\u0301"));
    }

    [Fact]
    public void MalformedInputNeverThrows()
    {
        // Lone surrogates and noncharacters make .NET's Normalize throw; the judge must not.
        Assert.False(_judge.TitleMatches("\uD800", Track("\uFFFE x", "X")));
        Assert.True(_judge.TitleMatches("x \uFDD0", Track("x \uFDD0", "X")));
        Assert.False(_judge.Judge(new Guess("\uDC00a", "\uFFFF"), Track("a", "b")).IsCorrect);
    }

    [Fact]
    public void BracketsDashesAndFeaturingAreDecorations()
    {
        Assert.Equal(" Song  x  ", FuzzyAnswerJudge.RemovingBrackets("(intro) Song [a (b)] x )")); // stray ")" = space
        Assert.Equal("Song", FuzzyAnswerJudge.BeforeDashSuffix("Song \u2014 Live - 2011"));
        Assert.Equal(["a", "b"], FuzzyAnswerJudge.BeforeFeaturing(["a", "b", "feat", "c"]));
        Assert.Equal(["feat", "c"], FuzzyAnswerJudge.BeforeFeaturing(["feat", "c"]));
    }

    [Fact]
    public void TooManyCreditedArtistsIsWrongNotSlow()
    {
        var many = Enumerable.Range(0, 64).Select(i => $"Artist{i}").ToArray();
        Assert.False(_judge.ArtistMatches(string.Join(" ", many), many));
    }
}
