namespace Notchle.Core;

// Mirror of Sources/NotchleCore/Contracts/Ports.swift.

public interface ITrackSource
{
    Task<SourceListing> GetListingAsync(SourceRef source, CancellationToken cancellationToken = default);
}

public enum SourceErrorKind { InvalidUrl, NotFound, Network, ParseFailed, Empty }

public sealed class SourceException(SourceErrorKind kind, string? detail = null)
    : Exception(detail ?? kind.ToString())
{
    public SourceErrorKind Kind { get; } = kind;
}

/// Plays audio for the game. Contract every implementation must honour:
/// - PlaySnippetAsync starts the track at `start` seconds and completes once `seconds` of audio
///   have actually played (time by the player's own position, not a wall-clock sleep: both
///   macOS players were measured starting ~250–300 ms late) and playback is paused.
///   Cancellation must pause promptly and throw OperationCanceledException.
/// - ContinuePlayingAsync resumes the current track from where it is paused.
/// - RestartTrackAsync plays the track from 0:00 and keeps playing; a cancelled snippet's
///   late pause must not stop it.
/// - Nothing an implementation does may reveal the title or artist on screen.
public interface IPlayer
{
    string DisplayName { get; }
    /// False for the preview player: "keeps playing" ends after the ~30s clip.
    bool PlaysFullTrack { get; }

    Task PlaySnippetAsync(Track track, double start, double seconds, CancellationToken cancellationToken);
    Task ContinuePlayingAsync(CancellationToken cancellationToken = default);
    /// Plays the track from 0:00 and keeps playing (no pause). Must not reveal the title on screen.
    Task RestartTrackAsync(Track track, CancellationToken cancellationToken = default);
    Task StopAsync();
}

public enum PlayerErrorKind { Unavailable, NotAuthorized, NoPreview, Failed }

public sealed class PlayerException(PlayerErrorKind kind, string? detail = null)
    : Exception(detail ?? kind.ToString())
{
    public PlayerErrorKind Kind { get; } = kind;
}

public enum PlayerMode
{
    /// ~30s preview clips; works for everyone, no login.
    Preview,
    /// Full tracks through Spotify Connect (Web API; Premium + a Spotify developer app).
    SpotifyConnect,
}

public sealed record AppSettings
{
    public PlayerMode PlayerMode { get; init; } = PlayerMode.Preview;
    public SourceRef? LastSource { get; init; }
    public GameConfig Config { get; init; } = GameConfig.Default;
    /// Client id of Tren's own Spotify developer app, only needed for SpotifyConnect.
    public string? SpotifyClientId { get; init; }
}

public sealed record Progress
{
    public AppSettings Settings { get; init; } = new();
    public IReadOnlySet<string> ClearedTrackIds { get; init; } = new HashSet<string>();
}
