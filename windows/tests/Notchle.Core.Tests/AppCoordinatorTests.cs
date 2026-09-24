using System.Collections.Concurrent;
using Notchle.Core;
using Notchle.Core.App;

namespace Notchle.Core.Tests;

/// Records the order of player calls. A snippet "plays" until cancelled, then logs its pause
/// after a delay, like a real player winding down.
public sealed class AppRecordingPlayer(string name = "fake", TimeSpan? pauseLatency = null) : IPlayer
{
    private readonly object _gate = new();
    private readonly List<string> _log = new();
    public string DisplayName { get; } = name;
    public bool PlaysFullTrack => true;
    public Exception? SnippetError { get; set; }
    public double? CompleteSnippetAfterSeconds { get; set; }

    public IReadOnlyList<string> Log { get { lock (_gate) return _log.ToList(); } }
    private void Append(string entry) { lock (_gate) _log.Add(entry); }

    public async Task PlaySnippetAsync(Track track, double start, double seconds, CancellationToken cancellationToken)
    {
        Append($"snippet:{track.Id}");
        if (SnippetError is { } error) throw error;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(CompleteSnippetAfterSeconds ?? seconds), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            await Task.Delay(pauseLatency ?? TimeSpan.FromMilliseconds(50), CancellationToken.None);
            Append($"paused:{track.Id}");
            throw;
        }
        Append($"finished:{track.Id}");
    }

    public Task ContinuePlayingAsync(CancellationToken cancellationToken = default)
    {
        Append("continue");
        return Task.CompletedTask;
    }

    public Task RestartTrackAsync(Track track, CancellationToken cancellationToken = default)
    {
        Append($"restart:{track.Id}");
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        Append("stop");
        return Task.CompletedTask;
    }
}

internal sealed class AppMemoryStore(Progress? initial = null) : IProgressPersistence
{
    public ConcurrentQueue<Progress> Saved { get; } = new();
    public bool FailSaves { get; set; }
    public Progress Load() => initial ?? new Progress();
    public void Save(Progress progress)
    {
        if (FailSaves) throw new IOException("disk full");
        Saved.Enqueue(progress);
    }
}

/// Source whose calls block until the test releases them.
internal sealed class AppGatedSource : ITrackSource
{
    public ConcurrentQueue<(SourceRef Ref, TaskCompletionSource<SourceListing> Result, CancellationToken Token)> Calls { get; } = new();

    public Task<SourceListing> GetListingAsync(SourceRef source, CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource<SourceListing>(TaskCreationOptions.RunContinuationsAsynchronously);
        Calls.Enqueue((source, tcs, cancellationToken));
        return tcs.Task;
    }
}

/// Queues posted callbacks; the test pumps them, like a UI thread would.
internal sealed class AppQueueContext : SynchronizationContext
{
    public ConcurrentQueue<(SendOrPostCallback Callback, object? State)> Posted { get; } = new();
    public override void Post(SendOrPostCallback d, object? state) => Posted.Enqueue((d, state));
    public override void Send(SendOrPostCallback d, object? state) => throw new InvalidOperationException("Send must not be used");
    public int Pump()
    {
        var count = 0;
        while (Posted.TryDequeue(out var item)) { item.Callback(item.State); count++; }
        return count;
    }
}

public class AppCoordinatorTests
{
    private static readonly Track T1 = new("t1", "spotify:track:t1", "x", new[] { "y" }, 1, null);
    private static readonly SourceRef Ref1 = new(SourceKind.Playlist, "p1");
    private static readonly SourceRef Ref2 = new(SourceKind.Album, "a2");

    private sealed class Harness
    {
        public readonly List<GameAction> Actions = new();
        public readonly List<GameState> Published = new();
        public readonly List<AppSettings> PublishedSettings = new();
        public readonly List<IPlayer> PublishedPlayers = new();
        public readonly List<AppSettings> PlayersMadeFor = new();
        public readonly AppMemoryStore Store;
        public readonly AppCoordinator Coordinator;

