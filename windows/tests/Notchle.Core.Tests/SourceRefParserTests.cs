using Notchle.Core;

namespace Notchle.Core.Tests;

/// Same tables as Tests/NotchleCoreTests/SourceRefParsingTests.swift.
public class SourceRefParserTests
{
    private const string PlaylistId = "37i9dQZF1DXcBWIGoYBM5M";
    private const string AlbumId = "0ETFjACtuP2ADo6LFhL6HN";
    private const string ArtistId = "06HL4z0CvFAxyc27GXpf02";

    public static TheoryData<string, SourceKind, string> Accepted => new()
    {
        { $"https://open.spotify.com/playlist/{PlaylistId}", SourceKind.Playlist, PlaylistId },
        { $"https://open.spotify.com/album/{AlbumId}", SourceKind.Album, AlbumId },
        { $"https://open.spotify.com/artist/{ArtistId}", SourceKind.Artist, ArtistId },
        { $"http://open.spotify.com/playlist/{PlaylistId}", SourceKind.Playlist, PlaylistId },
        { $"open.spotify.com/playlist/{PlaylistId}", SourceKind.Playlist, PlaylistId },
        { $"HTTPS://Open.Spotify.com/playlist/{PlaylistId}", SourceKind.Playlist, PlaylistId },
        { $"https://open.spotify.com/playlist/{PlaylistId}?si=a1b2c3d4e5f6", SourceKind.Playlist, PlaylistId },
        { $"https://open.spotify.com/playlist/{PlaylistId}?si=abc&pi=u-xyz", SourceKind.Playlist, PlaylistId },
        { $"https://open.spotify.com/playlist/{PlaylistId}#frag", SourceKind.Playlist, PlaylistId },
        { $"https://open.spotify.com/playlist/{PlaylistId}/", SourceKind.Playlist, PlaylistId },
        { $"https://open.spotify.com/playlist/{PlaylistId}/?si=abc", SourceKind.Playlist, PlaylistId },
        { $"https://open.spotify.com/intl-nl/playlist/{PlaylistId}", SourceKind.Playlist, PlaylistId },
        { $"https://open.spotify.com/intl-pt-BR/album/{AlbumId}?si=x", SourceKind.Album, AlbumId },
        { $"https://open.spotify.com/embed/playlist/{PlaylistId}", SourceKind.Playlist, PlaylistId },
        { $"https://open.spotify.com/embed/artist/{ArtistId}?utm_source=generator", SourceKind.Artist, ArtistId },
        { $"https://open.spotify.com/intl-de/embed/album/{AlbumId}", SourceKind.Album, AlbumId },
        { $"  https://open.spotify.com/album/{AlbumId}\n", SourceKind.Album, AlbumId },
        { $"\thttps://open.spotify.com/artist/{ArtistId} ", SourceKind.Artist, ArtistId },
        { $"spotify:playlist:{PlaylistId}", SourceKind.Playlist, PlaylistId },
        { $"spotify:album:{AlbumId}", SourceKind.Album, AlbumId },
        { $"spotify:artist:{ArtistId}", SourceKind.Artist, ArtistId },
        { $" spotify:album:{AlbumId} ", SourceKind.Album, AlbumId },
    };

    [Theory, MemberData(nameof(Accepted))]
    public void ParsesSupportedLinks(string input, SourceKind kind, string id) =>
        Assert.Equal(new SourceRef(kind, id), SourceRefParser.TryParse(input));

    public static TheoryData<string> Rejected => new()
    {
        "",
        "   ",
        "not a url",
        // Unsupported kinds.
        "https://open.spotify.com/track/2FZcjBYK4dTt48q94pJbJD",
        "spotify:track:2FZcjBYK4dTt48q94pJbJD",
        "https://open.spotify.com/show/4rOoJ6Egrf8K2IrywzwOMk",
        "https://open.spotify.com/episode/512ojhOuo1ktJprKbVcKyQ",
        "spotify:episode:512ojhOuo1ktJprKbVcKyQ",
        "https://open.spotify.com/user/spotify",
        $"https://open.spotify.com/user/spotify/playlist/{PlaylistId}",
        $"spotify:user:spotify:playlist:{PlaylistId}",
        // Other hosts and look-alikes.
        $"https://spotify.com/playlist/{PlaylistId}",
        $"https://play.spotify.com/playlist/{PlaylistId}",
        $"https://open.spotify.com.evil.example/playlist/{PlaylistId}",
        $"https://evil.example/open.spotify.com/playlist/{PlaylistId}",
        $"https://open.spotify.com:8443/playlist/{PlaylistId}",
        $"https://user@open.spotify.com/playlist/{PlaylistId}",
        $"ftp://open.spotify.com/playlist/{PlaylistId}",
        // Bad ids.
        "https://open.spotify.com/playlist/37i9dQZF1DXcBWIGoYBM5",    // 21 chars
        "https://open.spotify.com/playlist/37i9dQZF1DXcBWIGoYBM5Mx",  // 23 chars
        "https://open.spotify.com/playlist/37i9dQZF1DXcBWIGoYB-5M",   // non-base62
        "https://open.spotify.com/playlist/37i9dQZF1DXcBWIGoYBM5\u00C9",   // non-ASCII
        "https://open.spotify.com/playlist/",
        "https://open.spotify.com/playlist",
        "https://open.spotify.com/",
        "spotify:playlist:",
        "spotify:playlist:short",
        $"spotify:playlist:{PlaylistId}:extra",
        // Wrong shapes.
        $"https://open.spotify.com/playlist/{PlaylistId}/tracks",
        $"https://open.spotify.com/embed/embed/playlist/{PlaylistId}",
        $"https://open.spotify.com/intl-nl/intl-de/playlist/{PlaylistId}",
        $"https://open.spotify.com/{PlaylistId}",
    };

    [Theory, MemberData(nameof(Rejected))]
    public void RejectsUnsupportedLinks(string input) => Assert.Null(SourceRefParser.TryParse(input));

    [Fact]
    public void EmbedUrlRoundTripsThroughParser()
    {
        foreach (var kind in Enum.GetValues<SourceKind>())
        {
            var source = new SourceRef(kind, PlaylistId);
            Assert.Equal($"https://open.spotify.com/embed/{kind.ToString().ToLowerInvariant()}/{PlaylistId}", source.EmbedUrl.AbsoluteUri);
            Assert.Equal(source, SourceRefParser.TryParse(source.EmbedUrl.AbsoluteUri));
        }
    }

    [Fact]
    public void KindAndSchemeAreCaseInsensitiveButIdIsNot()
    {
        Assert.Equal(new SourceRef(SourceKind.Album, AlbumId), SourceRefParser.TryParse($"SPOTIFY:ALBUM:{AlbumId}"));
        Assert.Equal(new SourceRef(SourceKind.Artist, ArtistId), SourceRefParser.TryParse($"https://open.spotify.com/INTL-nl/EMBED/Artist/{ArtistId}"));
        Assert.Equal(AlbumId.ToUpperInvariant(), SourceRefParser.TryParse($"spotify:album:{AlbumId.ToUpperInvariant()}")!.Id);
    }
}
