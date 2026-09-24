using System.Collections.Concurrent;
using System.Text.Json;

namespace Notchle.Core;

/// Finds a track's album cover (Tren, 2026-09-24: "i'd like the album cover included when you
/// see the actual song"). Mirror of Sources/NotchleCore/Artwork/ArtworkResolver.swift.
/// Source: Spotify's public oEmbed endpoint (no auth),
/// <c>GET https://open.spotify.com/oembed?url=https://open.spotify.com/track/&lt;id&gt;</c>, whose
/// <c>thumbnail_url</c> is the 300×300 cover.
///
/// SPOILER RULE: callers resolve only once the track's outcome is decided (Correct / Revealed),
/// never while it is being guessed: the cover gives the album away.
public interface IArtworkResolver
{
    /// The cover's URL, or null when there is none or it could not be fetched. Never throws
    /// (except for cancellation).
    Task<Uri?> ResolveAsync(string trackId, CancellationToken cancellationToken = default);
}

public sealed class ArtworkResolver(HttpClient http) : IArtworkResolver
{
    /// Successful lookups only: a failure (offline) is retried next time.
    private readonly ConcurrentDictionary<string, Uri> _cache = new(StringComparer.Ordinal);

    public static Uri OEmbedUrl(string trackId) =>
        new("https://open.spotify.com/oembed?url=" + Uri.EscapeDataString($"https://open.spotify.com/track/{trackId}"));

    /// <c>thumbnail_url</c> of an oEmbed response, when it is an absolute http(s) URL.
    public static Uri? ParseOEmbed(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("thumbnail_url", out var url)
                   && url.ValueKind == JsonValueKind.String
                   && Uri.TryCreate(url.GetString(), UriKind.Absolute, out var uri)
                   && uri.Scheme is "https" or "http"
                ? uri
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task<Uri?> ResolveAsync(string trackId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(trackId)) return null;
        if (_cache.TryGetValue(trackId, out var cached)) return cached;
        try
        {
            using var response = await http.GetAsync(OEmbedUrl(trackId), cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (ParseOEmbed(json) is not { } uri) return null;
            _cache[trackId] = uri;
            return uri;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or IOException)
        {
            return null;
        }
    }
}
