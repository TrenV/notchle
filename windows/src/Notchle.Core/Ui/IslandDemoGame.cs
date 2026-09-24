namespace Notchle.Core.Ui;

/// Minimal stand-in for GameEngine so every island screen can be driven without Spotify or
/// audio (port of DemoGame in Sources/NotchleMac/UI/UIDemo.swift). The platform layer supplies
/// the state storage and a scheduler (a DispatcherTimer on Windows, a manual queue in tests).
public sealed class IslandDemoGame
{
    private readonly Func<GameState> _get;
    private readonly Action<GameState> _set;
    private readonly Action<TimeSpan, Action> _schedule;
    private int _snippetToken;

    public IslandDemoGame(Func<GameState> get, Action<GameState> set, Action<TimeSpan, Action> schedule)
    {
        _get = get;
        _set = set;
        _schedule = schedule;
    }

    public Action<string> Log { get; set; } = _ => { };

    public static IReadOnlyList<Track> Tracks { get; } = new (string Title, string[] Artists)[]
    {
        ("Paper Lanterns", ["The Midnight Kites"]), ("Glass Harbour", ["Nova Reyes"]),
        ("Slow Satellites", ["Juniper & The Owls"]), ("Coastline Radio", ["Mara Linde"]),
        ("Velvet Static", ["Odd Weather"]), ("Northbound", ["Tove Ahlberg", "Kasper Ruud"]),
        ("Lemon Skies", ["Sunday Club"]), ("Afterglow Avenue", ["Neon Tapes"]),
        ("Hollow Moon", ["Iris Vale"]), ("Carousel", ["The Paper Boats"]),
        ("Fever Dream Summer", ["Lola Park"]), ("Undertow", ["Blue Harbor"]),
        ("Monochrome", ["Elliot Stray"]), ("Wildflower Tape", ["June Arcade"]),
        ("Neon Rain", ["Kyoto Drive"]), ("Silver Lining", ["Amber Fields"]),
        ("Gravity Games", ["Otto Frame"]), ("Midnight Ferry", ["Sea of Lamps"]),
        ("Golden Hour Ghosts", ["Wren & Wilder"]), ("Last Train Home", ["The Quiet Hours"]),
    }.Select((t, i) => new Track($"demo{i}", $"spotify:track:demo{i}", t.Title, t.Artists, 200_000, null)).ToList();

    public static SourceRef DemoSource { get; } = new(SourceKind.Playlist, "demo");
    public static SourceRef ErrorSource { get; } = new(SourceKind.Playlist, "error");

    /// The demo's link parser: anything mentioning "spotify" or "demo"; "error" shows the error screen.
    public static SourceRef? Parse(string text)
    {
        var t = text.Trim().ToLowerInvariant();
        if (t == "error") return ErrorSource;
        return t.Contains("spotify") || t.Contains("demo") ? DemoSource : null;
    }

    /// A state in the middle of the demo set, for snapshots and tests.
    public static GameState SampleState(GamePhase phase, int index = 6)
    {
        var s = new GameState
        {
            Config = GameConfig.Default,
            Listing = new SourceListing(DemoSource, "Notchle Demo Mix", Tracks),
            CurrentSet = Tracks,
            Index = index,
            SetNumber = 1,
            Phase = phase,
        };
        return phase switch
        {
            GamePhase.Idle => s with { Listing = null, CurrentSet = Array.Empty<Track>(), Index = 0 },
            GamePhase.Loading or GamePhase.Exhausted => s with { CurrentSet = Array.Empty<Track>(), Index = 0 },
            GamePhase.SetComplete or GamePhase.SetFailed => s with { Index = Tracks.Count - 1 },
            _ => s,
        };
    }

