using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Web;
using Notchle.Core;
using Notchle.Core.App.Playback;
using Notchle.Core.App.Spotify;

namespace Notchle.Core.Tests;

internal sealed class AppMemoryTokenStore(SpotifyTokens? tokens) : ISpotifyTokenStore
{
    public SpotifyTokens? Tokens { get; private set; } = tokens;
    public SpotifyTokens? Load() => Tokens;
    public void Save(SpotifyTokens? tokens) => Tokens = tokens;
}

/// A tiny in-memory Spotify: devices, playback state driven by the fake clock (position starts
/// moving `latency` after play), pause/resume, the token endpoint. Records every request.
internal sealed class AppFakeSpotify(AppFakeClock clock, double latency = 0.3) : HttpMessageHandler
{
    public readonly List<string> Requests = new();
    public readonly List<string?> Bodies = new();
    public readonly List<string?> AuthHeaders = new();
    public string DevicesJson = """{"devices":[{"id":"phone","name":"Pixel","type":"Smartphone","is_active":true,"is_restricted":false},{"id":"pc","name":"GAMING-PC","type":"Computer","is_active":false,"is_restricted":false}]}""";
    public string ValidAccessToken = "access-1";
    /// Returns a response to short-circuit a request (by "METHOD path").
    public Func<string, HttpResponseMessage?>? Override;

    private bool _playing;
    private string? _uri;
    private int _startMs;
    private TimeSpan _playedAt;
    private double _pausedPosition;

    private double Position => !_playing
        ? _pausedPosition
        : _startMs / 1000.0 + Math.Max(0, (clock.Now - _playedAt).TotalSeconds - latency);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var key = $"{request.Method} {request.RequestUri!.PathAndQuery}";
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add(key);
        Bodies.Add(body);
        AuthHeaders.Add(request.Headers.Authorization?.ToString());
        if (Override?.Invoke(key) is { } overridden) return overridden;

        if (request.RequestUri.Host == "accounts.spotify.com")
            return Json(HttpStatusCode.OK, """{"access_token":"access-2","token_type":"Bearer","expires_in":3600}""");
        if (request.Headers.Authorization?.Parameter != ValidAccessToken)
            return Json(HttpStatusCode.Unauthorized, """{"error":{"status":401,"message":"The access token expired"}}""");

        var path = request.RequestUri.AbsolutePath;
        switch (request.Method.Method, path)
        {
            case ("GET", "/v1/me/player/devices"):
                return Json(HttpStatusCode.OK, DevicesJson);
            case ("PUT", "/v1/me/player"):
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            case ("PUT", "/v1/me/player/play"):
                if (!string.IsNullOrEmpty(body))
                {
                    var json = JsonNode.Parse(body)!;
                    _uri = json["uris"]![0]!.GetValue<string>();
                    _startMs = json["position_ms"]!.GetValue<int>();
                }
                else
                {
                    _startMs = (int)(_pausedPosition * 1000);
                }
                _playing = true;
                _playedAt = clock.Now;
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            case ("PUT", "/v1/me/player/pause"):
                _pausedPosition = Position;
                _playing = false;
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            case ("GET", "/v1/me/player"):
                if (_uri is null) return new HttpResponseMessage(HttpStatusCode.NoContent);
                var progress = (int)(Position * 1000);
                return Json(HttpStatusCode.OK,
                    new JsonObject
                    {
                        ["device"] = new JsonObject { ["id"] = "pc" },
                        ["is_playing"] = _playing,
                        ["progress_ms"] = progress,
                        ["currently_playing_type"] = "track",
                        ["item"] = new JsonObject { ["uri"] = _uri },
                    }.ToJsonString());
        }
        return Json(HttpStatusCode.NotFound, """{"error":{"status":404,"message":"no route"}}""");
    }

    public double PausedPosition => _pausedPosition;
    public bool IsPlaying => _playing;

    public static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

public class AppSpotifyPkceTests
{
    /// RFC 7636, Appendix B.
    [Fact]
    public void MatchesTheRfc7636TestVector()
    {
        byte[] octets = [116, 24, 223, 180, 151, 153, 224, 37, 79, 250, 96, 125, 216, 173, 187, 186,
            22, 212, 37, 77, 105, 214, 191, 240, 91, 88, 5, 88, 83, 132, 141, 121];
        Assert.Equal("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk", Pkce.Base64Url(octets));
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", Pkce.Challenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));
    }

