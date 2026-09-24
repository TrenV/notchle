using System.Net;
using System.Text;
using System.Text.Json;

namespace Notchle.Core;

/// Reads tracks from Spotify's public embed page (__NEXT_DATA__ JSON). No login, no API key.
/// Port of Sources/NotchleCore/Source/EmbedTrackSource.swift; see there for the observed page
/// shape. Limits seen live: playlist embeds list at most 100 items, artist embeds the top 10.
public sealed class EmbedTrackSource(HttpClient http) : ITrackSource
{
    private readonly HttpClient _http = http;

    /// Spotify serves the server-rendered page (with __NEXT_DATA__) to desktop browsers.
    /// Same headers as the Swift source, so both platforms see the same page.
    internal static readonly IReadOnlyList<KeyValuePair<string, string>> RequestHeaders =
    [
        new("User-Agent", "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36"),
        new("Accept", "text/html,application/xhtml+xml"),
    ];

    public async Task<SourceListing> GetListingAsync(SourceRef source, CancellationToken cancellationToken = default)
    {
        var url = source.EmbedUrl;
        int status;
        byte[] body;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            foreach (var (name, value) in RequestHeaders) request.Headers.TryAddWithoutValidation(name, value);
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            status = (int)response.StatusCode;
            body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SourceException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            // Includes HttpClient timeouts (TaskCanceledException without our token cancelled),
            // like URLError.timedOut becomes .network on macOS.
            throw new SourceException(SourceErrorKind.Network, error.Message);
        }