    /// A set-end state (SetComplete / SetFailed) whose listing has <paramref name="newAvailable"/>
    /// tracks beyond the set, so the "N new songs" choice shows; failed sets miss every third track.
    public static GameState SetEndState(bool complete, int newAvailable)
    {
        var results = Enumerable.Range(0, Tracks.Count)
            .Select(i => complete || i % 3 != 0 ? (TrackOutcome)new TrackOutcome.Correct(0) : new TrackOutcome.Missed())
            .ToList();
        var correct = results.Count(r => r is TrackOutcome.Correct);
        var extra = Enumerable.Range(0, newAvailable)
            .Select(i => new Track($"demo-new{i}", $"spotify:track:demo-new{i}", $"New song {i + 1}", ["Demo Artist"], 200_000, null));
        var s = SampleState(complete ? new GamePhase.SetComplete(correct) : new GamePhase.SetFailed(correct));
        return s with
        {
            Listing = s.Listing! with { Tracks = [.. Tracks, .. extra] },
            Results = results,
            ClearedTrackIds = Tracks.Where((_, i) => results[i] is TrackOutcome.Correct).Select(t => t.Id).ToHashSet(),
        };
    }

    public Track? Current => _get().CurrentTrack;

    public void SetPhase(GamePhase phase)
    {
        _set(_get() with { Phase = phase });
        Log($"phase → {phase}");
    }

    public void ForceSetEnd(bool complete)
    {
        var s = _get();
        var results = Enumerable.Range(0, s.CurrentSet.Count)
            .Select(i => complete || i % 3 != 0 ? (TrackOutcome)new TrackOutcome.Correct(0) : new TrackOutcome.Missed())
            .ToList();
        s = s with { Results = results, Index = Math.Max(0, s.CurrentSet.Count - 1) };
        _set(s);
        SetPhase(complete ? new GamePhase.SetComplete(s.CorrectCount) : new GamePhase.SetFailed(s.CorrectCount));
    }

