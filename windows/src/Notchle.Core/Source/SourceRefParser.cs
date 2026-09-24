namespace Notchle.Core;

/// Port of Sources/NotchleCore/Source/SourceRef+Parsing.swift. Accepted shapes (surrounding
/// whitespace ignored):
/// - `https://open.spotify.com/&lt;kind&gt;/&lt;id&gt;`, also `http://` or no scheme at all
/// - an optional `/intl-xx/` locale segment and/or `/embed/` segment before the kind
/// - an optional trailing slash, `?query` and `#fragment`
/// - `spotify:&lt;kind&gt;:&lt;id&gt;`
/// The kind is playlist, album or artist; the id is 22 base62 characters. Tracks, shows,
/// episodes, users, other hosts and malformed ids give null.
public static class SourceRefParser
{
    /// Same accepted forms as the Swift SourceRef(string:): open.spotify.com links (locale
    /// segment, /embed/, query, fragment, missing scheme) and spotify:{kind}:{id} URIs.
    public static SourceRef? TryParse(string text)
    {
        if (text is null) return null;
        var trimmed = text.Trim();
        var parts = trimmed.StartsWith("spotify:", StringComparison.OrdinalIgnoreCase)
            ? UriParts(trimmed)
            : WebParts(trimmed);
        if (parts is not var (kindText, id)) return null;
        if (ParseKind(kindText) is not { } kind || !IsSpotifyId(id)) return null;
        return new SourceRef(kind, id);
    }

    /// Spotify ids are 22 base62 (ASCII) characters.
    internal static bool IsSpotifyId(string id)
    {
        if (id.Length != 22) return false;
        foreach (var c in id)
        {
            if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'z') or (>= 'A' and <= 'Z'))) return false;
        }
        return true;
    }

    private static SourceKind? ParseKind(string text) => text.ToLowerInvariant() switch
    {
        "playlist" => SourceKind.Playlist,
        "album" => SourceKind.Album,
        "artist" => SourceKind.Artist,
        _ => null,
    };

    /// `spotify:<kind>:<id>`. Legacy `spotify:user:<name>:playlist:<id>` is rejected.
    private static (string Kind, string Id)? UriParts(string text)
    {
        var fields = text.Split(':');
        return fields.Length == 3 ? (fields[1], fields[2]) : null;
    }

    /// `[scheme://]open.spotify.com[/intl-xx][/embed]/<kind>/<id>[/][?query][#fragment]`
    private static (string Kind, string Id)? WebParts(string text)
    {
        var rest = text;
        foreach (var scheme in new[] { "https://", "http://" })
        {
            if (rest.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
            {
                rest = rest[scheme.Length..];
                break;
            }
        }
        var cut = rest.IndexOfAny(['?', '#']);
        if (cut >= 0) rest = rest[..cut];

        var slash = rest.IndexOf('/');
        if (slash < 0) return null;
        // Exact host match: rejects ports, userinfo, subdomains and look-alike hosts.
        if (!string.Equals(rest[..slash], "open.spotify.com", StringComparison.OrdinalIgnoreCase)) return null;

        var segments = new Queue<string>(rest[slash..].Split('/', StringSplitOptions.RemoveEmptyEntries));
        bool sawLocale = false, sawEmbed = false;
        while (segments.TryPeek(out var first))
        {
            if (!sawLocale && first.StartsWith("intl-", StringComparison.OrdinalIgnoreCase)) sawLocale = true;
            else if (!sawEmbed && first.Equals("embed", StringComparison.OrdinalIgnoreCase)) sawEmbed = true;
            else break;
            segments.Dequeue();
        }
        return segments.Count == 2 ? (segments.Dequeue(), segments.Dequeue()) : null;
    }
}
