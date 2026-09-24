namespace Notchle.Core.App;

/// Human messages for errors shown in the island. Same texts as AppCoordinator.describe(_:) in
/// the Swift app, except NotAuthorized: on macOS that is the Automation permission, on Windows
/// it only comes from Spotify Connect (no or expired sign-in).
public static class AppMessages
{
    public static string Describe(Exception error) => error switch
    {
        PlayerException { Kind: PlayerErrorKind.NotAuthorized } =>
            "Notchle isn't connected to Spotify. Use Connect Spotify… in the tray menu, or switch to 30-second previews in settings.",
        PlayerException { Kind: PlayerErrorKind.Unavailable } e =>
            $"{e.Message}. Switch to 30-second previews in settings.",
        PlayerException { Kind: PlayerErrorKind.NoPreview } =>
            "This song has no preview clip. Skip it, or switch to Spotify Connect in settings.",
        PlayerException { Kind: PlayerErrorKind.Failed } e => e.Message,
        SourceException { Kind: SourceErrorKind.InvalidUrl } =>
            "That doesn't look like a Spotify playlist, album or artist link.",
        SourceException { Kind: SourceErrorKind.NotFound } =>
            "Spotify couldn't find that. Is it public?",
        SourceException { Kind: SourceErrorKind.Network } e => $"Couldn't reach Spotify: {e.Message}",
        SourceException { Kind: SourceErrorKind.ParseFailed } e => $"Couldn't read that Spotify page: {e.Message}",
        SourceException { Kind: SourceErrorKind.Empty } => "No playable songs on that page.",
        _ => error.Message,
    };
}
