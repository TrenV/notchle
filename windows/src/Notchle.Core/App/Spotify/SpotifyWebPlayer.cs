using System.Collections.Concurrent;
using System.Net;
using Notchle.Core.App.Playback;

namespace Notchle.Core.App.Spotify;

/// Full tracks through Spotify Connect: the Web API tells the Spotify desktop app on this PC what
/// to play. Everything platform-neutral lives here (and is tested against a fake Spotify); the
/// Windows project only adds the DPAPI token store.
///
/// Note: Spotify's own app shows the track in its window and in the Windows media flyout, which
/// spoils the answer. That's why the preview player is the default.
public sealed class SpotifyWebPlayer : IPlayer
{
    private readonly string? _clientId;
    private readonly HttpClient _http;
    private readonly ISpotifyTokenStore _tokens;
    private readonly string _machineName;
    private readonly IPlaybackClock _clock;
    private readonly SnippetTiming _timing;
    private readonly Func<DateTimeOffset> _now;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private string? _deviceId;
    /// Track uri -> album uri, so each track is looked up once.
    private readonly ConcurrentDictionary<string, string> _albumCache = new();
    /// Bumped by every public operation, so a cancelled snippet's late cleanup can't pause a
    /// newer snippet or the song ContinuePlayingAsync just resumed.
    private int _generation;

    public SpotifyWebPlayer(
        string? clientId,
        HttpClient http,
        ISpotifyTokenStore tokens,
        string? machineName = null,
        IPlaybackClock? clock = null,
        SnippetTiming? timing = null,
        Func<DateTimeOffset>? now = null)
    {
        _clientId = clientId;
        _http = http;
        _tokens = tokens;
        _machineName = machineName ?? Environment.MachineName;
        _clock = clock ?? SystemPlaybackClock.Instance;
        _timing = timing ?? SnippetTiming.SpotifyConnect;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public string DisplayName => "Spotify Connect (full tracks)";
    public bool PlaysFullTrack => true;

    public async Task PlaySnippetAsync(Track track, double start, double seconds, CancellationToken cancellationToken)
    {
        var myGeneration = Interlocked.Increment(ref _generation);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var device = await LocalDeviceAsync(cancellationToken).ConfigureAwait(false);
            _deviceId = device.Id!;
            if (!device.IsActive)
                await SendAsync(() => SpotifyPlayerApi.Transfer(device.Id!), cancellationToken).ConfigureAwait(false);
            var startAt = SnippetTimer.ClampedStart(start, seconds, track.DurationMs > 0 ? track.DurationMs / 1000.0 : null);
            var startMs = (int)Math.Round(startAt * 1000);
            await PlayAsync(device.Id!, track.Uri, startMs, cancellationToken).ConfigureAwait(false);

            Task<PlaybackSample> Sample(CancellationToken ct) => SampleAsync(track.Uri, ct);
            await SnippetTimer.WaitUntilAudioAdvancesAsync(Sample, startAt, _timing, _clock, "Spotify", cancellationToken,
                reseek: ct => SendAsync(() => SpotifyPlayerApi.Seek(startMs, device.Id), ct)).ConfigureAwait(false);
            await SnippetTimer.WaitUntilPlayedAsync(Sample, startAt + Math.Max(0, seconds), _timing, _clock, "Spotify",
                cancellationToken).ConfigureAwait(false);
            await PauseIfStillAsync(myGeneration).ConfigureAwait(false);
        }
        catch
        {
            // Cancellation (a guess mid-snippet) or a failure: never leave Spotify playing
            // something the game didn't ask for.
            await PauseIfStillAsync(myGeneration).ConfigureAwait(false);
            throw;
        }
    }

