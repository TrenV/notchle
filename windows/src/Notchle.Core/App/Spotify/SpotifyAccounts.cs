using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;

namespace Notchle.Core.App.Spotify;

/// Proof Key for Code Exchange (RFC 7636), S256 method.
public static class Pkce
{
    /// 64 random bytes → 86 base64url characters (the RFC allows 43–128 from the unreserved set).
    public static string CreateVerifier() => Base64Url(RandomNumberGenerator.GetBytes(64));

    /// BASE64URL(SHA256(ASCII(verifier))), no padding.
    public static string Challenge(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    public static string CreateState() => Base64Url(RandomNumberGenerator.GetBytes(16));

    public static string Base64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

public sealed record SpotifyTokens(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt, string? Scope = null)
{
    /// Refresh a minute early, so a token can't expire in the middle of a snippet.
    public bool IsFresh(DateTimeOffset now) => now < ExpiresAt - TimeSpan.FromMinutes(1);
}

/// Where the Spotify tokens live. Windows: DPAPI-protected file (Notchle.Windows.Playback).
public interface ISpotifyTokenStore
{
    SpotifyTokens? Load();
    void Save(SpotifyTokens? tokens);
}

/// Authorization Code with PKCE against accounts.spotify.com: pure request building and parsing.
/// https://developer.spotify.com/documentation/web-api/tutorials/code-pkce-flow
public static class SpotifyAccounts
{
    public const string Scopes = "user-modify-playback-state user-read-playback-state";
    public static readonly Uri AuthorizeEndpoint = new("https://accounts.spotify.com/authorize");
    public static readonly Uri TokenEndpoint = new("https://accounts.spotify.com/api/token");

    /// Spotify rejects "localhost"; loopback must be an IP literal. The dashboard refuses a
    /// port-less loopback URI in practice (Tren, 2026-09-24), so Notchle always listens on this
    /// fixed port and the dashboard gets exactly RedirectUriString.
    public const int CallbackPort = 43821;
    public const string RedirectUriString = "http://127.0.0.1:43821/callback";
    public static Uri RedirectUri(int port) => new($"http://127.0.0.1:{port}/callback");

    public static Uri AuthorizeUrl(string clientId, Uri redirectUri, string codeChallenge, string state)
    {
        var query = Query(
            ("response_type", "code"),
            ("client_id", clientId),
            ("scope", Scopes),
            ("redirect_uri", redirectUri.ToString()),
            ("state", state),
            ("code_challenge_method", "S256"),
            ("code_challenge", codeChallenge));
        return new Uri($"{AuthorizeEndpoint}?{query}");
    }

    public static HttpRequestMessage TokenRequest(string clientId, string code, Uri redirectUri, string codeVerifier) =>
        Form(("grant_type", "authorization_code"), ("code", code), ("redirect_uri", redirectUri.ToString()),
            ("client_id", clientId), ("code_verifier", codeVerifier));

    public static HttpRequestMessage RefreshRequest(string clientId, string refreshToken) =>
        Form(("grant_type", "refresh_token"), ("refresh_token", refreshToken), ("client_id", clientId));

    /// Parses a token response. Refresh responses may omit refresh_token: keep the old one.
    public static SpotifyTokens ParseTokenResponse(string json, DateTimeOffset now, string? previousRefreshToken = null)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var access = root.GetProperty("access_token").GetString();
            var refresh = root.TryGetProperty("refresh_token", out var r) ? r.GetString() : previousRefreshToken;
            var expiresIn = root.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 3600;
            var scope = root.TryGetProperty("scope", out var s) ? s.GetString() : null;
            if (string.IsNullOrEmpty(access) || string.IsNullOrEmpty(refresh))
                throw new PlayerException(PlayerErrorKind.Failed, "Spotify's sign-in answer had no token");
            return new SpotifyTokens(access, refresh, now + TimeSpan.FromSeconds(expiresIn), scope);
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new PlayerException(PlayerErrorKind.Failed, "Spotify's sign-in answer couldn't be read");
        }
    }

    /// The error of a failed token request (400 invalid_grant: the refresh token was revoked).
    public static PlayerException TokenError(int status, string body)
    {
        var code = TryReadString(body, "error");
        return code == "invalid_grant" || status is 400 or 401
            ? new PlayerException(PlayerErrorKind.NotAuthorized, $"Spotify sign-in was rejected ({code ?? status.ToString()})")
            : new PlayerException(PlayerErrorKind.Failed, $"Spotify sign-in failed (HTTP {status})");
    }

    /// Reads the code out of the redirect's request target ("/callback?code=…&amp;state=…").
    /// A state mismatch (CSRF) or an error such as access_denied means NotAuthorized.
    public static string ParseCallback(string requestTarget, string expectedState)
    {
        var queryStart = requestTarget.IndexOf('?');
        var query = HttpUtility.ParseQueryString(queryStart < 0 ? "" : requestTarget[(queryStart + 1)..]);
        if (query["state"] != expectedState)
            throw new PlayerException(PlayerErrorKind.NotAuthorized, "Spotify sign-in answer didn't match the request");
        if (query["error"] is { } error)
            throw new PlayerException(PlayerErrorKind.NotAuthorized, $"Spotify sign-in was cancelled ({error})");
        return query["code"] is { Length: > 0 } code
            ? code
            : throw new PlayerException(PlayerErrorKind.NotAuthorized, "Spotify sign-in answer had no code");
    }

    private static HttpRequestMessage Form(params (string Key, string Value)[] fields) =>
        new(HttpMethod.Post, TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(fields.Select(f => KeyValuePair.Create(f.Key, f.Value))),
        };

    private static string Query(params (string Key, string Value)[] fields) =>
        string.Join("&", fields.Select(f => $"{f.Key}={Uri.EscapeDataString(f.Value)}"));

    internal static string? TryReadString(string json, string property)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty(property, out var value)
                   && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static void Authorize(HttpRequestMessage request, string accessToken) =>
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
}
