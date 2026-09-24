using Notchle.Core;
using Notchle.Core.App;

namespace Notchle.Core.Tests;

/// The coordinator's side of the history: RecordOutcome → stamped entry, persisted, published;
/// loaded on launch; kept by Reset progress; ClearHistory; album covers resolved only after the
/// outcome and never blocking the entry.
public class AppHistoryTests
{
    private static readonly Track T1 = new("t1", "spotify:track:t1", "x", ["y"], 1, null);
    private static readonly Track T2 = new("t2", "spotify:track:t2", "q", ["r"], 1, null);
    private static readonly SourceRef Ref = new(SourceKind.Playlist, "p1");
    private static readonly Uri Cover = new("https://image-cdn-fa.spotifycdn.com/image/cover");

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 24, 14, 5, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// Lookups wait until the test answers them.
    private sealed class GatedResolver : IArtworkResolver
    {
        public List<(string TrackId, TaskCompletionSource<Uri?> Answer)> Calls { get; } = [];

        public Task<Uri?> ResolveAsync(string trackId, CancellationToken cancellationToken = default)
        {
            var tcs = new TaskCompletionSource<Uri?>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (Calls) Calls.Add((trackId, tcs));
            return tcs.Task;
        }

        public int Count { get { lock (Calls) return Calls.Count; } }
        public void Answer(int i, Uri? url) { lock (Calls) Calls[i].Answer.SetResult(url); }
        public void Fail(int i) { lock (Calls) Calls[i].Answer.SetException(new HttpRequestException("offline")); }
    }

    private sealed class Harness
    {
        public readonly AppGatedSource Source = new();
        public readonly AppMemoryStore Store;
        public readonly InMemoryHistoryPersistence History;
        public readonly ManualClock Clock = new();
        public readonly GatedResolver Artwork = new();
        public readonly List<IReadOnlyList<HistoryEntry>> Published = [];
        public readonly List<Uri?> Covers = [];
        public readonly AppCoordinator Coordinator;

        public Harness(IReadOnlyList<HistoryEntry>? initial = null, bool artwork = true)
        {
            Store = new AppMemoryStore();
            History = new InMemoryHistoryPersistence(initial);
            Coordinator = new AppCoordinator(Source, Store,
                _ => new AppRecordingPlayer { CompleteSnippetAfterSeconds = 0.01 }, _ => { }, null, null, 1,
                History, Clock, artwork ? Artwork : null);
            Coordinator.HistoryChanged += h => { lock (Published) Published.Add(h); };
            Coordinator.CurrentArtworkChanged += u => { lock (Covers) Covers.Add(u); };
        }

        public async Task Load(params Track[] tracks)
        {
            Coordinator.Send(new GameAction.Load(Ref));
            await WaitUntil(() => Source.Calls.Count == 1);
            Source.Calls.Single().Result.SetResult(new SourceListing(Ref, "Road trip", tracks));
            await Coordinator.DrainAsync();
        }

        public void Right() => Coordinator.Send(new GameAction.Submit(new Guess(Coordinator.State.CurrentTrack!.Title,
            Coordinator.State.CurrentTrack!.Artists[0])));
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

    [Fact]
    public async Task CorrectAppendsAStampedEntryAndPublishesIt()
    {
        var h = new Harness(artwork: false);
        await h.Load(T1);
        h.Coordinator.Send(new GameAction.Submit(new Guess("wrong", "y")));
        h.Coordinator.Send(new GameAction.Retry());
        h.Clock.Now = h.Clock.Now.AddSeconds(30);
        h.Right();

        var entry = Assert.Single(h.Coordinator.History);
        Assert.Equal(
            new HistoryEntry(entry.Id, h.Clock.Now, "t1", "x", ["y"], "Road trip", Ref, true, 1, 1, 0, null),
            entry);
        Assert.Equal([entry], h.History.Saved);
        Assert.Equal([entry], Assert.Single(h.Published));
    }

    [Fact]
    public async Task GiveUpRecordsAMissAndOnlyOnce()
    {
        var h = new Harness(artwork: false);
        await h.Load(T1, T2);
        var first = h.Coordinator.State.CurrentTrack!;
        h.Coordinator.Send(new GameAction.Skip());
        h.Coordinator.Send(new GameAction.GiveUp());
        h.Coordinator.Send(new GameAction.Restart());
        await h.Coordinator.DrainAsync();
        var entry = Assert.Single(h.Coordinator.History);
        Assert.Equal((first.Id, false, (int?)null, 0, 1), (entry.TrackId, entry.Correct, entry.TierIndex, entry.WrongGuesses, entry.Skips));
        h.Coordinator.Send(new GameAction.Next());
        await h.Coordinator.DrainAsync();
        Assert.Single(h.Coordinator.History);
    }

