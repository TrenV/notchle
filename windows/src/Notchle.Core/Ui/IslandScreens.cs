namespace Notchle.Core.Ui;

// What the expanded island shows, as plain data: every string the WPF views draw comes from
// here, so the copy and the secrecy rule are tested headless. Only Answer carries the title and
// artists, and it is built only from IslandRules.RevealedAnswer.

public enum AttemptState { Missed, Current, Later }

/// One "5s / 10s / 15s" pill.
public sealed record AttemptPill(string Label, AttemptState State);

/// ✓/✗ chip for one half of a verdict, with what the player typed.
public sealed record VerdictChip(string Label, bool Correct, string Guess, string? Hint);

public sealed record IslandHeader(string Title, string? Progress)
{
    /// Tooltip / accessible name of the quit-playlist button.
    public const string QuitLabel = "Quit playlist";
    /// The red capsule after the first press.
    public const string QuitConfirmLabel = "Quit playlist?";
}

public abstract record IslandScreen
{
    /// Idle / Exhausted: paste a link.
    public sealed record SourceEntry(string Heading, string Subheading, string? UrlMessage) : IslandScreen
    {
        public const string Placeholder = "https://open.spotify.com/playlist/…";
        public const string Footnote = "Links from open.spotify.com or spotify: URIs";
        public const string LoadLabel = "Load";
    }

    public sealed record Loading(string Text) : IslandScreen;

    /// PlayingSnippet / Guessing. Never holds the track. <paramref name="SkipLabel"/>: "Skip · 10s",
    /// null at the last tier (no Skip button there).
    public sealed record Guess(bool Playing, string Status, double Seconds, IReadOnlyList<AttemptPill> Attempts,
        string ArtistPlaceholder, string? SkipLabel = null) : IslandScreen
    {
        public const string TitlePlaceholder = "Title";
        public const string GiveUpLabel = "Give up";
        public const string SubmitLabel = "Submit";
        /// Tooltip of the ↺ button: the snippet again, same tier, no attempt used.
        public const string RestartLabel = "Replay snippet";
    }

    /// Wrong. Never holds the track; the chips show what the player typed.
    public sealed record Wrong(string Headline, IReadOnlyList<AttemptPill> Attempts, VerdictChip TitleChip,
        VerdictChip ArtistChip, string RetryLabel) : IslandScreen
    {
        public const string GiveUpLabel = "Give up";
    }

    /// Correct / Revealed: the only screen with the answer.
    public sealed record Answer(bool Correct, string Headline, string Title, string Artists, string? PreviewHint) : IslandScreen
    {
        public const string NextLabel = "Next";
        /// Tooltip of the ↺ button: the whole song from 0:00.
        public const string RestartLabel = "Restart song";
    }

    /// SetComplete / SetFailed. <paramref name="Primary"/> is Enter; Secondary / Tertiary are
    /// null when not offered (no new songs left: only Replay).
    public sealed record SetEnd(bool Complete, int Correct, int Total, string Headline, string Body,
        SetEndOption Primary, SetEndOption? Secondary = null, SetEndOption? Tertiary = null) : IslandScreen
    {
        public const string KeepMissesLabel = "Keep misses + new";
        public const string NoNewHint = "No new songs left in this playlist · paste another link";

        public IEnumerable<SetEndOption> Options =>
            new[] { Primary, Secondary, Tertiary }.OfType<SetEndOption>();
    }

    public sealed record Error(string Message) : IslandScreen
    {
        public const string Heading = "Something went wrong";
        public const string ResetLabel = "Reset";
        public const string SkipLabel = "Skip";
    }

    public sealed record Settings(PlayerMode Mode, string Snippets, string NowUsing, string ConnectHint) : IslandScreen
    {
        public const string Heading = "Settings";
        public const string PlayWithLabel = "Play songs with";
        public const string PreviewLabel = "30-second previews";
        public const string ConnectLabel = "Spotify Connect";
        public const string SnippetsLabel = "Snippets";
        public const string DoneLabel = "Done";
    }
}

/// One set-end button: what it starts and its label ("20 new songs", "Replay these 20").
public sealed record SetEndOption(SetChoice Choice, string Label, string KeyHint);

/// Keyboard hints drawn next to button labels (Windows names, not the Mac glyphs).
public static class KeyHints
{
    public const string Enter = "Enter";
    public const string Esc = "Esc";
    public const string CtrlR = "Ctrl+R";
    public const string CtrlShiftR = "Ctrl+Shift+R";
    public const string CtrlShiftS = "Ctrl+Shift+S";
    public const string CtrlShiftN = "Ctrl+Shift+N";
    public const string CtrlN = "Ctrl+N";
    public const string Hotkey = "Ctrl+Alt+N";
}

