using Notchle.Core;
using Notchle.Core.Ui;

namespace Notchle.Core.Tests;

/// Manually advanced clock for the island rules.
public sealed class UiManualClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
    public void Advance(double seconds) => Now += TimeSpan.FromSeconds(seconds);
}

public static class UiFixtures
{
    public static readonly Track Secret = new("t1", "spotify:track:t1", "Zanzibar Nights",
        ["Quill Ostrander", "Mabel Fitch"], 1, null);

    public static readonly Track Other = new("t2", "spotify:track:t2", "Other Song", ["Solo"], 1, null);

    public static GameState State(GamePhase phase, params Track[] tracks)
    {
        tracks = tracks.Length == 0 ? [Secret] : tracks;
        return new GameState
        {
            Config = GameConfig.Default,
            Listing = new SourceListing(new SourceRef(SourceKind.Playlist, "p"), "List", tracks),
            CurrentSet = tracks,
            Phase = phase,
        };
    }

    public static readonly GamePhase[] AllPhases =
    [
        new GamePhase.Idle(), new GamePhase.Loading(), new GamePhase.PlayingSnippet(0), new GamePhase.Guessing(1),
        new GamePhase.Wrong(0, new Verdict(true, false)), new GamePhase.Correct(0), new GamePhase.Revealed(null),
        new GamePhase.SetComplete(20), new GamePhase.SetFailed(3), new GamePhase.Exhausted(), new GamePhase.Error("x"),
    ];
}