    public async Task ContinuePlayingAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _generation);
        await SendAsync(() => SpotifyPlayerApi.Resume(_deviceId), cancellationToken).ConfigureAwait(false);
    }

    /// The same device lookup / transfer as a snippet, then play from 0 and leave it playing.
    /// Bumping the generation first means a cancelled snippet's late pause won't stop it.
    public async Task RestartTrackAsync(Track track, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _generation);
        cancellationToken.ThrowIfCancellationRequested();
        var device = await LocalDeviceAsync(cancellationToken).ConfigureAwait(false);
        _deviceId = device.Id!;
        if (!device.IsActive)
            await SendAsync(() => SpotifyPlayerApi.Transfer(device.Id!), cancellationToken).ConfigureAwait(false);
        await PlayAsync(device.Id!, track.Uri, 0, cancellationToken).ConfigureAwait(false);
    }

    public async Task StopAsync()
    {
        Interlocked.Increment(ref _generation);
        await PauseQuietlyAsync().ConfigureAwait(false);
    }

    // MARK: - Internals

    /// Plays the track inside its album (see `SpotifyPlayerApi.Play`). If the album lookup fails
    /// it falls back to a bare `uris` play.
    private async Task PlayAsync(string deviceId, string trackUri, int positionMs, CancellationToken ct)
    {
        string? context;
        try { context = await AlbumUriAsync(trackUri, ct).ConfigureAwait(false); }
        catch (PlayerException) { context = null; }
        await SendAsync(() => SpotifyPlayerApi.Play(deviceId, trackUri, positionMs, context), ct).ConfigureAwait(false);
    }

    public async Task<string?> AlbumUriAsync(string trackUri, CancellationToken ct = default)
    {
        if (_albumCache.TryGetValue(trackUri, out var cached)) return cached;
        const string prefix = "spotify:track:";
        if (!trackUri.StartsWith(prefix, StringComparison.Ordinal) || trackUri.Length == prefix.Length) return null;
        var id = trackUri[prefix.Length..];
        var album = SpotifyPlayerApi.ParseAlbumUri(await SendAsync(() => SpotifyPlayerApi.Track(id), ct).ConfigureAwait(false));
        if (album is not null) _albumCache[trackUri] = album;
        return album;
    }

    private async Task<SpotifyDevice> LocalDeviceAsync(CancellationToken ct)
    {
        var body = await SendAsync(SpotifyPlayerApi.Devices, ct).ConfigureAwait(false);
        return SpotifyPlayerApi.PickLocalDevice(SpotifyPlayerApi.ParseDevices(body), _machineName)
               ?? throw new PlayerException(PlayerErrorKind.Unavailable,
                   "Spotify isn't open on this PC. Start the Spotify app and try again");
    }

    private async Task<PlaybackSample> SampleAsync(string trackUri, CancellationToken ct)
    {
        var body = await SendAsync(SpotifyPlayerApi.PlaybackState, ct).ConfigureAwait(false);
        return SpotifyPlayerApi.ParsePlayback(body)?.ToSample(trackUri) ?? new PlaybackSample(0, IsPlaying: false);
    }

    private async Task PauseIfStillAsync(int myGeneration)
    {
        if (Volatile.Read(ref _generation) != myGeneration) return;
        await PauseQuietlyAsync().ConfigureAwait(false);
    }

    /// Best effort, never with the (possibly cancelled) snippet token, and bounded so a dead
    /// network can't hold up the playback queue.
    private async Task PauseQuietlyAsync()
    {
        if (_deviceId is null) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try { await SendAsync(() => SpotifyPlayerApi.Pause(_deviceId), timeout.Token).ConfigureAwait(false); }
        catch (Exception) { /* already paused (403), no device (404), offline */ }
    }

    /// Sends an authorised request; on 401 refreshes the token once and retries.
    private async Task<string> SendAsync(Func<HttpRequestMessage> build, CancellationToken ct)
    {
        var token = await AccessTokenAsync(forceRefresh: false, ct).ConfigureAwait(false);
        for (var attempt = 0; ; attempt++)
        {
            using var request = build();
            SpotifyAccounts.Authorize(request, token);
            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException error)
            {
                throw new PlayerException(PlayerErrorKind.Failed, $"Couldn't reach Spotify: {error.Message}");
            }
            using (response)
            {
                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (response.IsSuccessStatusCode) return body;
                if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
                {
                    token = await AccessTokenAsync(forceRefresh: true, ct).ConfigureAwait(false);
                    continue;
                }
                throw SpotifyPlayerApi.MapError(response.StatusCode, body, response.Headers.RetryAfter?.Delta);
            }
        }
    }

    private async Task<string> AccessTokenAsync(bool forceRefresh, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_clientId))
            throw new PlayerException(PlayerErrorKind.Unavailable, "Spotify Connect needs your Spotify client id (see the README)");
        await _refreshGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var tokens = _tokens.Load() ?? throw new PlayerException(PlayerErrorKind.NotAuthorized, "Not signed in to Spotify");
            if (!forceRefresh && tokens.IsFresh(_now())) return tokens.AccessToken;
            using var request = SpotifyAccounts.RefreshRequest(_clientId, tokens.RefreshToken);
            HttpResponseMessage response;
            try { response = await _http.SendAsync(request, ct).ConfigureAwait(false); }
            catch (HttpRequestException error)
            {
                throw new PlayerException(PlayerErrorKind.Failed, $"Couldn't reach Spotify: {error.Message}");
            }
            using var _ = response;
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var error = SpotifyAccounts.TokenError((int)response.StatusCode, body);
                if (error.Kind == PlayerErrorKind.NotAuthorized) _tokens.Save(null);
                throw error;
            }
            var refreshed = SpotifyAccounts.ParseTokenResponse(body, _now(), tokens.RefreshToken);
            _tokens.Save(refreshed);
            return refreshed.AccessToken;
        }
        finally
        {
            _refreshGate.Release();
        }
    }
}
