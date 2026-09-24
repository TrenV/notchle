using Notchle.Core.Ui;

namespace Notchle.Core.Tests;

/// The copy of every screen (same as the macOS notch) and the collapsed pill indicators.
public class UiScreensTests
{
    private static IslandScreen Build(GamePhase phase, bool fullTrack = true, string title = "", string artist = "") =>
        IslandScreens.Build(UiFixtures.State(phase), title, artist, null, fullTrack);

    [Fact]
    public void SourceEntryCopy()
    {
        var idle = Assert.IsType<IslandScreen.SourceEntry>(Build(new GamePhase.Idle()));
        Assert.Equal("Paste a Spotify link", idle.Heading);
        Assert.Equal("A playlist, album or artist. You get 5 seconds per song.", idle.Subheading);
        var done = Assert.IsType<IslandScreen.SourceEntry>(Build(new GamePhase.Exhausted()));
        Assert.Equal("You've heard them all", done.Heading);
        Assert.Equal("Every song in List has been played. Try another link.", done.Subheading);
    }

    [Fact]
    public void GuessCopyFollowsTheSnippet()
    {
        var playing = Assert.IsType<IslandScreen.Guess>(Build(new GamePhase.PlayingSnippet(1)));
        Assert.True(playing.Playing);
        Assert.Equal("Listening · 10s", playing.Status);
        Assert.Equal(10, playing.Seconds);
        Assert.Equal([AttemptState.Missed, AttemptState.Current, AttemptState.Later], playing.Attempts.Select(a => a.State));
        Assert.Equal(["5s", "10s", "15s"], playing.Attempts.Select(a => a.Label));
        var guessing = Assert.IsType<IslandScreen.Guess>(Build(new GamePhase.Guessing(0)));
        Assert.Equal("What's this song?", guessing.Status);
    }

    [Fact]
    public void GuessScreenOffersSkipUntilTheLastTierAndShowsSkippedAttemptsAsUsed()
    {
        var t0 = Assert.IsType<IslandScreen.Guess>(Build(new GamePhase.PlayingSnippet(0)));
        Assert.Equal("Skip · 10s", t0.SkipLabel);
        var t1 = Assert.IsType<IslandScreen.Guess>(Build(new GamePhase.PlayingSnippet(1))); // after a skip
        Assert.Equal("Skip · 15s", t1.SkipLabel);
        Assert.Equal([AttemptState.Missed, AttemptState.Current, AttemptState.Later], t1.Attempts.Select(a => a.State));
        var t2 = Assert.IsType<IslandScreen.Guess>(Build(new GamePhase.Guessing(2)));
        Assert.Null(t2.SkipLabel);
        Assert.Equal([AttemptState.Missed, AttemptState.Missed, AttemptState.Current], t2.Attempts.Select(a => a.State));
    }

    [Fact]
    public void WrongCopy()
    {
        var half = Assert.IsType<IslandScreen.Wrong>(Build(new GamePhase.Wrong(0, new Verdict(true, false)), title: "T", artist: "A"));
        Assert.Equal("Half right", half.Headline);
        Assert.Equal("Retry · 10s", half.RetryLabel);
        Assert.Equal(new VerdictChip("Title", true, "T", null), half.TitleChip);
        Assert.Equal(new VerdictChip("Artist(s)", false, "A", null), half.ArtistChip);
        Assert.Equal([AttemptState.Missed, AttemptState.Later, AttemptState.Later], half.Attempts.Select(a => a.State));
        var none = Assert.IsType<IslandScreen.Wrong>(Build(new GamePhase.Wrong(1, new Verdict(false, false))));
        Assert.Equal("Not quite", none.Headline);
        Assert.Equal("Retry · 15s", none.RetryLabel);
    }