        public Harness(Func<AppSettings, IPlayer> makePlayer, ITrackSource? source = null, AppMemoryStore? store = null,
            SynchronizationContext? context = null)
        {
            Store = store ?? new AppMemoryStore();
            Coordinator = new AppCoordinator(source ?? new AppGatedSource(), Store,
                s => { lock (PlayersMadeFor) PlayersMadeFor.Add(s); return makePlayer(s); },
                s => { lock (Published) Published.Add(s); }, context, seed: 1);
            Coordinator.ActionSent += a => { lock (Actions) Actions.Add(a); };
            Coordinator.SettingsChanged += s => PublishedSettings.Add(s);
            Coordinator.PlayerChanged += p => PublishedPlayers.Add(p);
        }

        public List<T> ActionsOf<T>() { lock (Actions) return Actions.OfType<T>().ToList(); }
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("condition not met");
            await Task.Delay(5);
        }
    }

    /// Port of Tests/NotchleAppTests/CoordinatorPlaybackTests.swift.
    [Fact]
    public async Task ContinueWaitsForCancelledSnippetToPause()
    {
        var player = new AppRecordingPlayer();
        var h = new Harness(_ => player);

        h.Coordinator.RunForTesting(new GameEffect.PlaySnippet(T1, 0, 30));
        await WaitUntil(() => player.Log.Contains("snippet:t1"));
        h.Coordinator.RunForTesting(new GameEffect.ContinuePlaying());
        await h.Coordinator.DrainAsync();

        Assert.Equal(new[] { "snippet:t1", "paused:t1", "continue" }, player.Log);
    }

    /// A restart while a snippet plays: the snippet is cancelled and its (slow) pause lands
    /// before the song starts over; nothing pauses after the restart.
    [Fact]
    public async Task RestartWaitsForCancelledSnippetToPauseAndNothingPausesAfter()
    {
        var player = new AppRecordingPlayer(pauseLatency: TimeSpan.FromMilliseconds(150));
        var h = new Harness(_ => player);

        h.Coordinator.RunForTesting(new GameEffect.PlaySnippet(T1, 0, 30));
        await WaitUntil(() => player.Log.Contains("snippet:t1"));
        h.Coordinator.RunForTesting(new GameEffect.RestartTrack(T1));
        await h.Coordinator.DrainAsync();
        await Task.Delay(200); // a stray pause arriving late would show up here

        Assert.Equal(new[] { "snippet:t1", "paused:t1", "restart:t1" }, player.Log);
        Assert.Empty(h.ActionsOf<GameAction.SnippetFinished>());
        Assert.Empty(h.ActionsOf<GameAction.PlaybackFailed>());
    }

    /// End to end through the engine: Restart in Correct restarts the song on the player.
    [Fact]
    public async Task RestartInCorrectRestartsTheSongOnThePlayer()
    {
        var source = new AppGatedSource();
        var player = new AppRecordingPlayer { CompleteSnippetAfterSeconds = 0.01 };
        var h = new Harness(_ => player, source);

        h.Coordinator.Send(new GameAction.Load(Ref1));
        await WaitUntil(() => source.Calls.Count == 1);
        source.Calls.Single().Result.SetResult(new SourceListing(Ref1, "one", new[] { T1 }));
        await h.Coordinator.DrainAsync();
        h.Coordinator.Send(new GameAction.Submit(new Guess("x", "y")));
        Assert.IsType<GamePhase.Correct>(h.Coordinator.State.Phase);
        h.Coordinator.Send(new GameAction.Restart());
        await h.Coordinator.DrainAsync();

        Assert.Equal(new[] { "stop", "snippet:t1", "finished:t1", "continue", "restart:t1" }, player.Log);
        Assert.IsType<GamePhase.Correct>(h.Coordinator.State.Phase);
    }

    [Fact]
    public async Task FinishedSnippetReportsSnippetFinished()
    {
        var player = new AppRecordingPlayer { CompleteSnippetAfterSeconds = 0.01 };
        var h = new Harness(_ => player);

        h.Coordinator.RunForTesting(new GameEffect.PlaySnippet(T1, 0, 5));
        await h.Coordinator.DrainAsync();

        Assert.Equal(new[] { "snippet:t1", "finished:t1" }, player.Log);
        Assert.Single(h.ActionsOf<GameAction.SnippetFinished>());
    }

    [Fact]
    public async Task CancelledSnippetNeverReportsSnippetFinished()
    {
        var player = new AppRecordingPlayer();
        var h = new Harness(_ => player);

        h.Coordinator.RunForTesting(new GameEffect.PlaySnippet(T1, 0, 30));
        await WaitUntil(() => player.Log.Contains("snippet:t1"));
        h.Coordinator.RunForTesting(new GameEffect.Stop());
        await h.Coordinator.DrainAsync();

        Assert.Equal(new[] { "snippet:t1", "paused:t1", "stop" }, player.Log);
        Assert.Empty(h.ActionsOf<GameAction.SnippetFinished>());
        Assert.Empty(h.ActionsOf<GameAction.PlaybackFailed>());
    }

    [Theory]
    [InlineData(PlayerErrorKind.NoPreview, null,
        "This song has no preview clip. Skip it, or switch to Spotify Connect in settings.")]
    [InlineData(PlayerErrorKind.Unavailable, "Spotify isn't open on this PC",
        "Spotify isn't open on this PC. Switch to 30-second previews in settings.")]
    [InlineData(PlayerErrorKind.Failed, "Preview clip stalled", "Preview clip stalled")]
    public async Task FailedSnippetReportsPlaybackFailedWithHumanMessage(PlayerErrorKind kind, string? detail, string expected)
    {
        var player = new AppRecordingPlayer { SnippetError = new PlayerException(kind, detail) };
        var h = new Harness(_ => player);

        h.Coordinator.RunForTesting(new GameEffect.PlaySnippet(T1, 0, 5));
        await h.Coordinator.DrainAsync();

        Assert.Equal(expected, Assert.Single(h.ActionsOf<GameAction.PlaybackFailed>()).Message);
    }

    [Fact]
    public async Task NewFetchCancelsThePreviousOne()
    {
        var source = new AppGatedSource();
        var h = new Harness(_ => new AppRecordingPlayer(), source);
        var listing1 = new SourceListing(Ref1, "one", new[] { T1 });
        var listing2 = new SourceListing(Ref2, "two", new[] { T1 });

        h.Coordinator.RunForTesting(new GameEffect.Fetch(Ref1));
        await WaitUntil(() => source.Calls.Count == 1);
        h.Coordinator.RunForTesting(new GameEffect.Fetch(Ref2));
        await WaitUntil(() => source.Calls.Count == 2);
        var calls = source.Calls.ToArray();
        Assert.True(calls[0].Token.IsCancellationRequested);
        Assert.False(calls[1].Token.IsCancellationRequested);

        calls[1].Result.SetResult(listing2);
        calls[0].Result.SetResult(listing1); // late answer of the superseded fetch
        await h.Coordinator.DrainAsync();
        await Task.Delay(50);

        Assert.Equal("two", Assert.Single(h.ActionsOf<GameAction.Loaded>()).Listing.Name);
        Assert.Equal(Ref2, h.Coordinator.Settings.LastSource);
        Assert.Equal(Ref2, h.Store.Saved.Last().Settings.LastSource);
    }

    [Theory]
    [InlineData(SourceErrorKind.NotFound, null, "Spotify couldn't find that. Is it public?")]
    [InlineData(SourceErrorKind.Network, "timed out", "Couldn't reach Spotify: timed out")]
    [InlineData(SourceErrorKind.Empty, null, "No playable songs on that page.")]
    public async Task FailedFetchReportsLoadFailed(SourceErrorKind kind, string? detail, string expected)
    {
        var source = new AppGatedSource();
        var h = new Harness(_ => new AppRecordingPlayer(), source);

        h.Coordinator.RunForTesting(new GameEffect.Fetch(Ref1));
        await WaitUntil(() => source.Calls.Count == 1);
        source.Calls.Single().Result.SetException(new SourceException(kind, detail));
        await h.Coordinator.DrainAsync();

        Assert.Equal(expected, Assert.Single(h.ActionsOf<GameAction.LoadFailed>()).Message);
    }

    [Fact]
    public async Task PlayerSwapWaitsForSnippetToPauseThenStopsOldPlayer()
    {
        var preview = new AppRecordingPlayer("preview");
        var connect = new AppRecordingPlayer("connect");
        var h = new Harness(s => s.PlayerMode == PlayerMode.Preview ? preview : connect);

        h.Coordinator.RunForTesting(new GameEffect.PlaySnippet(T1, 0, 30));
        await WaitUntil(() => preview.Log.Contains("snippet:t1"));
        h.Coordinator.UpdateSettings(h.Coordinator.Settings with { PlayerMode = PlayerMode.SpotifyConnect, SpotifyClientId = "abc" });
        h.Coordinator.RunForTesting(new GameEffect.PlaySnippet(T1, 0, 0.01));
        await h.Coordinator.DrainAsync();

        Assert.Equal(new[] { "snippet:t1", "paused:t1", "stop" }, preview.Log);
        Assert.Equal(new[] { "snippet:t1", "finished:t1" }, connect.Log);
        Assert.Same(connect, h.Coordinator.Player);
        Assert.Same(connect, Assert.Single(h.PublishedPlayers));
        Assert.Equal(PlayerMode.SpotifyConnect, h.Store.Saved.Last().Settings.PlayerMode);
    }

    [Fact]
    public void ConfigChangeSendsConfigureAndSameConfigDoesNot()
    {
        var h = new Harness(_ => new AppRecordingPlayer());

        // Equal by value, different instance (as after a JSON round trip): not a change.
        h.Coordinator.UpdateSettings(h.Coordinator.Settings with { Config = new GameConfig(new[] { 5.0, 10.0, 15.0 }) });
        Assert.Empty(h.ActionsOf<GameAction.Configure>());
        Assert.Single(h.PlayersMadeFor); // no swap either

        var longer = new GameConfig(new[] { 5.0, 10.0, 20.0 });
        h.Coordinator.UpdateSettings(h.Coordinator.Settings with { Config = longer });
        Assert.Same(longer, Assert.Single(h.ActionsOf<GameAction.Configure>()).Config);
        Assert.Equal(2, h.Store.Saved.Count);
    }

    [Fact]
    public void ClientIdChangeRebuildsTheConnectPlayerOnly()
    {
        var h = new Harness(_ => new AppRecordingPlayer());
        h.Coordinator.UpdateSettings(h.Coordinator.Settings with { SpotifyClientId = "one" }); // still Preview
        Assert.Single(h.PlayersMadeFor);
        h.Coordinator.UpdateSettings(h.Coordinator.Settings with { PlayerMode = PlayerMode.SpotifyConnect });
        h.Coordinator.UpdateSettings(h.Coordinator.Settings with { SpotifyClientId = "two" });
        Assert.Equal(new[] { null, "one", "two" }, h.PlayersMadeFor.Select(s => s.SpotifyClientId));
    }

    [Fact]
    public void SettingsAndProgressComeFromTheStore()
    {
        var saved = new Progress
        {
            Settings = new AppSettings { PlayerMode = PlayerMode.SpotifyConnect, SpotifyClientId = "cid" },
            ClearedTrackIds = new HashSet<string> { "a", "b" },
        };
        var h = new Harness(_ => new AppRecordingPlayer(), store: new AppMemoryStore(saved));

        Assert.Equal(PlayerMode.SpotifyConnect, Assert.Single(h.PlayersMadeFor).PlayerMode);
        Assert.Equal(new[] { "a", "b" }, h.Coordinator.State.ClearedTrackIds.Order());
    }

    [Fact]
    public void ResetProgressForgetsClearedTracksAndSaves()
    {
        var saved = new Progress { ClearedTrackIds = new HashSet<string> { "a" } };
        var h = new Harness(_ => new AppRecordingPlayer(), store: new AppMemoryStore(saved));

        h.Coordinator.ResetProgress();

        Assert.Empty(h.Coordinator.State.ClearedTrackIds);
        Assert.Empty(h.Store.Saved.Last().ClearedTrackIds);
        Assert.IsType<GameAction.Reset>(h.ActionsOf<GameAction>().First());
    }

    [Fact]
    public void FailingStoreDoesNotBreakTheGame()
    {
        var h = new Harness(_ => new AppRecordingPlayer(), store: new AppMemoryStore { FailSaves = true });
        h.Coordinator.RunForTesting(new GameEffect.PersistProgress());
        h.Coordinator.ResetProgress();
    }

    [Fact]
    public async Task CallbacksArePostedToTheContextInOrder()
    {
        var context = new AppQueueContext();
        var source = new AppGatedSource();
        var h = new Harness(_ => new AppRecordingPlayer(), source, context: context);

        h.Coordinator.Send(new GameAction.Reset());
        h.Coordinator.RunForTesting(new GameEffect.Fetch(Ref1));
        await WaitUntil(() => source.Calls.Count == 1);
        Assert.Empty(h.Published);          // nothing ran inline
        Assert.Empty(h.PublishedSettings);

        Assert.Equal(2, context.Pump());   // state from Reset, settings from Fetch
        Assert.Single(h.Published);
        Assert.Equal(Ref1, Assert.Single(h.PublishedSettings).LastSource);
    }

    [Fact]
    public async Task ShutdownStopsThePlayer()
    {
        var player = new AppRecordingPlayer();
        var h = new Harness(_ => player);
        h.Coordinator.RunForTesting(new GameEffect.PlaySnippet(T1, 0, 30));
        await WaitUntil(() => player.Log.Contains("snippet:t1"));

        await h.Coordinator.ShutdownAsync();

        Assert.Equal(new[] { "snippet:t1", "paused:t1", "stop" }, player.Log);
    }

    [Fact]
    public void ConfigEqualityIsByValue()
    {
        Assert.True(AppCoordinator.ConfigEquals(GameConfig.Default, new GameConfig(new[] { 5.0, 10.0, 15.0 })));
        Assert.False(AppCoordinator.ConfigEquals(GameConfig.Default, GameConfig.Default with { SetSize = 10 }));
        Assert.False(AppCoordinator.ConfigEquals(GameConfig.Default, GameConfig.Default with { SnippetStart = 30 }));
    }
}

