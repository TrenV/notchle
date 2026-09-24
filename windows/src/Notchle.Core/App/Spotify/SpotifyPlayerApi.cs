using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Notchle.Core.App.Playback;

namespace Notchle.Core.App.Spotify;

public sealed record SpotifyDevice(string? Id, string Name, string Type, bool IsActive, bool IsRestricted);

/// The parts of GET /v1/me/player the snippet timing needs.
public sealed record SpotifyPlayback(bool IsPlaying, int ProgressMs, string? ItemUri, string? CurrentlyPlayingType, string? DeviceId)
{
    public PlaybackSample ToSample(string expectedUri) => new(
        Position: ProgressMs / 1000.0,
        IsPlaying: IsPlaying,
        WrongTrack: ItemUri != expectedUri && CurrentlyPlayingType != "ad",
        Failure: CurrentlyPlayingType == "ad" && IsPlaying ? "Spotify is playing an ad" : null);
}

/// Spotify Web API player endpoints: pure request building, response parsing and error mapping.
/// https://developer.spotify.com/documentation/web-api/reference/start-a-users-playback
public static class SpotifyPlayerApi
{
    public static readonly Uri BaseUri = new("https://api.spotify.com/v1/");

    public static HttpRequestMessage Devices() => new(HttpMethod.Get, new Uri(BaseUri, "me/player/devices"));

    public static HttpRequestMessage PlaybackState() => new(HttpMethod.Get, new Uri(BaseUri, "me/player"));

    /// PUT /me/player: move playback to this PC's Spotify app without starting anything.
    public static HttpRequestMessage Transfer(string deviceId) =>
        Put("me/player", new JsonObject { ["device_ids"] = new JsonArray(deviceId), ["play"] = false });

    /// PUT /me/player/play: this one track, from positionMs, on the given device.
    public static HttpRequestMessage Play(string deviceId, string trackUri, int positionMs) =>
        Put($"me/player/play?device_id={Uri.EscapeDataString(deviceId)}",
            new JsonObject { ["uris"] = new JsonArray(trackUri), ["position_ms"] = Math.Max(0, positionMs) });

    /// PUT /me/player/play without a body resumes the current track.
    public static HttpRequestMessage Resume(string? deviceId) => Put(WithDevice("me/player/play", deviceId), null);

    public static HttpRequestMessage Pause(string? deviceId) => Put(WithDevice("me/player/pause", deviceId), null);

    public static HttpRequestMessage Seek(int positionMs, string? deviceId) =>
        Put(WithDevice($"me/player/seek?position_ms={Math.Max(0, positionMs).ToString(CultureInfo.InvariantCulture)}", deviceId), null);

    public static IReadOnlyList<SpotifyDevice> ParseDevices(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("devices", out var devices) || devices.ValueKind != JsonValueKind.Array)
            return Array.Empty<SpotifyDevice>();
        return devices.EnumerateArray().Select(d => new SpotifyDevice(
            Id: Str(d, "id"),
            Name: Str(d, "name") ?? "",
            Type: Str(d, "type") ?? "",
            IsActive: Bool(d, "is_active"),
            IsRestricted: Bool(d, "is_restricted"))).ToList();
    }

    /// The Spotify desktop app on this PC: a controllable "Computer" device, preferring the one
    /// named after this machine (the app's default name), then the active one.
    public static SpotifyDevice? PickLocalDevice(IReadOnlyList<SpotifyDevice> devices, string machineName)
    {
        var candidates = devices
            .Where(d => d.Id is not null && !d.IsRestricted && d.Type.Equals("Computer", StringComparison.OrdinalIgnoreCase))
            .ToList();
        return candidates.FirstOrDefault(d => d.Name.Equals(machineName, StringComparison.OrdinalIgnoreCase))
               ?? candidates.FirstOrDefault(d => d.IsActive)
               ?? candidates.FirstOrDefault();
    }

    /// GET /me/player: 204 (empty body) means nothing is playing anywhere.
    public static SpotifyPlayback? ParsePlayback(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string? itemUri = root.TryGetProperty("item", out var item) && item.ValueKind == JsonValueKind.Object ? Str(item, "uri") : null;
        string? deviceId = root.TryGetProperty("device", out var device) && device.ValueKind == JsonValueKind.Object ? Str(device, "id") : null;
        var progress = root.TryGetProperty("progress_ms", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : 0;
        return new SpotifyPlayback(Bool(root, "is_playing"), progress, itemUri, Str(root, "currently_playing_type"), deviceId);
    }

    /// Maps a failed player call to a PlayerException with a message fit for the island.
    public static PlayerException MapError(HttpStatusCode status, string body, TimeSpan? retryAfter)
    {
        string? message = null, reason = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                message = Str(error, "message");
                reason = Str(error, "reason");
            }
        }
        catch (JsonException) { /* not JSON */ }

        return (int)status switch
        {
            401 => new PlayerException(PlayerErrorKind.NotAuthorized, "Spotify sign-in expired"),
            403 when reason == "PREMIUM_REQUIRED" =>
                new PlayerException(PlayerErrorKind.Unavailable, "Spotify Connect needs Spotify Premium"),
            403 => new PlayerException(PlayerErrorKind.Unavailable,
                $"Spotify refused playback control ({message ?? "forbidden"}). It needs Premium, and your account added under User Management in your Spotify developer app"),
            404 => new PlayerException(PlayerErrorKind.Unavailable,
                "Spotify isn't open on this PC. Start the Spotify app and try again"),
            429 => new PlayerException(PlayerErrorKind.Failed,
                retryAfter is { } wait
                    ? $"Spotify is rate-limiting Notchle. Try again in {Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds))} s."
                    : "Spotify is rate-limiting Notchle. Try again in a minute."),
            _ => new PlayerException(PlayerErrorKind.Failed,
                $"Spotify returned HTTP {(int)status}{(message is null ? "" : $": {message}")}"),
        };
    }

    private static string WithDevice(string path, string? deviceId) =>
        deviceId is null ? path : $"{path}{(path.Contains('?') ? '&' : '?')}device_id={Uri.EscapeDataString(deviceId)}";

    private static HttpRequestMessage Put(string path, JsonNode? body) => new(HttpMethod.Put, new Uri(BaseUri, path))
    {
        // Spotify wants a Content-Length on PUT even without a body.
        Content = new StringContent(body?.ToJsonString() ?? "", Encoding.UTF8, "application/json"),
    };

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
