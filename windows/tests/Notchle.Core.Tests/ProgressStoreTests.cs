using System.Text.Json;
using Notchle.Core;

namespace Notchle.Core.Tests;

/// Mirrors Tests/NotchleCoreTests/ProgressStoreTests.swift, plus the cross-platform file shape.
public sealed class ProgressStoreTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("notchle-progress-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static void AssertSame(Progress expected, Progress actual)
    {
        // GameConfig holds its tiers in a list (reference equality), so compare it field by field.
        Assert.Equal(expected.Settings with { Config = GameConfig.Default }, actual.Settings with { Config = GameConfig.Default });
        Assert.Equal(expected.Settings.Config.Tiers, actual.Settings.Config.Tiers);
        Assert.Equal(expected.Settings.Config.SetSize, actual.Settings.Config.SetSize);
        Assert.Equal(expected.Settings.Config.SnippetStart, actual.Settings.Config.SnippetStart);
        Assert.Equal(expected.Settings.LastSource, actual.Settings.LastSource);
        Assert.Equal(expected.Settings.PlayerMode, actual.Settings.PlayerMode);
        Assert.Equal(expected.Settings.SpotifyClientId, actual.Settings.SpotifyClientId);
        Assert.True(expected.ClearedTrackIds.SetEquals(actual.ClearedTrackIds));
    }

    private static void AssertDefault(Progress actual) => AssertSame(new Progress(), actual);

    [Fact]
    public void RoundTripCreatesMissingDirectories()
    {
        var store = new ProgressStore(Path.Combine(_root, "a", "b", "Notchle"));
        var progress = new Progress
        {
            Settings = new AppSettings
            {
                PlayerMode = PlayerMode.SpotifyConnect,
                LastSource = new SourceRef(SourceKind.Album, "abc"),
                Config = new GameConfig([3, 6], 10, 12),
                SpotifyClientId = "client",
            },
            ClearedTrackIds = new HashSet<string> { "t2", "t1", "t3" },
        };

        store.Save(progress);
        AssertSame(progress, store.Load());
        AssertSame(progress, new ProgressStore(Path.GetDirectoryName(store.FilePath)!).Load());
    }

    [Fact]
    public void FileIsPrettyJsonWithSortedKeysAndIds()
    {
        var store = new ProgressStore(_root);
        store.Save(new Progress { ClearedTrackIds = new HashSet<string> { "b", "c", "a" } });
        var text = File.ReadAllText(store.FilePath);
        Assert.Contains("\n  ", text);
        using var json = JsonDocument.Parse(text);
        Assert.Equal(["a", "b", "c"], json.RootElement.GetProperty("clearedTrackIDs").Strings());
        Assert.Equal(["clearedTrackIDs", "settings"], json.RootElement.EnumerateObject().Select(p => p.Name));
        var settings = json.RootElement.GetProperty("settings");
        Assert.Equal(["config", "playerMode"], settings.EnumerateObject().Select(p => p.Name)); // nulls omitted
        Assert.Equal(["setSize", "snippetStart", "tiers"], settings.GetProperty("config").EnumerateObject().Select(p => p.Name));
        Assert.Equal("preview", settings.GetProperty("playerMode").GetString());
    }

    [Fact]
    public void ReadsAFileWrittenByTheSwiftApp()
    {
        // What Sources/NotchleCore/Engine/ProgressStore.swift writes. "spotifyApp" is a macOS-only
        // mode, so it loads as this platform's default; everything else carries over.
        var store = new ProgressStore(_root);
        File.WriteAllText(store.FilePath, """
            {
              "clearedTrackIDs" : [
                "a",
                "b"
              ],
              "settings" : {
                "config" : {
                  "setSize" : 10,
                  "snippetStart" : 12,
                  "tiers" : [
                    3,
                    6.5
                  ]
                },
                "lastSource" : {
                  "id" : "37i9dQZF1DXcBWIGoYBM5M",
                  "kind" : "playlist"
                },
                "playerMode" : "spotifyApp"
              }
            }
            """);
        var progress = store.Load();
        Assert.True(progress.ClearedTrackIds.SetEquals(["a", "b"]));
        Assert.Equal(new SourceRef(SourceKind.Playlist, "37i9dQZF1DXcBWIGoYBM5M"), progress.Settings.LastSource);
        Assert.Equal([3, 6.5], progress.Settings.Config.Tiers);
        Assert.Equal(10, progress.Settings.Config.SetSize);
        Assert.Equal(12, progress.Settings.Config.SnippetStart);
        Assert.Equal(PlayerMode.Preview, progress.Settings.PlayerMode);
    }

    [Fact]
    public void ReadsTheSwiftAppsSpotifyConnectMode()
    {
        // The macOS app's PlayerMode.spotifyConnect (raw value "spotifyConnect") is the same
        // player as this port's SpotifyConnect, so it carries over instead of falling back.
        var store = new ProgressStore(_root);
        File.WriteAllText(store.FilePath, """
            {
              "clearedTrackIDs" : [],
              "settings" : {
                "config" : { "setSize" : 20, "snippetStart" : 30, "tiers" : [1, 2] },
                "playerMode" : "spotifyConnect"
              }
            }
            """);
        Assert.Equal(PlayerMode.SpotifyConnect, store.Load().Settings.PlayerMode);
    }

    [Fact]
    public void SaveOverwritesPreviousFileAndLeavesNoTempFiles()
    {
        var store = new ProgressStore(_root);
        store.Save(new Progress { ClearedTrackIds = new HashSet<string> { "old" } });
        store.Save(new Progress { ClearedTrackIds = new HashSet<string> { "new" } });
        Assert.True(store.Load().ClearedTrackIds.SetEquals(["new"]));
        Assert.Equal([store.FilePath], Directory.GetFiles(_root));
    }

    [Fact]
    public void MissingFileLoadsDefault()
    {
        var store = new ProgressStore(Path.Combine(_root, "never-created"));
        Assert.False(File.Exists(store.FilePath));
        AssertDefault(store.Load());
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"clearedTrackIDs\": 5}")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"clearedTrackIDs\": [], \"settings\": {\"playerMode\": \"preview\"}}")]
    [InlineData("{\"clearedTrackIDs\": [null], \"settings\": {\"playerMode\": \"preview\", \"config\": {\"setSize\": 20, \"snippetStart\": 0, \"tiers\": [5]}}}")]
    [InlineData("{\"clearedTrackIDs\": [], \"settings\": {\"playerMode\": \"preview\", \"lastSource\": {\"kind\": \"track\", \"id\": \"x\"}, \"config\": {\"setSize\": 20, \"snippetStart\": 0, \"tiers\": [5]}}}")]
    public void CorruptFileLoadsDefault(string contents)
    {
        var store = new ProgressStore(_root);
        File.WriteAllText(store.FilePath, contents);
        AssertDefault(store.Load());
    }

    [Fact]
    public void UnreadablePathLoadsDefault()
    {
        // progress.json is a directory: reading it throws, Load must not.
        var store = new ProgressStore(_root);
        Directory.CreateDirectory(store.FilePath);
        AssertDefault(store.Load());
    }
}
