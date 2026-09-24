using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using Notchle.Core;
using Notchle.Core.App.Spotify;

namespace Notchle.Windows.Playback;

/// Full tracks through Spotify Connect (Spotify Web API → the Spotify desktop app on this PC).
/// Needs Spotify Premium, the Spotify app running, and AppSettings.SpotifyClientId from Tren's
/// own Spotify developer app. All logic is SpotifyWebPlayer in Notchle.Core (tested against a
/// fake Spotify on the Mac); this wrapper only adds the DPAPI token store.
///
/// Spoiler warning: Spotify's own app shows the track in its window and in the Windows media
/// flyout. That's why previews are the default.
public sealed class SpotifyConnectPlayer(string? clientId, ISpotifyTokenStore tokens) : IPlayer
{
    private readonly SpotifyWebPlayer _inner = new(clientId, Http.Shared, tokens);

    public SpotifyConnectPlayer(string? clientId) : this(clientId, DpapiTokenStore.Default) { }

    public string DisplayName => _inner.DisplayName;
    public bool PlaysFullTrack => _inner.PlaysFullTrack;

    public Task PlaySnippetAsync(Track track, double start, double seconds, CancellationToken cancellationToken) =>
        _inner.PlaySnippetAsync(track, start, seconds, cancellationToken);

    public Task ContinuePlayingAsync(CancellationToken cancellationToken = default) =>
        _inner.ContinuePlayingAsync(cancellationToken);

    public Task RestartTrackAsync(Track track, CancellationToken cancellationToken = default) =>
        _inner.RestartTrackAsync(track, cancellationToken);

    public Task StopAsync() => _inner.StopAsync();

    /// "Connect Spotify…": browser sign-in (PKCE), tokens into the DPAPI store.
    public static SpotifyAuthorizer CreateAuthorizer() =>
        new(Http.Shared, DpapiTokenStore.Default, url =>
        {
            Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });
            return Task.CompletedTask;
        });
}

internal static class Http
{
    public static readonly HttpClient Shared = new() { Timeout = TimeSpan.FromSeconds(15) };
}

/// Spotify tokens in %APPDATA%\Notchle\spotify-tokens.bin, encrypted with DPAPI for the current
/// Windows user (ProtectedData, CurrentUser scope): other accounts, and copies of the file on
/// another machine, can't read them.
public sealed class DpapiTokenStore(string filePath) : ISpotifyTokenStore
{
    private static readonly byte[] Entropy = "Notchle.SpotifyTokens.v1"u8.ToArray();

    public static DpapiTokenStore Default { get; } = new(Path.Combine(Shell.AppPaths.DataDirectory, "spotify-tokens.bin"));

    public string FilePath { get; } = filePath;

    public SpotifyTokens? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var clear = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), Entropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<SpotifyTokens>(clear);
        }
        catch (Exception error) when (error is CryptographicException or JsonException or IOException)
        {
            Trace.TraceWarning($"Notchle: unreadable Spotify tokens ignored: {error.Message}");
            return null;
        }
    }

    public void Save(SpotifyTokens? tokens)
    {
        if (tokens is null)
        {
            File.Delete(FilePath);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var sealedBytes = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(tokens), Entropy, DataProtectionScope.CurrentUser);
        var temp = FilePath + ".tmp";
        File.WriteAllBytes(temp, sealedBytes);
        File.Move(temp, FilePath, overwrite: true);
    }
}

/// PlayerMode → player. Previews are the default and need nothing.
public static class PlayerFactory
{
    public static IPlayer Create(AppSettings settings) => settings.PlayerMode switch
    {
        PlayerMode.SpotifyConnect => new SpotifyConnectPlayer(settings.SpotifyClientId),
        _ => new PreviewPlayer(),
    };
}