    [Fact]
    public void AnswerCopy()
    {
        var correct = Assert.IsType<IslandScreen.Answer>(Build(new GamePhase.Correct(1)));
        Assert.Equal("Got it in 10s", correct.Headline);
        Assert.Null(correct.PreviewHint);
        var preview = Assert.IsType<IslandScreen.Answer>(Build(new GamePhase.Correct(0), fullTrack: false));
        Assert.Equal("Preview ends at 30s", preview.PreviewHint);
        Assert.Equal("You gave up. It was", Assert.IsType<IslandScreen.Answer>(Build(new GamePhase.Revealed(null))).Headline);
        Assert.Equal("Out of tries. It was",
            Assert.IsType<IslandScreen.Answer>(Build(new GamePhase.Revealed(new Verdict(false, true)))).Headline);
    }

    private static IslandScreen.SetEnd SetEnd(bool complete, int newAvailable) =>
        Assert.IsType<IslandScreen.SetEnd>(IslandScreens.Build(IslandDemoGame.SetEndState(complete, newAvailable), "", "", null, true));

    [Fact]
    public void SetCompleteOffersNewSongsThenReplay()
    {
        var done = SetEnd(complete: true, newAvailable: 40);
        Assert.Equal(("Perfect set!", 20, 20), (done.Headline, done.Correct, done.Total));
        Assert.Equal(new SetEndOption(SetChoice.AllNew, "20 new songs", KeyHints.Enter), done.Primary);
        Assert.Equal(new SetEndOption(SetChoice.Replay, "Replay these 20", KeyHints.CtrlShiftR), done.Secondary);
        Assert.Null(done.Tertiary);
    }

    [Fact]
    public void SetFailedOffersKeepMissesReplayAndNew()
    {
        var failed = SetEnd(complete: false, newAvailable: 40);
        Assert.Equal(("Set over", 13, 20), (failed.Headline, failed.Correct, failed.Total));
        Assert.Equal("7 missed. Keep practising them, or move on.", failed.Body);
        Assert.Equal(new SetEndOption(SetChoice.KeepMisses, "Keep misses + new", KeyHints.Enter), failed.Primary);
        Assert.Equal(new SetEndOption(SetChoice.Replay, "Replay these 20", KeyHints.CtrlShiftR), failed.Secondary);
        Assert.Equal(new SetEndOption(SetChoice.AllNew, "20 new songs", KeyHints.CtrlShiftN), failed.Tertiary);
    }

    [Theory]
    [InlineData(7, "7 new songs")]
    [InlineData(1, "1 new song")]
    public void NewSongsLabelShowsTheLiveCount(int available, string label) =>
        Assert.Equal(label, SetEnd(complete: false, available).Tertiary?.Label);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NoNewSongsLeavesOnlyReplayWithTheHint(bool complete)
    {
        var end = SetEnd(complete, newAvailable: 0);
        Assert.Equal([new SetEndOption(SetChoice.Replay, "Replay these 20", KeyHints.Enter)], end.Options.ToArray());
        Assert.Equal("No new songs left in this playlist · paste another link", end.Body);
    }

    [Fact]
    public void SetEndScreenIsStableAcrossBuilds() =>
        // The view is only rebuilt when the screen changes; value equality keeps clicks landing.
        Assert.Equal(SetEnd(complete: false, 40), SetEnd(complete: false, 40));

    [Fact]
    public void LoadingAndErrorCopy()
    {
        Assert.Equal("Loading List…", Assert.IsType<IslandScreen.Loading>(Build(new GamePhase.Loading())).Text);
        var state = UiFixtures.State(new GamePhase.Loading()) with { Listing = null };
        Assert.Equal("Loading songs…", Assert.IsType<IslandScreen.Loading>(IslandScreens.Build(state, "", "", null, true)).Text);
        Assert.Equal("boom", Assert.IsType<IslandScreen.Error>(Build(new GamePhase.Error("boom"))).Message);
    }

    [Fact]
    public void HeaderShowsListingAndProgress()
    {
        Assert.Equal(new IslandHeader("List", "1/1"), IslandScreens.Header(UiFixtures.State(new GamePhase.Guessing(0))));
        Assert.Equal(new IslandHeader("Notchle", null), IslandScreens.Header(IslandDemoGame.SampleState(new GamePhase.Idle())));
    }