    [Fact]
    public void HistoryIsLoadedOnLaunch()
    {
        var old = new HistoryEntry(Guid.NewGuid(), DateTimeOffset.UnixEpoch, "t9", "Old", ["A"], "L", null, true, 0, 0, 0);
        var h = new Harness([old]);
        Assert.Equal([old], h.Coordinator.History);
    }

    [Fact]
    public async Task ResetProgressKeepsTheHistoryAndClearHistoryEmptiesIt()
    {
        var h = new Harness(artwork: false);
        await h.Load(T1);
        h.Right();
        h.Coordinator.ResetProgress();
        Assert.Single(h.Coordinator.History);
        Assert.Single(h.History.Saved);

        h.Coordinator.ClearHistory();
        Assert.Empty(h.Coordinator.History);
        Assert.Empty(h.History.Saved);
        Assert.Empty(h.Published[^1]);
        Assert.NotEmpty(h.Store.Saved); // progress untouched by clearing
    }

    [Fact]
    public async Task TheCoverIsLookedUpOnlyOnceTheOutcomeIsDecided()
    {
        var h = new Harness();
        await h.Load(T1, T2);
        h.Coordinator.Send(new GameAction.SnippetFinished());
        h.Coordinator.Send(new GameAction.Submit(new Guess("nope", "nope")));
        h.Coordinator.Send(new GameAction.Retry());
        await h.Coordinator.DrainAsync();
        Assert.Equal(0, h.Artwork.Count); // playing, guessing, wrong: never

        var track = h.Coordinator.State.CurrentTrack!;
        h.Right();
        await WaitUntil(() => h.Artwork.Count == 1); // the lookup runs off the lock
        Assert.Equal(track.Id, h.Artwork.Calls[0].TrackId);
        // Recorded right away, without the cover.
        Assert.Null(Assert.Single(h.Coordinator.History).ArtworkUrl);
        Assert.Null(h.Coordinator.CurrentArtworkUrl);

        h.Artwork.Answer(0, Cover);
        await h.Coordinator.DrainAsync();
        Assert.Equal(Cover, Assert.Single(h.Coordinator.History).ArtworkUrl);
        Assert.Equal(Cover, Assert.Single(h.History.Saved).ArtworkUrl);
        Assert.Equal(Cover, h.Published[^1][0].ArtworkUrl);
        Assert.Equal(Cover, h.Coordinator.CurrentArtworkUrl);
        Assert.Equal([Cover], h.Covers);

        // Next track: the cover goes with the answer screen.
        h.Coordinator.Send(new GameAction.Next());
        Assert.Null(h.Coordinator.CurrentArtworkUrl);
        Assert.Equal([Cover, null], h.Covers);
    }

    [Fact]
    public async Task ALateCoverNeverShowsOnTheNextTrackButStillFillsTheEntry()
    {
        var h = new Harness();
        await h.Load(T1, T2);
        h.Coordinator.Send(new GameAction.GiveUp());
        h.Coordinator.Send(new GameAction.Next()); // now guessing track 2
        await WaitUntil(() => h.Artwork.Count == 1);
        h.Artwork.Answer(0, Cover);
        await h.Coordinator.DrainAsync();
        Assert.Null(h.Coordinator.CurrentArtworkUrl);
        Assert.Empty(h.Covers);
        Assert.Equal(Cover, Assert.Single(h.Coordinator.History).ArtworkUrl);
    }

    [Fact]
    public async Task AFailedLookupLeavesTheEntryWithoutACover()
    {
        var h = new Harness();
        await h.Load(T1);
        h.Right();
        await WaitUntil(() => h.Artwork.Count == 1);
        h.Artwork.Fail(0);
        await h.Coordinator.DrainAsync();
        var entry = Assert.Single(h.Coordinator.History);
        Assert.Null(entry.ArtworkUrl);
        Assert.Null(h.Coordinator.CurrentArtworkUrl);
        Assert.Single(h.Published); // just the append
    }

    [Fact]
    public async Task ACoverForAClearedEntryIsDropped()
    {
        var h = new Harness();
        await h.Load(T1);
        h.Right();
        h.Coordinator.ClearHistory();
        await WaitUntil(() => h.Artwork.Count == 1);
        h.Artwork.Answer(0, Cover);
        await h.Coordinator.DrainAsync();
        Assert.Empty(h.Coordinator.History);
        Assert.Equal(Cover, h.Coordinator.CurrentArtworkUrl); // the answer still shows
    }
}
