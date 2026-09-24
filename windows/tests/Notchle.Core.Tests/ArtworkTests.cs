using System.Net;
using System.Runtime.CompilerServices;
using Notchle.Core;
using Notchle.Core.Ui;

namespace Notchle.Core.Tests;

/// Album covers: the oEmbed parser (saved fixture, no network), the resolver's cache and
/// failures (fake handler), and the spoiler gate.
public class ArtworkTests
{
    private static string Fixture(string name, [CallerFilePath] string here = "") =>
        File.ReadAllText(Path.Combine(Path.GetDirectoryName(here)!, "Fixtures", name));

    private static readonly Uri Cover = new("https://image-cdn-fa.spotifycdn.com/image/ab67616d00001e0240e583b55bdddcf70516fa6c");

    /// Answers every request from a function and records it. No network.
    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests) Requests.Add(request.RequestUri!);
            return Task.FromResult(answer(request));
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body) };

    [Fact]
    public void ParsesTheThumbnailOfASavedOEmbedResponse() =>
        Assert.Equal(Cover, ArtworkResolver.ParseOEmbed(Fixture("oembed-track.json")));

    [Theory]
    [InlineData("")]
    [InlineData("nope")]
    [InlineData("[]")]
    [InlineData("{\"title\":\"x\"}")]
    [InlineData("{\"thumbnail_url\":42}")]
    [InlineData("{\"thumbnail_url\":\"/relative.jpg\"}")]
    [InlineData("{\"thumbnail_url\":\"file:///c:/x.jpg\"}")]
    public void AnythingElseIsNoCover(string json) => Assert.Null(ArtworkResolver.ParseOEmbed(json));

    [Fact]
    public void OEmbedUrlWrapsTheTrackLink() =>
        Assert.Equal("https://open.spotify.com/oembed?url=https%3A%2F%2Fopen.spotify.com%2Ftrack%2Fabc123",
            ArtworkResolver.OEmbedUrl("abc123").AbsoluteUri);

    [Fact]
    public async Task ResolvesOnceThenServesFromTheCache()
    {
        var handler = new FakeHandler(_ => Json(Fixture("oembed-track.json")));
        var resolver = new ArtworkResolver(new HttpClient(handler));
        Assert.Equal(Cover, await resolver.ResolveAsync("abc123"));
        Assert.Equal(Cover, await resolver.ResolveAsync("abc123"));
        Assert.Equal([ArtworkResolver.OEmbedUrl("abc123")], handler.Requests);
    }

    [Fact]
    public async Task FailuresAreNullAndRetriedLater()
    {
        var calls = 0;
        var handler = new FakeHandler(_ => ++calls switch
        {
            1 => throw new HttpRequestException("offline"),
            2 => Json("", HttpStatusCode.NotFound),
            3 => Json("garbage"),
            _ => Json(Fixture("oembed-track.json")),
        });
        var resolver = new ArtworkResolver(new HttpClient(handler));
        Assert.Null(await resolver.ResolveAsync("t"));
        Assert.Null(await resolver.ResolveAsync("t"));
        Assert.Null(await resolver.ResolveAsync("t"));
        Assert.Equal(Cover, await resolver.ResolveAsync("t"));
        Assert.Null(await resolver.ResolveAsync(" "));
        Assert.Equal(4, calls);
    }

    // MARK: Spoiler gate

    public static TheoryData<GamePhase> AllPhases => new(UiFixtures.AllPhases);

    [Theory]
    [MemberData(nameof(AllPhases))]
    public void TheCoverReachesOnlyTheAnswerScreens(GamePhase phase)
    {
        var state = UiFixtures.State(phase);
        var reveals = phase is GamePhase.Correct or GamePhase.Revealed;
        Assert.Equal(reveals ? Cover : null, IslandRules.RevealedArtwork(state, Cover));
        var screen = IslandScreens.Build(state, "Paper", "Kites", null, true, Cover);
        Assert.Equal(reveals, screen is IslandScreen.Answer { ArtworkUrl: not null });
        Assert.Equal(reveals, ContainsUri(screen, Cover));
    }

    [Theory]
    [MemberData(nameof(UiSecrecyTests.SecretPhases), MemberType = typeof(UiSecrecyTests))]
    public void SessionNeverPutsTheCoverOnAGuessScreen(GamePhase phase)
    {
        var session = new IslandSession(new UiManualClock());
        session.StateDidChange(null, UiFixtures.State(phase));
        Assert.False(ContainsUri(session.Screen(new AppSettings(), "p", true, artworkUrl: Cover), Cover));
    }

    private static bool ContainsUri(object screen, Uri uri) =>
        screen.GetType().GetProperties().Any(p => p.GetIndexParameters().Length == 0 && Equals(p.GetValue(screen), uri));
}