public class AppMessagesTests
{
    [Fact]
    public void DescribesEveryErrorKind()
    {
        Assert.StartsWith("Notchle isn't connected to Spotify.",
            AppMessages.Describe(new PlayerException(PlayerErrorKind.NotAuthorized)));
        Assert.Equal("That doesn't look like a Spotify playlist, album or artist link.",
            AppMessages.Describe(new SourceException(SourceErrorKind.InvalidUrl)));
        Assert.Equal("Couldn't read that Spotify page: no __NEXT_DATA__",
            AppMessages.Describe(new SourceException(SourceErrorKind.ParseFailed, "no __NEXT_DATA__")));
        Assert.Equal("boom", AppMessages.Describe(new InvalidOperationException("boom")));
    }
}

public class AppLaunchOptionsTests
{
    [Fact]
    public void ParsesTheDebugFlags()
    {
        Assert.Equal(LaunchMode.Game, LaunchOptions.Parse(Array.Empty<string>()).Mode);
        Assert.Equal(LaunchMode.UiDemo, LaunchOptions.Parse(new[] { "--ui-demo", "--ui-demo-auto" }).Mode);
        var snapshots = LaunchOptions.Parse(new[] { "--ui-snapshots", "out dir" });
        Assert.Equal((LaunchMode.UiSnapshots, "out dir"), (snapshots.Mode, snapshots.SnapshotDirectory));
        Assert.Throws<ArgumentException>(() => LaunchOptions.Parse(new[] { "--ui-snapshots" }));
        Assert.Throws<ArgumentException>(() => LaunchOptions.Parse(new[] { "--ui-snapshots", "--ui-demo" }));
    }
}
