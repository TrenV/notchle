using System.IO;
using Notchle.Core;
using Notchle.Core.App.Spotify;
using Notchle.Windows.Playback;

namespace Notchle.Windows.Tests;

// Windows only (CI). None of these start audio: a GitHub runner has no sound device.

public class PlaybackPreviewPlayerTests
{
    [Fact]
    public async Task TrackWithoutPreviewIsNoPreview()
    {
        using var player = new PreviewPlayer(volume: 0);
        var track = new Track("t", "spotify:track:t", "x", new[] { "y" }, 1000, PreviewUrl: null);

        var error = await Assert.ThrowsAsync<PlayerException>(() => player.PlaySnippetAsync(track, 0, 5, default));
        Assert.Equal(PlayerErrorKind.NoPreview, error.Kind);
    }

    [Fact]
    public async Task RestartOfTrackWithoutPreviewIsNoPreview()
    {
        using var player = new PreviewPlayer(volume: 0);
        var track = new Track("t", "spotify:track:t", "x", new[] { "y" }, 1000, PreviewUrl: null);

        var error = await Assert.ThrowsAsync<PlayerException>(() => player.RestartTrackAsync(track));
        Assert.Equal(PlayerErrorKind.NoPreview, error.Kind);
    }

    [Fact]
    public void DescribesItself()
    {
        using var player = new PreviewPlayer();
        Assert.Equal("30-second previews", player.DisplayName);
        Assert.False(player.PlaysFullTrack);
    }

    [Fact]
    public async Task ContinueWithoutClipFails()
    {
        using var player = new PreviewPlayer();
        await Assert.ThrowsAsync<PlayerException>(() => player.ContinuePlayingAsync());
        await player.StopAsync(); // no clip: a no-op, not a throw
    }
}

public class PlaybackPlayerFactoryTests
{
    [Fact]
    public void PreviewIsTheDefault()
    {
        Assert.IsType<PreviewPlayer>(PlayerFactory.Create(new AppSettings()));
        var connect = PlayerFactory.Create(new AppSettings { PlayerMode = PlayerMode.SpotifyConnect, SpotifyClientId = "cid" });
        Assert.IsType<SpotifyConnectPlayer>(connect);
        Assert.True(connect.PlaysFullTrack);
    }
}

public class PlaybackDpapiTokenStoreTests
{
    [Fact]
    public void RoundTripsAndNeverWritesTokensInTheClear()
    {
        var dir = Directory.CreateTempSubdirectory("notchle-dpapi-");
        try
        {
            var store = new DpapiTokenStore(Path.Combine(dir.FullName, "tokens.bin"));
            Assert.Null(store.Load());
            var tokens = new SpotifyTokens("access-SECRET", "refresh-SECRET", DateTimeOffset.UnixEpoch.AddDays(1), "scope");

            store.Save(tokens);

            Assert.Equal(tokens, store.Load());
            var raw = File.ReadAllBytes(store.FilePath);
            Assert.DoesNotContain("SECRET", System.Text.Encoding.UTF8.GetString(raw));
            Assert.DoesNotContain("SECRET", System.Text.Encoding.Unicode.GetString(raw));

            store.Save(null);
            Assert.False(File.Exists(store.FilePath));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void CorruptFileLoadsAsSignedOut()
    {
        var dir = Directory.CreateTempSubdirectory("notchle-dpapi-");
        try
        {
            var path = Path.Combine(dir.FullName, "tokens.bin");
            File.WriteAllText(path, "not dpapi");
            Assert.Null(new DpapiTokenStore(path).Load());
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