    public void Send(GameAction action)
    {
        Log($"send {action}");
        var s = _get();
        switch (action, s.Phase)
        {
            case (GameAction.Load load, _):
                SetPhase(new GamePhase.Loading());
                _schedule(TimeSpan.FromSeconds(1), () =>
                {
                    if (load.Ref.Id == "error")
                    {
                        SetPhase(new GamePhase.Error("Could not load that link: the page had no tracks."));
                        return;
                    }
                    _set(_get() with
                    {
                        Listing = new SourceListing(load.Ref, "Notchle Demo Mix", Tracks),
                        CurrentSet = Tracks, Index = 0, Results = Array.Empty<TrackOutcome>(), SetNumber = 1,
                    });
                    PlaySnippet(0);
                });
                break;
            case (GameAction.Submit submit, GamePhase.PlayingSnippet or GamePhase.Guessing):
            {
                var tier = s.Phase is GamePhase.PlayingSnippet p ? p.TierIndex : ((GamePhase.Guessing)s.Phase).TierIndex;
                if (s.CurrentTrack is not { } track) return;
                var titleOk = string.Equals(submit.Guess.Title.Trim(), track.Title, StringComparison.OrdinalIgnoreCase);
                // Rule: every credited artist, any order.
                var typed = submit.Guess.Artist.ToLowerInvariant();
                var artistOk = track.Artists.All(a => typed.Contains(a.ToLowerInvariant()));
                var verdict = new Verdict(titleOk, artistOk);
                _snippetToken++;
                if (verdict.IsCorrect)
                    s = s with
                    {
                        Results = [.. s.Results, new TrackOutcome.Correct(tier)],
                        CelebrationCount = s.CelebrationCount + 1,
                        Phase = new GamePhase.Correct(tier),
                    };
                else if (tier + 1 < s.Config.Tiers.Count)
                    s = s with { Phase = new GamePhase.Wrong(tier, verdict) };
                else
                    s = s with { Results = [.. s.Results, new TrackOutcome.Missed()], Phase = new GamePhase.Revealed(verdict) };
                _set(s);
                Log($"phase → {s.Phase}");
                break;
            }
            case (GameAction.Retry, GamePhase.Wrong w):
                PlaySnippet(w.TierIndex + 1);
                break;
            case (GameAction.Skip, GamePhase.PlayingSnippet or GamePhase.Guessing):
            {
                var tier = IslandRules.GuessTier(s.Phase)!.Value;
                if (tier + 1 < s.Config.Tiers.Count) { PlaySnippet(tier + 1); break; }
                _snippetToken++;
                _set(s with { Results = [.. s.Results, new TrackOutcome.Missed()] });
                SetPhase(new GamePhase.Revealed(null));
                break;
            }
            case (GameAction.Restart, GamePhase.PlayingSnippet p):
                PlaySnippet(p.TierIndex);
                break;
            case (GameAction.Restart, GamePhase.Guessing g):
                PlaySnippet(g.TierIndex);
                break;
            case (GameAction.Restart, GamePhase.Correct or GamePhase.Revealed):
                Log("song restarts from 0:00"); // no audio in the demo
                break;
            case (GameAction.GiveUp, GamePhase.PlayingSnippet or GamePhase.Guessing or GamePhase.Wrong):
                _snippetToken++;
                _set(s with { Results = [.. s.Results, new TrackOutcome.Missed()] });
                SetPhase(new GamePhase.Revealed(null));
                break;
            case (GameAction.Next, GamePhase.Correct or GamePhase.Revealed or GamePhase.Error):
                if (s.Phase is GamePhase.Error && s.CurrentSet.Count == 0) { SetPhase(new GamePhase.Idle()); return; }
                if (s.Phase is GamePhase.Error) s = s with { Results = [.. s.Results, new TrackOutcome.Missed()] };
                s = s with { Index = s.Index + 1 };
                _set(s);
                if (s.Index >= s.CurrentSet.Count)
                    SetPhase(s.CorrectCount == s.CurrentSet.Count
                        ? new GamePhase.SetComplete(s.CorrectCount) : new GamePhase.SetFailed(s.CorrectCount));
                else
                    PlaySnippet(0);
                break;
            case (GameAction.NextSet, GamePhase.SetComplete or GamePhase.SetFailed):
                Send(new GameAction.StartSet(SetChoice.AllNew));
                break;
            case (GameAction.ReplaySet, GamePhase.SetComplete or GamePhase.SetFailed):
                Send(new GameAction.StartSet(SetChoice.Replay));
                break;
            // The demo listing is one set, so there is never anything new: AllNew is Exhausted and
            // KeepMisses keeps only the misses.
            case (GameAction.StartSet { Choice: SetChoice.AllNew }, GamePhase.SetComplete or GamePhase.SetFailed):
                SetPhase(new GamePhase.Exhausted());
                break;
            case (GameAction.StartSet start, GamePhase.SetComplete or GamePhase.SetFailed):
            {
                var next = start.Choice == SetChoice.Replay
                    ? s.CurrentSet.Reverse().ToList()
                    : s.CurrentSet.Where((_, i) => i >= s.Results.Count || s.Results[i] is not TrackOutcome.Correct).ToList();
                if (next.Count == 0) { SetPhase(new GamePhase.Exhausted()); break; }
                _set(s with { Index = 0, Results = Array.Empty<TrackOutcome>(), CurrentSet = next });
                PlaySnippet(0);
                break;
            }
            case (GameAction.Reset, _):
                _snippetToken++;
                _set(s with
                {
                    Phase = new GamePhase.Idle(), Listing = null, CurrentSet = Array.Empty<Track>(), Index = 0,
                    Results = Array.Empty<TrackOutcome>(),
                });
                Log("phase → idle");
                break;
            default:
                Log($"ignored {action} in {s.Phase}");
                break;
        }
    }

    private void PlaySnippet(int tier)
    {
        var token = ++_snippetToken;
        SetPhase(new GamePhase.PlayingSnippet(tier));
        var seconds = IslandRules.Seconds(tier, _get().Config);
        _schedule(TimeSpan.FromSeconds(seconds), () =>
        {
            if (token != _snippetToken || _get().Phase is not GamePhase.PlayingSnippet p || p.TierIndex != tier) return;
            SetPhase(new GamePhase.Guessing(tier));
        });
    }
}
