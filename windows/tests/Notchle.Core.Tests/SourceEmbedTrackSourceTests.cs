using System.Net;
using System.Text;
using Notchle.Core;

namespace Notchle.Core.Tests;

/// Serves a canned response (or throws) and records what was requested.
public sealed class SourceFakeHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> reply) : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];

    public static SourceFakeHandler Ok(byte[] body) => Status(HttpStatusCode.OK, body);

    public static SourceFakeHandler Status(HttpStatusCode code, byte[]? body = null) =>
        new((_, _) => new HttpResponseMessage(code) { Content = new ByteArrayContent(body ?? []) });

    public static SourceFakeHandler Throws(Exception error) => new((_, _) => throw error);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(reply(request, cancellationToken));
    }
}

public static class SourceFixtures
{
    /// The same saved Spotify pages the Swift tests read.
    public static byte[] Data(string name) => File.ReadAllBytes(Fixtures.Path(name + ".html"));

    /// A minimal embed page wrapping trackList items in the real entity shape.
    public static byte[] Page(string items, string? name = "Synthetic")
    {
        var nameField = name is null ? "" : $"\"name\":\"{name}\",";
        var html = "<!DOCTYPE html><html><body><div id=\"__next\"></div>\n"
            + "<script id=\"__NEXT_DATA__\" type=\"application/json\">{\"props\":{\"pageProps\":{\"state\":{\"data\":{\"entity\":{"
            + nameField + "\"type\":\"playlist\",\"trackList\":[" + items + "]}}}}}}</script>\n</body></html>";
        return Encoding.UTF8.GetBytes(html);
    }

    public static string Item(string id, string title = "Song", string subtitle = "Artist", string uriKind = "track",
        bool isPlayable = true, int duration = 200_000, string? preview = "https://p.scdn.co/mp3-preview/abc")
    {
        var previewField = preview is null ? "" : $",\"audioPreview\":{{\"format\":\"MP3_96\",\"url\":\"{preview}\"}}";
        return $"{{\"uri\":\"spotify:{uriKind}:{id}\",\"uid\":\"u\",\"title\":\"{title}\",\"subtitle\":\"{subtitle}\","
            + $"\"duration\":{duration},\"isPlayable\":{(isPlayable ? "true" : "false")},"
            + $"\"playabilityReason\":\"{(isPlayable ? "PLAYABLE" : "NOT_AVAILABLE")}\",\"entityType\":\"{uriKind}\"{previewField}}}";
    }
}

/// Mirrors Tests/NotchleCoreTests/SourceEmbedTrackSourceTests.swift.
public class SourceEmbedTrackSourceTests
{
    private static readonly SourceRef Ref = new(SourceKind.Playlist, "37i9dQZF1DXcBWIGoYBM5M");
    private const string IdA = "2FZcjBYK4dTt48q94pJbJD";
    private const string IdB = "0pNeVovbiZHkulpGeOx1Gj";
    private const string IdC = "53iuhJlwXhSER5J2IYYv1W";

    private static Task<SourceListing> Listing(byte[] body, SourceRef? source = null) =>
        new EmbedTrackSource(new HttpClient(SourceFakeHandler.Ok(body))).GetListingAsync(source ?? Ref);

    private static async Task ExpectError(SourceErrorKind kind, string message, SourceFakeHandler handler)
    {
        var error = await Assert.ThrowsAsync<SourceException>(
            () => new EmbedTrackSource(new HttpClient(handler)).GetListingAsync(Ref));
        Assert.Equal(kind, error.Kind);
        Assert.Equal(message, error.Message);
    }

    // Real fixtures

    [Fact]
    public async Task RequestsEmbedUrlWithBrowserUserAgent()
    {
        var handler = SourceFakeHandler.Ok(SourceFixtures.Data("embed-playlist-todays-top-hits"));
        await new EmbedTrackSource(new HttpClient(handler)).GetListingAsync(Ref);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("https://open.spotify.com/embed/playlist/37i9dQZF1DXcBWIGoYBM5M", request.RequestUri!.AbsoluteUri);
        Assert.StartsWith("Mozilla/5.0 (Macintosh", string.Join(" ", request.Headers.GetValues("User-Agent")));
        Assert.Equal("text/html,application/xhtml+xml", string.Join(",", request.Headers.GetValues("Accept")));
    }

