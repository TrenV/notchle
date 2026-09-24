using System.Globalization;

namespace Notchle.Core.Ui;

// Pure presentation rules of the Windows "island", ported from
// Sources/NotchleMac/UI/NotchUIRules.swift. Same phases, same copy, same shortcuts
// (Enter / Tab / Ctrl+R / Esc); the Windows-only rules (no auto-expand, hover collapse,
// click-to-type) live in IslandBehavior.

/// Text fields of the island.
public enum IslandField { Url, Title, Artist }

/// Keys the island handles itself; everything else goes to the focused text box.
public enum IslandKey { Enter, Escape, Tab, BackTab, CtrlR }

/// What a key means in the current phase.
public abstract record IslandCommand
{
    /// Submit title + artist (or move focus to the empty one).
    public sealed record SubmitGuess : IslandCommand;
    /// Parse the URL field and send Load.
    public sealed record Load : IslandCommand;
    public sealed record Send(GameAction Action) : IslandCommand;
    /// Collapse the island and hand the keyboard back.
    public sealed record Collapse : IslandCommand;
    public sealed record CloseSettings : IslandCommand;
    public sealed record Focus(IslandField Field) : IslandCommand;
}

public enum FieldTransitionKind
{
    None,
    /// A new track: clear both guess fields and focus Title.
    ClearAndFocusTitle,
    /// A retry of the same track: keep what was typed and focus Field (the first wrong half).
    KeepAndFocus,
    /// A link is needed: focus the URL field.
    FocusUrl,
}

public sealed record FieldTransition(FieldTransitionKind Kind, IslandField? Field = null)
{
    public static FieldTransition None { get; } = new(FieldTransitionKind.None);
}

/// The answer, only ever produced by <see cref="IslandRules.RevealedAnswer"/>.
public sealed record RevealedAnswer(string Title, string Artists);

public static class IslandRules
{
    /// Phases in which the track must stay secret.
    public static bool IsGuessPhase(GamePhase phase) =>
        phase is GamePhase.PlayingSnippet or GamePhase.Guessing or GamePhase.Wrong;

    /// Phases that show the Title / Artist(s) fields.
    public static bool ShowsGuessFields(GamePhase phase) =>
        phase is GamePhase.PlayingSnippet or GamePhase.Guessing;

    /// Phases that show the Spotify link field.
    public static bool ShowsUrlField(GamePhase phase) =>
        phase is GamePhase.Idle or GamePhase.Exhausted;

    /// The single gate every view goes through for the answer: null during PlayingSnippet,
    /// Guessing and Wrong (and whenever there is no current track).
    public static RevealedAnswer? RevealedAnswer(GameState state)
    {
        if (state.Phase is not (GamePhase.Correct or GamePhase.Revealed)) return null;
        var track = state.CurrentTrack;
        return track is null ? null : new RevealedAnswer(track.Title, string.Join(", ", track.Artists));
    }

    /// Placeholder of the artist field: with several artists only the count, never names.
    public static string ArtistPlaceholder(int artistCount) =>
        artistCount > 1 ? $"{artistCount} artists, any order" : "Artist(s)";

    /// Extra hint in Wrong when the artist half was wrong and several artists are needed.
    public static string? ArtistHint(Verdict verdict, int artistCount) =>
        !verdict.ArtistCorrect && artistCount > 1 ? $"need all {artistCount}" : null;

    /// Number of credited artists of the current track: safe to show in every phase.
    public static int ArtistCount(GameState state) => state.CurrentTrack?.Artists.Count ?? 1;

    /// "3/20": position of the current track in the set; "–" before a set is loaded.
    public static string ProgressText(GameState state)
    {
        var total = state.CurrentSet.Count;
        if (total <= 0) return "–";
        return state.Phase switch
        {
            GamePhase.SetComplete c => $"{c.CorrectCount}/{total}",
            GamePhase.SetFailed f => $"{f.CorrectCount}/{total}",
            GamePhase.Idle or GamePhase.Exhausted or GamePhase.Loading => "–",
            _ => $"{Math.Min(state.Index + 1, total)}/{total}",
        };
    }

    /// Seconds of the tier, clamped to the configured tiers.
    public static double Seconds(int tier, GameConfig config)
    {
        if (config.Tiers.Count == 0) return 0;
        return config.Tiers[Math.Clamp(tier, 0, config.Tiers.Count - 1)];
    }

    /// "5s", "10s", "2.5s".
    public static string SecondsLabel(double s) =>
        s == Math.Round(s)
            ? $"{(int)s}s"
            : s.ToString("0.0", CultureInfo.InvariantCulture) + "s";

