using Notchle.Core;

namespace Notchle.Core.Tests;

public class ContractTests
{
    [Fact]
    public void EmbedUrlMatchesSwiftContract() =>
        Assert.Equal("https://open.spotify.com/embed/playlist/37i9dQZF1DXcBWIGoYBM5M",
            new SourceRef(SourceKind.Playlist, "37i9dQZF1DXcBWIGoYBM5M").EmbedUrl.ToString());

    [Fact]
    public void SwiftFixturesAreReachable() =>
        Assert.True(File.Exists(Fixtures.Path("embed-playlist-todays-top-hits.html")));
}

/// The saved Spotify pages live with the Swift tests; both suites read the same files.
public static class Fixtures
{
    public static string Path(string name, [System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        System.IO.Path.GetFullPath(System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(here)!, "..", "..", "..", "Tests", "NotchleCoreTests", "Fixtures", name));
}