    [Fact]
    public void VerifiersAreLongRandomAndUnreserved()
    {
        var a = Pkce.CreateVerifier();
        Assert.InRange(a.Length, 43, 128);
        Assert.Matches("^[A-Za-z0-9._~-]+$", a);
        Assert.NotEqual(a, Pkce.CreateVerifier());
    }
}

public class AppSpotifyAccountsTests
{
    [Fact]
    public void AuthorizeUrlCarriesPkceScopesAndLoopbackRedirect()
    {
        var redirect = SpotifyAccounts.RedirectUri(51234);
        Assert.Equal("http://127.0.0.1:51234/callback", redirect.ToString());

        var url = SpotifyAccounts.AuthorizeUrl("cid", redirect, "chal", "st");
        var q = HttpUtility.ParseQueryString(url.Query);
        Assert.Equal("accounts.spotify.com", url.Host);
        Assert.Equal("/authorize", url.AbsolutePath);
        Assert.Equal("code", q["response_type"]);
        Assert.Equal("cid", q["client_id"]);
        Assert.Equal("user-modify-playback-state user-read-playback-state", q["scope"]);
        Assert.Equal("http://127.0.0.1:51234/callback", q["redirect_uri"]);
        Assert.Equal(("S256", "chal", "st"), (q["code_challenge_method"], q["code_challenge"], q["state"]));
    }

    [Fact]
    public async Task TokenAndRefreshRequestsAreFormPosts()
    {
        var token = SpotifyAccounts.TokenRequest("cid", "the code", SpotifyAccounts.RedirectUri(1), "ver");
        Assert.Equal(HttpMethod.Post, token.Method);
        Assert.Equal("https://accounts.spotify.com/api/token", token.RequestUri!.ToString());
        Assert.Equal("grant_type=authorization_code&code=the+code&redirect_uri=http%3A%2F%2F127.0.0.1%3A1%2Fcallback&client_id=cid&code_verifier=ver",
            await token.Content!.ReadAsStringAsync());

        var refresh = SpotifyAccounts.RefreshRequest("cid", "r1");
        Assert.Equal("grant_type=refresh_token&refresh_token=r1&client_id=cid", await refresh.Content!.ReadAsStringAsync());
    }

    [Fact]
    public void ParsesTokensAndKeepsTheOldRefreshToken()
    {
        var now = new DateTimeOffset(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
        var t = SpotifyAccounts.ParseTokenResponse("""{"access_token":"a","refresh_token":"r","expires_in":3600,"scope":"s"}""", now);
        Assert.Equal(new SpotifyTokens("a", "r", now.AddHours(1), "s"), t);
        Assert.True(t.IsFresh(now.AddMinutes(58)));
        Assert.False(t.IsFresh(now.AddMinutes(59.5)));

        Assert.Equal("old", SpotifyAccounts.ParseTokenResponse("""{"access_token":"b","expires_in":10}""", now, "old").RefreshToken);
        Assert.Throws<PlayerException>(() => SpotifyAccounts.ParseTokenResponse("not json", now));
        Assert.Equal(PlayerErrorKind.NotAuthorized,
            SpotifyAccounts.TokenError(400, """{"error":"invalid_grant","error_description":"Refresh token revoked"}""").Kind);
    }

    [Fact]
    public void CallbackChecksStateAndErrors()
    {
        Assert.Equal("abc", SpotifyAccounts.ParseCallback("/callback?code=abc&state=s1", "s1"));
        Assert.Equal(PlayerErrorKind.NotAuthorized,
            Assert.Throws<PlayerException>(() => SpotifyAccounts.ParseCallback("/callback?code=abc&state=evil", "s1")).Kind);
        Assert.Contains("access_denied",
            Assert.Throws<PlayerException>(() => SpotifyAccounts.ParseCallback("/callback?error=access_denied&state=s1", "s1")).Message);
        Assert.Throws<PlayerException>(() => SpotifyAccounts.ParseCallback("/callback?state=s1", "s1"));
    }
}

public class AppSpotifyPlayerApiTests
{
    [Fact]
    public async Task PlayTargetsTheDeviceWithUriAndPosition()
    {
        var play = SpotifyPlayerApi.Play("dev 1", "spotify:track:t1", 1500);
        Assert.Equal(HttpMethod.Put, play.Method);
        Assert.Equal("https://api.spotify.com/v1/me/player/play?device_id=dev%201", play.RequestUri!.AbsoluteUri);
        Assert.Equal("""{"uris":["spotify:track:t1"],"position_ms":1500}""", await play.Content!.ReadAsStringAsync());

        Assert.Equal("""{"device_ids":["d"],"play":false}""", await SpotifyPlayerApi.Transfer("d").Content!.ReadAsStringAsync());
        Assert.Equal("/v1/me/player/pause?device_id=d", SpotifyPlayerApi.Pause("d").RequestUri!.PathAndQuery);
        Assert.Equal("/v1/me/player/seek?position_ms=2000&device_id=d", SpotifyPlayerApi.Seek(2000, "d").RequestUri!.PathAndQuery);
        Assert.Equal("", await SpotifyPlayerApi.Resume(null).Content!.ReadAsStringAsync());
    }