        switch (status)
        {
            case (int)HttpStatusCode.OK: break;
            case (int)HttpStatusCode.NotFound: throw new SourceException(SourceErrorKind.NotFound);
            default: throw new SourceException(SourceErrorKind.Network, $"HTTP {status} from {url.AbsoluteUri}");
        }
        return Parse(body, source);
    }

    // Parsing

    internal static SourceListing Parse(byte[] html, SourceRef source)
    {
        var json = NextData(Encoding.UTF8.GetString(html));
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 512 });
        }
        catch (JsonException)
        {
            throw new SourceException(SourceErrorKind.ParseFailed, "__NEXT_DATA__ is not valid JSON");
        }
        using (document)
        {
            if (FindEntity(document.RootElement) is not { } entity)
                throw new SourceException(SourceErrorKind.ParseFailed, "no entity with a trackList array in __NEXT_DATA__");
            var name = NonEmptyString(entity, "name") ?? NonEmptyString(entity, "title")
                ?? throw new SourceException(SourceErrorKind.ParseFailed, "entity has no name or title");

            var items = entity.GetProperty("trackList");
            var tracks = new List<Track>();
            var seen = new HashSet<string>();
            int malformed = 0, count = 0;
            foreach (var item in items.EnumerateArray())
            {
                count++;
                switch (DecodeItem(item, out var track))
                {
                    case ItemResult.Track:
                        if (seen.Add(track!.Id)) tracks.Add(track);
                        break;
                    case ItemResult.Malformed:
                        malformed++;
                        break;
                }
            }
            if (tracks.Count == 0)
            {
                // Every item unreadable points at a changed page format, not an empty listing.
                if (malformed > 0 && malformed == count)
                    throw new SourceException(SourceErrorKind.ParseFailed, "no trackList item had a readable uri, title and subtitle");
                throw new SourceException(SourceErrorKind.Empty);
            }
            return new SourceListing(source, name, tracks);
        }
    }

    /// The JSON text inside `<script id="__NEXT_DATA__" …>…</script>`.
    internal static string NextData(string html)
    {
        const string marker = "id=\"__NEXT_DATA__\"";
        var at = html.IndexOf(marker, StringComparison.Ordinal);
        var tagEnd = at < 0 ? -1 : html.IndexOf('>', at + marker.Length);
        var close = tagEnd < 0 ? -1 : html.IndexOf("</script>", tagEnd + 1, StringComparison.Ordinal);
        if (close < 0)
            throw new SourceException(SourceErrorKind.ParseFailed, "no <script id=\"__NEXT_DATA__\"> block in the embed page");
        return html[(tagEnd + 1)..close];
    }

    /// props.pageProps.state.data.entity when it has a trackList, else the first object with a
    /// trackList array found breadth-first (in case Spotify moves it).
    internal static JsonElement? FindEntity(JsonElement root)
    {
        var node = root;
        var onPath = true;
        foreach (var key in new[] { "props", "pageProps", "state", "data", "entity" })
        {
            if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty(key, out node)) { onPath = false; break; }
        }
        if (onPath && HasTrackList(node)) return node;

        var queue = new Queue<JsonElement>();
        queue.Enqueue(root);
        while (queue.TryDequeue(out var current))
        {
            if (current.ValueKind == JsonValueKind.Object)
            {
                if (HasTrackList(current)) return current;
                // Sorted keys keep the search deterministic, and match the Swift order.
                foreach (var property in current.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                    queue.Enqueue(property.Value);
            }
            else if (current.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in current.EnumerateArray()) queue.Enqueue(element);
            }
        }
        return null;
    }

    private static bool HasTrackList(JsonElement node) =>
        node.ValueKind == JsonValueKind.Object
        && node.TryGetProperty("trackList", out var list)
        && list.ValueKind == JsonValueKind.Array;

    internal enum ItemResult
    {
        Track,
        /// A well-formed item Notchle can't play: not a track (episode, local file) or marked unplayable.
        Skipped,
        /// Missing the fields we need.
        Malformed,
    }

    internal static ItemResult DecodeItem(JsonElement item, out Track? track)
    {
        track = null;
        if (item.ValueKind != JsonValueKind.Object || NonEmptyString(item, "uri") is not { } uri) return ItemResult.Malformed;
        const string prefix = "spotify:track:";
        if (!uri.StartsWith(prefix, StringComparison.Ordinal)) return ItemResult.Skipped;
        var id = uri[prefix.Length..];
        if (!SourceRefParser.IsSpotifyId(id)) return ItemResult.Skipped;
        if (item.TryGetProperty("isPlayable", out var playable) && playable.ValueKind == JsonValueKind.False)
            return ItemResult.Skipped;
        if (NonEmptyString(item, "title") is not { } title
            || !item.TryGetProperty("subtitle", out var subtitle) || subtitle.ValueKind != JsonValueKind.String)
            return ItemResult.Malformed;
        var artists = SplitArtists(subtitle.GetString()!);
        if (artists.Count == 0) return ItemResult.Malformed;

        var durationMs = item.TryGetProperty("duration", out var duration) ? IntValue(duration) ?? 0 : 0;
        Uri? preview = null;
        if (item.TryGetProperty("audioPreview", out var audio) && audio.ValueKind == JsonValueKind.Object
            && NonEmptyString(audio, "url") is { } previewText
            && Uri.TryCreate(previewText, UriKind.Absolute, out var parsed))
        {
            preview = parsed;
        }
        track = new Track(id, uri, title, artists, durationMs, preview);
        return ItemResult.Track;
    }

    /// Spotify joins credited artists with "," + U+00A0 ("KAROL G, Judeline"). A comma
    /// followed by a plain space is part of a name ("Earth, Wind &amp; Fire", "Tyler, The Creator"),
    /// so only the comma+NBSP pair separates artists.
    internal static IReadOnlyList<string> SplitArtists(string subtitle) =>
        subtitle.Split(",\u00A0")
            .Select(part => part.Trim()) // Trim() includes U+00A0, like the Swift trim set.
            .Where(part => part.Length > 0)
            .ToArray();

    /// Integral JSON numbers, or fractional ones rounded half away from zero (Swift's rounded()).
    private static int? IntValue(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number) return null;
        if (value.TryGetInt32(out var i)) return i;
        if (value.TryGetDouble(out var d))
        {
            var rounded = Math.Round(d, MidpointRounding.AwayFromZero);
            if (rounded is >= int.MinValue and <= int.MaxValue) return (int)rounded;
        }
        return null;
    }

    private static string? NonEmptyString(JsonElement obj, string key) =>
        obj.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
        && value.GetString() is { Length: > 0 } text
            ? text
            : null;
}
