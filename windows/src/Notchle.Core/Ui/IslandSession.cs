namespace Notchle.Core.Ui;

/// View-local state of the island (text fields, focus requests, animation clocks) plus the
/// expansion rules (<see cref="Behavior"/>). Port of Sources/NotchleMac/UI/NotchUIState.swift
/// without the Mac's auto-expand: on Windows a phase change never opens the island.
/// The WPF layer reads this, writes the field texts back, and forwards keys / pointer / focus.
public sealed class IslandSession
{
    private readonly TimeProvider _clock;

    public IslandSession(TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
        Behavior = new IslandBehavior(_clock);
        Quit = new QuitConfirmation(_clock);
        ClearHistoryConfirmation = new QuitConfirmation(_clock);
        Behavior.Changed += () =>
        {
            if (!Behavior.IsExpanded)
            {
                ShowingSettings = false;
                FocusedField = null;
                Tab = IslandTab.Play; // the island always opens on the game
                Quit.Disarm(); // a confirmation nobody can see can't be confirmed
                ClearHistoryConfirmation.Disarm();
            }
        };
    }

    public IslandBehavior Behavior { get; }
    /// The two-step quit-playlist confirmation.
    public QuitConfirmation Quit { get; }
    public GameState State { get; private set; } = new() { Config = GameConfig.Default };

    /// The two-step "Clear history" at the bottom of the History tab (same timing as quit).
    public QuitConfirmation ClearHistoryConfirmation { get; }

    public Action<GameAction> Send { get; set; } = _ => { };
    /// Forgets the play history (the view model's ClearHistory); called on the confirming press.
    public Action ClearHistory { get; set; } = () => { };
    /// Play or History. The game keeps running while History is shown.
    public IslandTab Tab { get; private set; } = IslandTab.Play;
    /// The History tab is what the island shows (not covered by settings).
    public bool ShowingHistory => Tab == IslandTab.History && !ShowingSettings;
    public bool ClearHistoryArmed => ClearHistoryConfirmation.IsArmed;
    /// Turns the link field into a source. The demo swaps it for a fake.
    public Func<string, SourceRef?> ParseSource { get; set; } = SourceRefParser.TryParse;

    public string UrlText { get; set; } = "";
    public string TitleText { get; set; } = "";
    public string ArtistText { get; set; } = "";
    /// Shown under the link field, e.g. "Unsupported link".
    public string? UrlMessage { get; set; }
    public bool ShowingSettings { get; set; }

    /// Focus the view should move to; <see cref="FocusToken"/> changes on every request so the
    /// same field can be asked for twice.
    public IslandField? RequestedFocus { get; private set; }
    public int FocusToken { get; private set; }
    /// The field that has keyboard focus, reported back by the view.
    public IslandField? FocusedField { get; set; }

    public DateTimeOffset? SnippetStart { get; private set; }
    public DateTimeOffset? CelebrationStart { get; private set; }
    public ulong CelebrationSeed { get; private set; } = 1;
    public DateTimeOffset? SetEndedAt { get; private set; }

    private DateTimeOffset Now => _clock.GetUtcNow();

    public bool HasTypedGuess => TitleText.Trim().Length > 0 || ArtistText.Trim().Length > 0;
    public bool CanSubmitGuess => TitleText.Trim().Length > 0 && ArtistText.Trim().Length > 0;

    /// Text in the fields the current phase shows (feeds the "unless typing" rule).
    public bool HasTextInVisibleFields =>
        IslandRules.ShowsGuessFields(State.Phase) ? HasTypedGuess
        : IslandRules.ShowsUrlField(State.Phase) && UrlText.Trim().Length > 0;

    public IslandMode Mode =>
        Behavior.IsExpanded ? IslandMode.Expanded
        : State.Phase is GamePhase.Idle ? IslandMode.Lip
        : IslandMode.Compact;

    /// The "Quit playlist?" capsule is showing.
    public bool QuitArmed => IslandRules.ShowsQuit(State.Phase) && Quit.IsArmed;