    [Fact]
    public void PicksThisPcsSpotifyApp()
    {
        var devices = SpotifyPlayerApi.ParseDevices("""
            {"devices":[
              {"id":"tv","name":"Living room","type":"TV","is_active":true,"is_restricted":false},
              {"id":"laptop","name":"LAPTOP","type":"Computer","is_active":true,"is_restricted":false},
              {"id":"me","name":"Gaming-PC","type":"Computer","is_active":false,"is_restricted":false},
              {"id":"locked","name":"GAMING-PC","type":"Computer","is_active":false,"is_restricted":true}]}
            """);
        Assert.Equal("me", SpotifyPlayerApi.PickLocalDevice(devices, "GAMING-PC")!.Id);
        Assert.Equal("laptop", SpotifyPlayerApi.PickLocalDevice(devices, "OTHER")!.Id);
        Assert.Null(SpotifyPlayerApi.PickLocalDevice(devices.Where(d => d.Type == "TV").ToList(), "GAMING-PC"));
    }

    [Fact]
    public void ParsesPlaybackState()
    {
        Assert.Null(SpotifyPlayerApi.ParsePlayback(""));
        var p = SpotifyPlayerApi.ParsePlayback("""{"device":{"id":"d"},"is_playing":true,"progress_ms":4321,"currently_playing_type":"track","item":{"uri":"spotify:track:t1"}}""")!;
        Assert.Equal(new SpotifyPlayback(true, 4321, "spotify:track:t1", "track", "d"), p);
        Assert.Equal(new PlaybackSample(4.321, true), p.ToSample("spotify:track:t1"));
        Assert.True(p.ToSample("spotify:track:other").WrongTrack);
        var ad = SpotifyPlayerApi.ParsePlayback("""{"is_playing":true,"progress_ms":1,"currently_playing_type":"ad","item":null}""")!;
        Assert.Equal("Spotify is playing an ad", ad.ToSample("spotify:track:t1").Failure);
    }

    [Theory]
    [InlineData(401, "", PlayerErrorKind.NotAuthorized, "Spotify sign-in expired")]
    [InlineData(403, """{"error":{"status":403,"message":"Premium required","reason":"PREMIUM_REQUIRED"}}""", PlayerErrorKind.Unavailable, "Spotify Connect needs Spotify Premium")]
    [InlineData(404, """{"error":{"status":404,"message":"Player command failed: No active device found","reason":"NO_ACTIVE_DEVICE"}}""", PlayerErrorKind.Unavailable, "Spotify isn't open on this PC. Start the Spotify app and try again")]
    [InlineData(429, "", PlayerErrorKind.Failed, "Spotify is rate-limiting Notchle. Try again in 7 s.")]
    [InlineData(502, """{"error":{"status":502,"message":"Bad gateway"}}""", PlayerErrorKind.Failed, "Spotify returned HTTP 502: Bad gateway")]
    public void MapsErrors(int status, string body, PlayerErrorKind kind, string message)
    {
        var error = SpotifyPlayerApi.MapError((HttpStatusCode)status, body, status == 429 ? TimeSpan.FromSeconds(6.2) : null);
        Assert.Equal((kind, message), (error.Kind, error.Message));
    }
}

public class AppSpotifyWebPlayerTests
{
    private static readonly Track T1 = new("t1", "spotify:track:t1", "x", new[] { "y" }, 200_000, null);
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    private static (SpotifyWebPlayer Player, AppFakeSpotify Spotify, AppFakeClock Clock, AppMemoryTokenStore Tokens) Make(
        string accessToken = "access-1", string? clientId = "cid", bool hasTokens = true)
    {
        var clock = new AppFakeClock();
        var spotify = new AppFakeSpotify(clock);
        var tokens = new AppMemoryTokenStore(hasTokens ? new SpotifyTokens(accessToken, "refresh-1", Now.AddHours(1)) : null);
        var player = new SpotifyWebPlayer(clientId, new HttpClient(spotify), tokens, "GAMING-PC", clock, now: () => Now);
        return (player, spotify, clock, tokens);
    }