public static class IslandScreens
{
    public static IslandHeader Header(GameState state) =>
        new(state.Listing?.Name ?? "Notchle", state.CurrentSet.Count > 0 ? IslandRules.ProgressText(state) : null);

    /// The phase screen. <paramref name="title"/> / <paramref name="artist"/> are what the
    /// player typed (for the Wrong chips).
    public static IslandScreen Build(GameState state, string title, string artist, string? urlMessage,
        bool playerPlaysFullTrack)
    {
        var config = state.Config;
        switch (state.Phase)
        {
            case GamePhase.Idle:
                return new IslandScreen.SourceEntry("Paste a Spotify link",
                    "A playlist, album or artist. You get 5 seconds per song.", urlMessage);
            case GamePhase.Exhausted:
                return new IslandScreen.SourceEntry("You've heard them all",
                    $"Every song in {state.Listing?.Name ?? "this listing"} has been played. Try another link.", urlMessage);
            case GamePhase.Loading:
                return new IslandScreen.Loading(state.Listing?.Name is { } name ? $"Loading {name}…" : "Loading songs…");
            case GamePhase.PlayingSnippet p:
                return Guess(state, p.TierIndex, playing: true);
            case GamePhase.Guessing g:
                return Guess(state, g.TierIndex, playing: false);
            case GamePhase.Wrong w:
            {
                var retry = IslandRules.RetrySeconds(w.TierIndex, config);
                var count = IslandRules.ArtistCount(state);
                return new IslandScreen.Wrong(
                    w.Verdict.TitleCorrect || w.Verdict.ArtistCorrect ? "Half right" : "Not quite",
                    Attempts(config, w.TierIndex, currentMissed: true),
                    new VerdictChip("Title", w.Verdict.TitleCorrect, title, null),
                    new VerdictChip("Artist(s)", w.Verdict.ArtistCorrect, artist, IslandRules.ArtistHint(w.Verdict, count)),
                    $"Retry · {IslandRules.SecondsLabel(retry)}");
            }
            case GamePhase.Correct c:
            {
                var answer = IslandRules.RevealedAnswer(state);
                return new IslandScreen.Answer(true,
                    $"Got it in {IslandRules.SecondsLabel(IslandRules.Seconds(c.TierIndex, config))}",
                    answer?.Title ?? "–", answer?.Artists ?? "", PreviewHint(playerPlaysFullTrack));
            }
            case GamePhase.Revealed r:
            {
                var answer = IslandRules.RevealedAnswer(state);
                return new IslandScreen.Answer(false,
                    r.Verdict is null ? "You gave up. It was" : "Out of tries. It was",
                    answer?.Title ?? "–", answer?.Artists ?? "", PreviewHint(playerPlaysFullTrack));
            }
            case GamePhase.SetComplete sc:
                return SetEnd(state, true, sc.CorrectCount);
            case GamePhase.SetFailed sf:
                return SetEnd(state, false, sf.CorrectCount);
            case GamePhase.Error e:
                return new IslandScreen.Error(e.Message);
            default:
                return new IslandScreen.Error("Unknown state");
        }
    }

    public static IslandScreen.Settings Settings(AppSettings settings, string playerName) => new(
        settings.PlayerMode,
        string.Join(" · ", settings.Config.Tiers.Select(IslandRules.SecondsLabel)),
        string.IsNullOrEmpty(playerName) ? "" : $"Now using {playerName}",
        ConnectHint(settings));

    /// The "Connect Spotify" hint under the player picker.
    public static string ConnectHint(AppSettings settings) => settings.PlayerMode switch
    {
        PlayerMode.SpotifyConnect when string.IsNullOrWhiteSpace(settings.SpotifyClientId) =>
            "Connect Spotify: full songs need Spotify Premium and the client id of your own Spotify developer app.",
        PlayerMode.SpotifyConnect =>
            "Connect Spotify: full songs play on your active Spotify device (Premium).",
        _ => "Previews work for everyone, no login. Pick Spotify Connect to play full songs.",
    };

    private static IslandScreen.SetEnd SetEnd(GameState state, bool complete, int correct)
    {
        var total = Math.Max(state.CurrentSet.Count, correct);
        var available = GameEngine.AvailableNewCount(state);
        var options = IslandRules.SetEndChoices(complete ? new GamePhase.SetComplete(correct) : new GamePhase.SetFailed(correct), available)
            .Select((choice, i) => new SetEndOption(choice, choice switch
            {
                SetChoice.Replay => $"Replay these {total}",
                SetChoice.KeepMisses => IslandScreen.SetEnd.KeepMissesLabel,
                _ => available == 1 ? "1 new song" : $"{available} new songs",
            }, i == 0 ? KeyHints.Enter : choice == SetChoice.Replay ? KeyHints.CtrlShiftR : KeyHints.CtrlShiftN))
            .ToArray();
        var missed = total - correct;
        var body = available <= 0 ? IslandScreen.SetEnd.NoNewHint
            : complete ? "All right. Play new songs, or these again."
            : $"{missed} missed. Keep practising them, or move on.";
        return new IslandScreen.SetEnd(complete, correct, total, complete ? "Perfect set!" : "Set over", body,
            options[0], options.ElementAtOrDefault(1), options.ElementAtOrDefault(2));
    }