    [Fact]
    public async Task ParsesTodaysTopHits()
    {
        var result = await Listing(SourceFixtures.Data("embed-playlist-todays-top-hits"));
        Assert.Equal(Ref, result.Ref);
        Assert.Equal("Today\u2019s Top Hits", result.Name);
        Assert.Equal(50, result.Tracks.Count);
        Assert.Equal(50, result.Tracks.Select(t => t.Id).Distinct().Count());
        var first = result.Tracks[0];
        Assert.Equal("2FZcjBYK4dTt48q94pJbJD", first.Id);
        Assert.Equal("spotify:track:2FZcjBYK4dTt48q94pJbJD", first.Uri);
        Assert.Equal("Bass Persuades", first.Title);
        Assert.Equal(["Miley Cyrus"], first.Artists);
        Assert.Equal(202_460, first.DurationMs);
        Assert.Equal("https://p.scdn.co/mp3-preview/e57b7c5fcb52b8bb4c781cc1e2e5b25f8a6c6e79", first.PreviewUrl!.AbsoluteUri);
        // Subtitle is "KAROL G,\u00A0Judeline,\u00A0rusowsky" in the page.
        var multi = result.Tracks.First(t => t.Artists[0] == "KAROL G");
        Assert.Equal(["KAROL G", "Judeline", "rusowsky"], multi.Artists);
        foreach (var track in result.Tracks)
        {
            Assert.Equal($"spotify:track:{track.Id}", track.Uri);
            Assert.NotEmpty(track.Title);
            Assert.NotEmpty(track.Artists);
            Assert.All(track.Artists, a => Assert.True(!a.Contains('\u00A0') && a == a.Trim()));
            Assert.True(track.DurationMs > 0);
            Assert.NotNull(track.PreviewUrl);
        }
    }

    [Fact]
    public async Task ParsesAlbumEmbed()
    {
        var album = new SourceRef(SourceKind.Album, "0ETFjACtuP2ADo6LFhL6HN");
        var result = await Listing(SourceFixtures.Data("Source-embed-album-abbey-road"), album);
        Assert.Equal(album, result.Ref);
        Assert.Equal("Abbey Road (Remastered)", result.Name);
        Assert.Equal(17, result.Tracks.Count);
        Assert.Equal("Come Together - Remastered 2009", result.Tracks[0].Title);
        Assert.All(result.Tracks, t => Assert.Equal(["The Beatles"], t.Artists));
    }

    [Fact]
    public async Task ParsesArtistEmbedTopTracks()
    {
        var artist = new SourceRef(SourceKind.Artist, "06HL4z0CvFAxyc27GXpf02");
        var result = await Listing(SourceFixtures.Data("Source-embed-artist-taylor-swift"), artist);
        Assert.Equal("Taylor Swift", result.Name);
        Assert.Equal(10, result.Tracks.Count);
        Assert.Equal(["The Fate of Ophelia", "Blank Space"], result.Tracks.Take(2).Select(t => t.Title));
    }

    [Fact]
    public async Task LargePlaylistEmbedIsCappedAt100AndKeepsCommaNames()
    {
        // All Out 80s had 150 songs on 2026-09-24; the embed lists the first 100.
        var result = await Listing(SourceFixtures.Data("Source-embed-playlist-all-out-80s"));
        Assert.Equal("All Out 80s", result.Name);
        Assert.Equal(100, result.Tracks.Count);
        Assert.Contains(result.Tracks, t => t.Artists.SequenceEqual(["Earth, Wind & Fire"]));
        Assert.Contains(result.Tracks, t => t.Artists.SequenceEqual(["Eurythmics", "Annie Lennox", "Dave Stewart"]));
    }

    // Item mapping

    [Fact]
    public async Task SkipsNonTracksUnplayableAndDuplicatesKeepingOrder()
    {
        var items = string.Join(",",
            SourceFixtures.Item(IdA, title: "First"),
            SourceFixtures.Item(IdB, uriKind: "episode"),
            SourceFixtures.Item(IdC, title: "Unplayable", isPlayable: false),
            SourceFixtures.Item(IdB, title: "Second"),
            SourceFixtures.Item(IdA, title: "First again"),
            """{"uri":"spotify:local:::Some+Local+File:180","title":"Local","subtitle":"Me","duration":1,"isPlayable":true}""",
            """{"uri":"spotify:track:not-a-valid-id","title":"Bad","subtitle":"Me","duration":1}""");
        var result = await Listing(SourceFixtures.Page(items));
        Assert.Equal([IdA, IdB], result.Tracks.Select(t => t.Id));
        Assert.Equal(["First", "Second"], result.Tracks.Select(t => t.Title));
    }

    [Fact]
    public async Task SplitsArtistsOnlyOnCommaNbsp()
    {
        // Real subtitle from the All Out 2010s embed.
        var result = await Listing(SourceFixtures.Page(SourceFixtures.Item(IdA, subtitle: "Tyler, The Creator,\u00A0Kali Uchis")));
        Assert.Equal(["Tyler, The Creator", "Kali Uchis"], result.Tracks[0].Artists);
        Assert.Equal(["A", "B"], EmbedTrackSource.SplitArtists(" A ,\u00A0 B\u00A0,\u00A0"));
    }

    [Fact]
    public async Task MapsMissingPreviewToNullAndMissingPlayableFlagToPlayable()
    {
        var items = string.Join(",",
            SourceFixtures.Item(IdA, preview: null),
            $$"""{"uri":"spotify:track:{{IdB}}","title":"No flag","subtitle":"X","duration":123}""");
        var result = await Listing(SourceFixtures.Page(items));
        Assert.Equal([IdA, IdB], result.Tracks.Select(t => t.Id));
        Assert.Null(result.Tracks[0].PreviewUrl);
        Assert.Equal(123, result.Tracks[1].DurationMs);
    }