    /// Label of the quit button: null where it is hidden, the question while armed.
    public string? QuitLabel =>
        !IslandRules.ShowsQuit(State.Phase) ? null
        : QuitArmed ? IslandHeader.QuitConfirmLabel
        : IslandHeader.QuitLabel;

    public IslandIndicator Indicator() => IslandIndicator.For(State, Now, SnippetStart, CelebrationStart, SetEndedAt);

    /// <paramref name="history"/>: the whole play history, oldest first (the spoiler rule is
    /// applied here). <paramref name="zone"/>: day groups, local time by default.
    /// <paramref name="artworkUrl"/>: the current track's cover (answer screens only).
    public IslandScreen Screen(AppSettings settings, string playerName, bool playerPlaysFullTrack,
        IReadOnlyList<HistoryEntry>? history = null, TimeZoneInfo? zone = null, Uri? artworkUrl = null) =>
        ShowingSettings ? IslandScreens.Settings(settings, playerName)
        : ShowingHistory ? HistoryScreen(history ?? [], zone)
        : IslandScreens.Build(State, TitleText, ArtistText, UrlMessage, playerPlaysFullTrack, artworkUrl);

    private (IReadOnlyList<HistoryEntry> History, GameState State, bool Armed, long Minute, TimeZoneInfo? Zone, IslandScreen.History Screen)? _historyMemo;

    /// The History screen, rebuilt only when the history, the game state, the clear
    /// confirmation or the minute changes (the view redraws up to 30 times a second).
    private IslandScreen.History HistoryScreen(IReadOnlyList<HistoryEntry> history, TimeZoneInfo? zone)
    {
        var now = Now;
        var minute = now.ToUnixTimeSeconds() / 60;
        var armed = ClearHistoryArmed;
        if (_historyMemo is { } m && ReferenceEquals(m.History, history) && ReferenceEquals(m.State, State)
            && m.Armed == armed && m.Minute == minute && ReferenceEquals(m.Zone, zone))
            return m.Screen;
        var screen = HistoryRules.Screen(history, State, now, armed, zone);
        _historyMemo = (history, State, armed, minute, zone, screen);
        return screen;
    }

    public void RequestFocus(IslandField? field)
    {
        RequestedFocus = field;
        FocusToken++;
    }

    /// Call after every game state change. <paramref name="old"/> is null for the first state
    /// (launch): that one never fires confetti. Returns true when a celebration starts.
    public bool StateDidChange(GameState? old, GameState @new)
    {
        State = @new;
        var now = Now;
        if (!Equals(old?.Phase, @new.Phase))
        {
            UrlMessage = null;
            if (@new.Phase is GamePhase.PlayingSnippet) SnippetStart = now;
            if (@new.Phase is GamePhase.SetComplete or GamePhase.SetFailed && old is not null) SetEndedAt = now;
            var transition = IslandRules.FieldTransitionFor(old?.Phase, @new.Phase);
            switch (transition.Kind)
            {
                case FieldTransitionKind.ClearAndFocusTitle:
                    TitleText = "";
                    ArtistText = "";
                    RequestFocus(IslandField.Title);
                    break;
                case FieldTransitionKind.KeepAndFocus:
                case FieldTransitionKind.FocusUrl:
                    RequestFocus(transition.Field);
                    break;
            }
        }
        if (old is not null && @new.CelebrationCount > old.CelebrationCount)
        {
            unchecked { CelebrationSeed += 0x9E3779B97F4A7C15; }
            CelebrationStart = now;
            return true;
        }
        return false;
    }

    /// Handles a key the island owns. Returns false when the key should go to the text box.
    public bool HandleKey(IslandKey key)
    {
        var command = IslandRules.Command(key, State.Phase, FocusedField, ShowingSettings, State.Config, QuitArmed, ShowingHistory);
        if (command is null) return false;
        Perform(command);
        return true;
    }