    [Fact]
    public async Task PlaysSnippetOnThisPcTimedByProgressThenPauses()
    {
        var (player, spotify, clock, _) = Make();

        await player.PlaySnippetAsync(T1, 0, 5, CancellationToken.None);

        Assert.Equal(new[] { "GET /v1/me/player/devices", "PUT /v1/me/player", "PUT /v1/me/player/play?device_id=pc" },
            spotify.Requests.Take(3));
        Assert.Equal("""{"device_ids":["pc"],"play":false}""", spotify.Bodies[1]);
        Assert.Equal("""{"uris":["spotify:track:t1"],"position_ms":0}""", spotify.Bodies[2]);
        Assert.Equal("PUT /v1/me/player/pause?device_id=pc", spotify.Requests.Last());
        Assert.All(spotify.AuthHeaders, h => Assert.Equal("Bearer access-1", h));
        Assert.False(spotify.IsPlaying);
        // 5 s of audio minus EndLead, although Spotify said "playing" 0.3 s before it moved.
        Assert.InRange(spotify.PausedPosition, 4.9, 5.2);
        Assert.True(clock.Now.TotalSeconds >= 5.1);
    }

    [Fact]
    public async Task CancellationPausesAndThrows()
    {
        var (player, spotify, clock, _) = Make();
        using var cts = new CancellationTokenSource();
        clock.OnAdvance = now => { if (now >= TimeSpan.FromSeconds(2)) cts.Cancel(); };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => player.PlaySnippetAsync(T1, 0, 15, cts.Token));

        Assert.Equal("PUT /v1/me/player/pause?device_id=pc", spotify.Requests.Last());
        Assert.False(spotify.IsPlaying);
        Assert.True(clock.Now < TimeSpan.FromSeconds(2.6));
    }

    [Fact]
    public async Task ContinueResumesAndStopPauses()
    {
        var (player, spotify, _, _) = Make();
        await player.PlaySnippetAsync(T1, 0, 1, CancellationToken.None);

        await player.ContinuePlayingAsync();
        Assert.Equal("PUT /v1/me/player/play?device_id=pc", spotify.Requests.Last());
        Assert.Equal("", spotify.Bodies.Last());
        Assert.True(spotify.IsPlaying);

        await player.StopAsync();
        Assert.False(spotify.IsPlaying);
    }

    [Fact]
    public async Task ExpiredAccessTokenIsRefreshedOnceAndStored()
    {
        var (player, spotify, _, tokens) = Make(accessToken: "stale");
        spotify.ValidAccessToken = "access-2";

        await player.PlaySnippetAsync(T1, 0, 1, CancellationToken.None);

        Assert.Equal("GET /v1/me/player/devices", spotify.Requests[0]);
        Assert.Equal("POST /api/token", spotify.Requests[1]);
        Assert.Equal("grant_type=refresh_token&refresh_token=refresh-1&client_id=cid", spotify.Bodies[1]);
        Assert.Equal(("access-2", "refresh-1"), (tokens.Tokens!.AccessToken, tokens.Tokens.RefreshToken));
    }

    [Fact]
    public async Task NoSpotifyAppOnThisPcIsUnavailable()
    {
        var (player, spotify, _, _) = Make();
        spotify.DevicesJson = """{"devices":[]}""";

        var error = await Assert.ThrowsAsync<PlayerException>(() => player.PlaySnippetAsync(T1, 0, 5, CancellationToken.None));
        Assert.Equal(PlayerErrorKind.Unavailable, error.Kind);
    }

    [Fact]
    public async Task PremiumRequiredIsUnavailable()
    {
        var (player, spotify, _, _) = Make();
        spotify.Override = key => key.StartsWith("PUT /v1/me/player/play", StringComparison.Ordinal)
            ? AppFakeSpotify.Json(HttpStatusCode.Forbidden, """{"error":{"status":403,"message":"Premium required","reason":"PREMIUM_REQUIRED"}}""")
            : null;

        var error = await Assert.ThrowsAsync<PlayerException>(() => player.PlaySnippetAsync(T1, 0, 5, CancellationToken.None));
        Assert.Equal((PlayerErrorKind.Unavailable, "Spotify Connect needs Spotify Premium"), (error.Kind, error.Message));
    }