    [Fact]
    public void SettingsShowModeTiersAndConnectHint()
    {
        var s = IslandScreens.Settings(new AppSettings(), "30-second previews");
        Assert.Equal(PlayerMode.Preview, s.Mode);
        Assert.Equal("5s · 10s · 15s", s.Snippets);
        Assert.Equal("Now using 30-second previews", s.NowUsing);
        var connect = IslandScreens.Settings(new AppSettings { PlayerMode = PlayerMode.SpotifyConnect }, "");
        Assert.Contains("client id", connect.ConnectHint);
        Assert.Equal("", connect.NowUsing);
        var ready = IslandScreens.Settings(new AppSettings { PlayerMode = PlayerMode.SpotifyConnect, SpotifyClientId = "abc" }, "");
        Assert.DoesNotContain("client id", ready.ConnectHint);
    }

    // MARK: collapsed pill

    private static readonly DateTimeOffset T0 = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    private static IslandIndicator Indicator(GamePhase phase, double at = 0, double? snippet = null,
        double? celebration = null, double? setEnd = null) =>
        IslandIndicator.For(IslandDemoGame.SampleState(phase), T0.AddSeconds(at),
            snippet is { } s ? T0.AddSeconds(s) : null, celebration is { } c ? T0.AddSeconds(c) : null,
            setEnd is { } e ? T0.AddSeconds(e) : null);

    [Fact]
    public void SnippetRingFillsOverTheSnippet()
    {
        var i = Indicator(new GamePhase.PlayingSnippet(1), at: 5, snippet: 0);
        Assert.Equal(IndicatorGlyph.SnippetRing, i.Glyph);
        Assert.Equal(0.5, i.Progress, 3);
        Assert.Equal("7/20", i.Text);
        Assert.Equal(1, Indicator(new GamePhase.PlayingSnippet(0), at: 9, snippet: 0).Progress);
    }

    [Fact]
    public void QuestionMarkWhileAGuessIsAwaited()
    {
        Assert.Equal(IndicatorGlyph.Question, Indicator(new GamePhase.Guessing(0)).Glyph);
        Assert.Equal(IndicatorGlyph.QuestionAfterWrong, Indicator(new GamePhase.Wrong(0, new Verdict(true, false))).Glyph);
    }

    [Fact]
    public void CheckFlashesAfterACorrectGuessThenTheSongPlaysOn()
    {
        var flash = Indicator(new GamePhase.Correct(0), at: 1, celebration: 0);
        Assert.Equal(IndicatorGlyph.Check, flash.Glyph);
        Assert.Equal("Got it in 5s", flash.Caption);
        var after = Indicator(new GamePhase.Correct(0), at: 3, celebration: 0);
        Assert.Equal(IndicatorGlyph.Equalizer, after.Glyph);
        Assert.Null(after.Caption);
        // No celebration recorded (e.g. launched into Correct): no flash.
        Assert.Equal(IndicatorGlyph.Equalizer, Indicator(new GamePhase.Correct(0), at: 1).Glyph);
    }

    [Fact]
    public void SetResultShowsBriefly()
    {
        Assert.Equal("Perfect set!", Indicator(new GamePhase.SetComplete(20), at: 1, setEnd: 0).Caption);
        Assert.Null(Indicator(new GamePhase.SetComplete(20), at: 5, setEnd: 0).Caption);
        var failed = Indicator(new GamePhase.SetFailed(14), at: 1, setEnd: 0);
        Assert.Equal(("Set over", "14/20"), (failed.Caption, failed.Text));
    }

    [Fact]
    public void OtherGlyphs()
    {
        Assert.Equal(IndicatorGlyph.None, Indicator(new GamePhase.Idle()).Glyph);
        Assert.Equal(IndicatorGlyph.Spinner, Indicator(new GamePhase.Loading()).Glyph);
        Assert.Equal(IndicatorGlyph.Equalizer, Indicator(new GamePhase.Revealed(null)).Glyph);
        Assert.Equal(IndicatorGlyph.Warning, Indicator(new GamePhase.Error("e")).Glyph);
        Assert.Equal(IndicatorGlyph.Note, Indicator(new GamePhase.Exhausted()).Glyph);
    }
}