    [Fact]
    public async Task RoundsFractionalAndDefaultsMissingDuration()
    {
        var items = string.Join(",",
            $$"""{"uri":"spotify:track:{{IdA}}","title":"A","subtitle":"X","duration":1234.5}""",
            $$"""{"uri":"spotify:track:{{IdB}}","title":"B","subtitle":"X"}""");
        var result = await Listing(SourceFixtures.Page(items));
        Assert.Equal([1235, 0], result.Tracks.Select(t => t.DurationMs));
    }

    [Fact]
    public async Task FallsBackToTitleForListingName()
    {
        var html = """<script id="__NEXT_DATA__" type="application/json">{"props":{"pageProps":{"state":{"data":{"entity":{"title":"Titled","trackList":["""
            + SourceFixtures.Item(IdA) + "]}}}}}}</script>";
        Assert.Equal("Titled", (await Listing(Encoding.UTF8.GetBytes(html))).Name);
    }

    [Fact]
    public async Task FindsTrackListOutsideTheKnownPath()
    {
        var html = """<script type="application/json" id="__NEXT_DATA__">{"props":{"moved":[{"entity":{"name":"Moved","trackList":["""
            + SourceFixtures.Item(IdA) + "]}}]}}</script>";
        var result = await Listing(Encoding.UTF8.GetBytes(html));
        Assert.Equal("Moved", result.Name);
        Assert.Equal([IdA], result.Tracks.Select(t => t.Id));
    }

    // Errors

    [Fact]
    public Task NotFoundOn404() => ExpectError(SourceErrorKind.NotFound, "NotFound", SourceFakeHandler.Status(HttpStatusCode.NotFound));

    [Theory]
    [InlineData(301)]
    [InlineData(403)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(503)]
    public Task NetworkErrorOnOtherStatus(int code) =>
        ExpectError(SourceErrorKind.Network, $"HTTP {code} from https://open.spotify.com/embed/playlist/37i9dQZF1DXcBWIGoYBM5M",
            SourceFakeHandler.Status((HttpStatusCode)code));

    [Fact]
    public Task NetworkErrorWhenTransportThrows() =>
        ExpectError(SourceErrorKind.Network, "offline", SourceFakeHandler.Throws(new HttpRequestException("offline")));

    [Fact]
    public Task TimeoutIsANetworkErrorNotACancellation() =>
        ExpectError(SourceErrorKind.Network, "timed out",
            SourceFakeHandler.Throws(new TaskCanceledException("timed out", new TimeoutException())));

    [Fact]
    public async Task CancellationPassesThrough()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var source = new EmbedTrackSource(new HttpClient(SourceFakeHandler.Ok(SourceFixtures.Page(SourceFixtures.Item(IdA)))));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.GetListingAsync(Ref, cancelled.Token));
    }

    [Fact]
    public Task ParseFailedWithoutNextData() =>
        ExpectError(SourceErrorKind.ParseFailed, "no <script id=\"__NEXT_DATA__\"> block in the embed page",
            SourceFakeHandler.Ok("<html><body>captcha</body></html>"u8.ToArray()));

    [Fact]
    public Task ParseFailedOnInvalidJson() =>
        ExpectError(SourceErrorKind.ParseFailed, "__NEXT_DATA__ is not valid JSON",
            SourceFakeHandler.Ok("""<script id="__NEXT_DATA__" type="application/json">{not json</script>"""u8.ToArray()));

    [Fact]
    public Task ParseFailedWithoutTrackList() =>
        ExpectError(SourceErrorKind.ParseFailed, "no entity with a trackList array in __NEXT_DATA__",
            SourceFakeHandler.Ok("""<script id="__NEXT_DATA__" type="application/json">{"props":{"pageProps":{"state":{"data":{"entity":{"name":"X","trackList":"nope"}}}}}}</script>"""u8.ToArray()));

    [Fact]
    public Task ParseFailedWithoutName() =>
        ExpectError(SourceErrorKind.ParseFailed, "entity has no name or title",
            SourceFakeHandler.Ok(SourceFixtures.Page(SourceFixtures.Item(IdA), name: null)));

    [Fact]
    public Task ParseFailedWhenEveryItemIsUnreadable() =>
        ExpectError(SourceErrorKind.ParseFailed, "no trackList item had a readable uri, title and subtitle",
            SourceFakeHandler.Ok(SourceFixtures.Page($$"""{"name":"x"},{"uri":"spotify:track:{{IdA}}"},3""")));

    [Fact]
    public Task EmptyWhenTrackListIsEmpty() =>
        ExpectError(SourceErrorKind.Empty, "Empty", SourceFakeHandler.Ok(SourceFixtures.Page("")));

    [Fact]
    public Task EmptyWhenNothingIsPlayable() =>
        ExpectError(SourceErrorKind.Empty, "Empty", SourceFakeHandler.Ok(SourceFixtures.Page(string.Join(",",
            SourceFixtures.Item(IdA, isPlayable: false),
            SourceFixtures.Item(IdB, uriKind: "episode")))));
}
