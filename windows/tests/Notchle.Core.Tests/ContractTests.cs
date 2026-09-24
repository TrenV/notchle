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

public class ContractEqualityTests
{
    [Fact]
    public void GameConfigComparesTiersByValue() =>
        Assert.Equal(GameConfig.Default, new GameConfig(new List<double> { 5, 10, 15 }));

    [Fact]
    public void GameConfigWithDifferentTiersDiffers() =>
        Assert.NotEqual(GameConfig.Default, new GameConfig(new[] { 5.0, 10.0 }));

    [Fact]
    public void TrackComparesArtistsByValue() =>
        Assert.Equal(
            new Track("a", "spotify:track:a", "T", new[] { "X", "Y" }, 1, null),
            new Track("a", "spotify:track:a", "T", new List<string> { "X", "Y" }, 1, null));
}