    /// Seconds of the retry offered from Wrong(tier).
    public static double RetrySeconds(int tier, GameConfig config) => Seconds(tier + 1, config);

    /// Field behaviour for a phase change. <paramref name="old"/> is null at launch.
    public static FieldTransition FieldTransitionFor(GamePhase? old, GamePhase @new)
    {
        if (Equals(old, @new)) return FieldTransition.None;
        switch (@new)
        {
            case GamePhase.Idle or GamePhase.Exhausted:
                return new(FieldTransitionKind.FocusUrl, IslandField.Url);
            case GamePhase.PlayingSnippet or GamePhase.Guessing:
                var tier = @new is GamePhase.PlayingSnippet p ? p.TierIndex : ((GamePhase.Guessing)@new).TierIndex;
                // Snippet -> guessing of the same tier: the player may be mid-typing; leave them be.
                if (old is GamePhase.PlayingSnippet op && op.TierIndex == tier && @new is GamePhase.Guessing)
                    return FieldTransition.None;
                if (old is GamePhase.Wrong w && tier > 0)
                    return new(FieldTransitionKind.KeepAndFocus, w.Verdict.TitleCorrect ? IslandField.Artist : IslandField.Title);
                return tier == 0
                    ? new(FieldTransitionKind.ClearAndFocusTitle, IslandField.Title)
                    : new(FieldTransitionKind.KeepAndFocus, IslandField.Title);
            default:
                return FieldTransition.None;
        }
    }

    /// Keyboard mapping. <paramref name="settingsOpen"/>: the settings view covers the phase.
    public static IslandCommand? Command(IslandKey key, GamePhase phase, IslandField? focused, bool settingsOpen = false)
    {
        if (settingsOpen) return key == IslandKey.Escape ? new IslandCommand.CloseSettings() : null;
        switch (key)
        {
            case IslandKey.Enter:
                return phase switch
                {
                    GamePhase.Idle or GamePhase.Exhausted => new IslandCommand.Load(),
                    GamePhase.PlayingSnippet or GamePhase.Guessing => new IslandCommand.SubmitGuess(),
                    GamePhase.Wrong => new IslandCommand.Send(new GameAction.Retry()),
                    GamePhase.Correct or GamePhase.Revealed or GamePhase.Error => new IslandCommand.Send(new GameAction.Next()),
                    GamePhase.SetComplete => new IslandCommand.Send(new GameAction.NextSet()),
                    GamePhase.SetFailed => new IslandCommand.Send(new GameAction.ReplaySet()),
                    _ => null, // Loading
                };
            case IslandKey.CtrlR:
                return phase is GamePhase.Wrong ? new IslandCommand.Send(new GameAction.Retry()) : null;
            case IslandKey.Escape:
                return IsGuessPhase(phase) ? new IslandCommand.Send(new GameAction.GiveUp()) : new IslandCommand.Collapse();
            case IslandKey.Tab or IslandKey.BackTab:
                if (!ShowsGuessFields(phase)) return null;
                return new IslandCommand.Focus(focused == IslandField.Title ? IslandField.Artist : IslandField.Title);
            default:
                return null;
        }
    }

    /// SHQueryUserNotificationState results in which the island hides: QUNS_BUSY (2, a
    /// fullscreen app), QUNS_RUNNING_D3D_FULL_SCREEN (3, a game), QUNS_PRESENTATION_MODE (4).
    public static bool HidesForNotificationState(int state) => state is 2 or 3 or 4;

    /// Settings with the new player mode, or null when nothing changes.
    public static AppSettings? WithPlayerMode(AppSettings settings, PlayerMode mode) =>
        settings.PlayerMode == mode ? null : settings with { PlayerMode = mode };
}

/// Win32 virtual-key code + modifiers → the keys the island owns. Pure, for tests; the WPF
/// layer passes KeyInterop.VirtualKeyFromKey(e.Key).
public static class IslandKeys
{
    public const int VkTab = 0x09, VkReturn = 0x0D, VkEscape = 0x1B, VkR = 0x52;

    public static IslandKey? FromVirtualKey(int vk, bool ctrl, bool alt, bool shift)
    {
        var none = !ctrl && !alt && !shift;
        return vk switch
        {
            VkReturn => none ? IslandKey.Enter : null,
            VkEscape => none ? IslandKey.Escape : null,
            VkTab when none => IslandKey.Tab,
            VkTab when shift && !ctrl && !alt => IslandKey.BackTab,
            VkR when ctrl && !alt && !shift => IslandKey.CtrlR,
            _ => null,
        };
    }
}