    public void Perform(IslandCommand command)
    {
        switch (command)
        {
            case IslandCommand.SubmitGuess: SubmitGuess(); break;
            case IslandCommand.Load: Load(); break;
            case IslandCommand.Send s: Send(s.Action); break;
            case IslandCommand.Collapse:
                ShowingSettings = false;
                RequestFocus(null);
                Behavior.Collapse();
                break;
            case IslandCommand.CloseSettings: ShowingSettings = false; break;
            case IslandCommand.Focus f: RequestFocus(f.Field); break;
            case IslandCommand.Restart: Restart(); break;
            case IslandCommand.Quit: PressQuit(); break;
            case IslandCommand.DisarmQuit: Quit.Disarm(); break;
            case IslandCommand.ShowTab t: ShowTab(t.Tab); break;
        }
    }

    /// The header switch / Ctrl+1 / Ctrl+2. Closes settings; back on Play the keyboard returns
    /// to the field the phase shows. Leaving History disarms "Clear history".
    public void ShowTab(IslandTab tab)
    {
        ShowingSettings = false;
        if (tab == Tab) return;
        Tab = tab;
        ClearHistoryConfirmation.Disarm();
        if (tab == IslandTab.Play)
            RequestFocus(IslandRules.ShowsGuessFields(State.Phase) ? FocusedFieldOr(IslandField.Title)
                : IslandRules.ShowsUrlField(State.Phase) ? IslandField.Url : null);
        else
            RequestFocus(null);
    }

    private IslandField FocusedFieldOr(IslandField fallback) =>
        FocusedField is IslandField.Title or IslandField.Artist ? FocusedField.Value : fallback;

    /// "Clear history": the first press arms "Clear all history?", a second one within
    /// QuitConfirmation.Window clears. Returns true when this press cleared.
    public bool PressClearHistory()
    {
        if (!ShowingHistory) { ClearHistoryConfirmation.Disarm(); return false; }
        if (!ClearHistoryConfirmation.Press()) return false;
        ClearHistory();
        return true;
    }

    /// The quit button / Ctrl+N. First press arms "Quit playlist?"; a second one within
    /// QuitConfirmation.Window sends Reset. The link field comes back empty, and the phase change
    /// to Idle focuses it (FieldTransitionFor → FocusUrl).
    public void PressQuit()
    {
        if (!IslandRules.ShowsQuit(State.Phase)) { Quit.Disarm(); return; }
        if (!Quit.Press()) return;
        UrlText = "";
        UrlMessage = null;
        ShowingSettings = false;
        Send(new GameAction.Reset());
    }

    /// The ↺ button / Ctrl+Shift+R. In a guess phase the snippet replays at the same tier: the
    /// typed text and focus stay, and the progress ring / bar start over now (from PlayingSnippet
    /// the phase doesn't change, so StateDidChange won't restart them). In Correct / Revealed the
    /// song starts over. Ignored elsewhere, like the engine does.
    public void Restart()
    {
        if (!IslandRules.ShowsRestart(State.Phase)) return;
        if (IslandRules.ShowsGuessFields(State.Phase)) SnippetStart = Now;
        Send(new GameAction.Restart());
    }

    public void Load()
    {
        var text = UrlText.Trim();
        if (text.Length == 0)
        {
            UrlMessage = "Paste a Spotify playlist, album or artist link";
            return;
        }
        if (ParseSource(text) is not { } source)
        {
            UrlMessage = "Unsupported link";
            return;
        }
        UrlMessage = null;
        Send(new GameAction.Load(source));
    }

    public void SubmitGuess()
    {
        var title = TitleText.Trim();
        var artist = ArtistText.Trim();
        if (title.Length == 0) { RequestFocus(IslandField.Title); return; }
        if (artist.Length == 0) { RequestFocus(IslandField.Artist); return; }
        Send(new GameAction.Submit(new Guess(title, artist)));
    }

    /// Snapshot / test hook: pretend the snippet or a celebration started at a given moment.
    public void SetClocks(DateTimeOffset? snippetStart, DateTimeOffset? celebrationStart = null, DateTimeOffset? setEndedAt = null)
    {
        SnippetStart = snippetStart;
        CelebrationStart = celebrationStart;
        SetEndedAt = setEndedAt;
    }
}