    private static IslandScreen.Guess Guess(GameState state, int tier, bool playing)
    {
        var seconds = IslandRules.Seconds(tier, state.Config);
        return new IslandScreen.Guess(playing,
            playing ? $"Listening · {IslandRules.SecondsLabel(seconds)}" : "What's this song?",
            seconds, Attempts(state.Config, tier, currentMissed: false),
            IslandRules.ArtistPlaceholder(IslandRules.ArtistCount(state)),
            IslandRules.SkipLabel(state.Phase, state.Config));
    }

    /// Used tiers red, current white, later dim; in Wrong the current tier counts as missed.
    public static IReadOnlyList<AttemptPill> Attempts(GameConfig config, int current, bool currentMissed) =>
        config.Tiers.Select((s, i) => new AttemptPill(IslandRules.SecondsLabel(s),
            i < current || (i == current && currentMissed) ? AttemptState.Missed
            : i == current ? AttemptState.Current : AttemptState.Later)).ToList();

    private static string? PreviewHint(bool playerPlaysFullTrack) =>
        playerPlaysFullTrack ? null : "Preview ends at 30s";
}

/// Glyph on the left of the collapsed pill.
public enum IndicatorGlyph
{
    None,
    Spinner,
    /// Snippet playing: a ring that fills over the snippet, with a pulsing dot.
    SnippetRing,
    /// A guess is awaited.
    Question,
    /// Wrong: a retry / give-up decision is awaited.
    QuestionAfterWrong,
    /// Correct, for a moment after the celebration (confetti still bursting).
    Check,
    /// The song keeps playing (Correct after the flash, Revealed).
    Equalizer,
    PerfectSet,
    SetOver,
    Note,
    Warning,
}

/// What the collapsed pill shows.
/// <param name="Caption">Short centre text for flashes ("Got it in 5s", "Perfect set!").</param>
/// <param name="Progress">0..1 for SnippetRing.</param>
public sealed record IslandIndicator(IndicatorGlyph Glyph, string Text, string? Caption = null, double Progress = 0)
{
    public static readonly TimeSpan CorrectFlash = TimeSpan.FromSeconds(2.2);
    public static readonly TimeSpan SetResultFlash = TimeSpan.FromSeconds(4);

    public static IslandIndicator For(GameState state, DateTimeOffset now, DateTimeOffset? snippetStart,
        DateTimeOffset? celebrationStart, DateTimeOffset? setEndedAt)
    {
        var progress = IslandRules.ProgressText(state);
        switch (state.Phase)
        {
            case GamePhase.Idle:
                return new(IndicatorGlyph.None, "");
            case GamePhase.Loading:
                return new(IndicatorGlyph.Spinner, "–");
            case GamePhase.PlayingSnippet p:
            {
                var seconds = IslandRules.Seconds(p.TierIndex, state.Config);
                var fraction = snippetStart is { } s && seconds > 0
                    ? Math.Clamp((now - s).TotalSeconds / seconds, 0, 1) : 0;
                return new(IndicatorGlyph.SnippetRing, progress, null, fraction);
            }
            case GamePhase.Guessing:
                return new(IndicatorGlyph.Question, progress);
            case GamePhase.Wrong:
                return new(IndicatorGlyph.QuestionAfterWrong, progress);
            case GamePhase.Correct c:
                if (celebrationStart is { } cs && now - cs < CorrectFlash && now >= cs)
                    return new(IndicatorGlyph.Check, progress,
                        $"Got it in {IslandRules.SecondsLabel(IslandRules.Seconds(c.TierIndex, state.Config))}");
                return new(IndicatorGlyph.Equalizer, progress);
            case GamePhase.Revealed:
                return new(IndicatorGlyph.Equalizer, progress);
            case GamePhase.SetComplete:
                return new(IndicatorGlyph.PerfectSet, progress, Flashing(now, setEndedAt) ? "Perfect set!" : null);
            case GamePhase.SetFailed:
                return new(IndicatorGlyph.SetOver, progress, Flashing(now, setEndedAt) ? "Set over" : null);
            case GamePhase.Exhausted:
                return new(IndicatorGlyph.Note, "–");
            case GamePhase.Error:
                return new(IndicatorGlyph.Warning, progress);
            default:
                return new(IndicatorGlyph.Note, progress);
        }
    }

    private static bool Flashing(DateTimeOffset now, DateTimeOffset? since) =>
        since is { } s && now >= s && now - s < SetResultFlash;
}