    [Fact]
    public async Task MissingClientIdOrTokensAreReportedWithoutCallingSpotify()
    {
        var (noId, spotify1, _, _) = Make(clientId: null);
        Assert.Equal(PlayerErrorKind.Unavailable,
            (await Assert.ThrowsAsync<PlayerException>(() => noId.PlaySnippetAsync(T1, 0, 5, default))).Kind);
        var (noTokens, spotify2, _, _) = Make(hasTokens: false);
        Assert.Equal(PlayerErrorKind.NotAuthorized,
            (await Assert.ThrowsAsync<PlayerException>(() => noTokens.PlaySnippetAsync(T1, 0, 5, default))).Kind);
        Assert.Empty(spotify1.Requests);
        Assert.Empty(spotify2.Requests);
    }

    [Fact]
    public async Task RevokedRefreshTokenSignsOut()
    {
        var (player, spotify, _, tokens) = Make(accessToken: "stale");
        spotify.Override = key => key == "POST /api/token"
            ? AppFakeSpotify.Json(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}""")
            : null;

        var error = await Assert.ThrowsAsync<PlayerException>(() => player.PlaySnippetAsync(T1, 0, 5, default));
        Assert.Equal(PlayerErrorKind.NotAuthorized, error.Kind);
        Assert.Null(tokens.Tokens);
    }
}

public class AppSpotifyAuthorizerTests
{
    [Fact]
    public async Task ConnectRunsPkceThroughTheLoopbackRedirect()
    {
        var now = new DateTimeOffset(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
        var accounts = new AppTokenEndpoint();
        var tokens = new AppMemoryTokenStore(null);
        Uri? opened = null;
        string? browserSaw = null;
        async Task Browser(Uri url)
        {
            opened = url;
            // Play the browser: Spotify redirects back to the loopback URI with code + state.
            var q = HttpUtility.ParseQueryString(url.Query);
            var redirect = $"{q["redirect_uri"]}?code=the-code&state={Uri.EscapeDataString(q["state"]!)}";
            _ = Task.Run(async () =>
            {
                using var browser = new HttpClient();
                await browser.GetStringAsync(new Uri(new Uri(q["redirect_uri"]!), "/favicon.ico")).ContinueWith(_ => { });
                browserSaw = await browser.GetStringAsync(redirect);
            });
            await Task.CompletedTask;
        }
        var authorizer = new SpotifyAuthorizer(new HttpClient(accounts), tokens, Browser, () => now);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await authorizer.ConnectAsync("cid", timeout.Token);

        var query = HttpUtility.ParseQueryString(opened!.Query);
        Assert.StartsWith("http://127.0.0.1:", query["redirect_uri"]);
        var form = HttpUtility.ParseQueryString(accounts.Body!);
        Assert.Equal(("authorization_code", "the-code", "cid"), (form["grant_type"], form["code"], form["client_id"]));
        Assert.Equal(query["redirect_uri"], form["redirect_uri"]);
        // The verifier sent to the token endpoint hashes to the challenge sent to the browser.
        Assert.Equal(query["code_challenge"], Pkce.Challenge(form["code_verifier"]!));
        Assert.Equal(new SpotifyTokens("a1", "r1", now.AddHours(1)), tokens.Tokens);
        for (var i = 0; i < 100 && browserSaw is null; i++) await Task.Delay(10);
        Assert.Contains("You can close this tab", browserSaw);
    }

    [Fact]
    public async Task ForgedStateIsRejected()
    {
        var tokens = new AppMemoryTokenStore(null);
        Task Browser(Uri url)
        {
            var q = HttpUtility.ParseQueryString(url.Query);
            _ = Task.Run(() => new HttpClient().GetStringAsync($"{q["redirect_uri"]}?code=x&state=forged"));
            return Task.CompletedTask;
        }
        var authorizer = new SpotifyAuthorizer(new HttpClient(new AppTokenEndpoint()), tokens, Browser);

        var error = await Assert.ThrowsAsync<PlayerException>(() => authorizer.ConnectAsync("cid", new CancellationTokenSource(10_000).Token));
        Assert.Equal(PlayerErrorKind.NotAuthorized, error.Kind);
        Assert.Null(tokens.Tokens);
    }

    private sealed class AppTokenEndpoint : HttpMessageHandler
    {
        public string? Body;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("https://accounts.spotify.com/api/token", request.RequestUri!.ToString());
            Body = await request.Content!.ReadAsStringAsync(ct);
            return AppFakeSpotify.Json(HttpStatusCode.OK, """{"access_token":"a1","refresh_token":"r1","expires_in":3600}""");
        }
    }
}
