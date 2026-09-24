using System.Diagnostics;
using System.Security.Cryptography;

namespace Notchle.Core.App;

/// Runs the game: feeds actions into the pure <see cref="GameEngine"/>, publishes the new state,
/// and executes the engine's effects against the track source, the player and the store.
/// Port of Sources/Notchle/AppCoordinator.swift.
///
/// Threading: every public method may be called from any thread; engine state is guarded by one
/// lock. Callbacks (<c>publishState</c>, <see cref="SettingsChanged"/>, <see cref="PlayerChanged"/>)
/// are posted, in order, to the <see cref="SynchronizationContext"/> passed in (the WPF dispatcher's),
/// or invoked inline on the producing thread when none is given (tests).
public sealed class AppCoordinator
{
    private readonly object _gate = new();
    private readonly ITrackSource _source;
    private readonly IProgressPersistence _store;
    private readonly Func<AppSettings, IPlayer> _makePlayer;
    private readonly Action<GameState> _publishState;
    private readonly SynchronizationContext? _context;
    private readonly IAnswerJudge _judge;
    private readonly Func<ulong> _nextSeed;

    private GameEngine _engine;
    private AppSettings _settings;
    private IPlayer _player;

    /// Playback operations run strictly one after another. A new operation cancels the one in
    /// flight (a snippet, typically) and waits for it to wind down before starting, so a
    /// "continue playing" or a restart can never race ahead of the snippet's own pause.
    private Task _playbackTask = Task.CompletedTask;
    private CancellationTokenSource _playbackCts = new();
    private CancellationTokenSource? _fetchCts;
    private Task _fetchTask = Task.CompletedTask;

    /// Raised (on the context) after settings change, including LastSource on every Fetch.
    public event Action<AppSettings>? SettingsChanged;
    /// Raised (on the context) after the player was swapped for a new PlayerMode.
    public event Action<IPlayer>? PlayerChanged;

    /// Test hook: every action as it enters the engine, on the thread that sent it.
    internal event Action<GameAction>? ActionSent;

    /// <param name="makePlayer">Builds the player for the given settings (their PlayerMode).</param>
    /// <param name="publishState">Receives every new state, on <paramref name="context"/>.</param>
    public AppCoordinator(
        ITrackSource source,
        ProgressStore store,
        Func<AppSettings, IPlayer> makePlayer,
        Action<GameState> publishState,
        SynchronizationContext? context = null,
        IAnswerJudge? judge = null,
        ulong? seed = null)
        : this(source, new ProgressStorePersistence(store), makePlayer, publishState, context, judge, seed)
    {
    }

    internal AppCoordinator(
        ITrackSource source,
        IProgressPersistence store,
        Func<AppSettings, IPlayer> makePlayer,
        Action<GameState> publishState,
        SynchronizationContext? context = null,
        IAnswerJudge? judge = null,
        ulong? seed = null)
    {
        _source = source;
        _store = store;
        _makePlayer = makePlayer;
        _publishState = publishState;
        _context = context;
        _judge = judge ?? new FuzzyAnswerJudge();
        _nextSeed = seed is { } fixedSeed ? () => fixedSeed : RandomSeed;

        var progress = store.Load();
        _settings = progress.Settings;
        _engine = new GameEngine(_settings.Config, progress.ClearedTrackIds, _judge, _nextSeed());
        _player = makePlayer(_settings);
    }

    public GameState State { get { lock (_gate) return _engine.State; } }
    public AppSettings Settings { get { lock (_gate) return _settings; } }
    public IPlayer Player { get { lock (_gate) return _player; } }

    public void Send(GameAction action)
    {
        lock (_gate) SendLocked(action);
    }

    /// Clears the list of cleared songs, so every track can come back.
    public void ResetProgress()
    {
        lock (_gate)
        {
            SendLocked(new GameAction.Reset());
            _engine = new GameEngine(_settings.Config, new HashSet<string>(), _judge, _nextSeed());
            Publish(_engine.State);
            Save();
        }
    }

    /// Applies new settings: a new player mode swaps (and stops) the player, a new config is
    /// sent to the engine as Configure. Always persisted.
    public void UpdateSettings(AppSettings updated)
    {
        lock (_gate)
        {
            var old = _settings;
            _settings = updated;
            PublishSettings(updated);
            if (NeedsNewPlayer(old, updated))
            {
                var oldPlayer = _player;
                // Through the queue: the running snippet is cancelled and paused before the old
                // player is stopped, and nothing the old player does can overlap the new one.
                EnqueuePlayback(async (p, _) =>
                {
                    await p.StopAsync().ConfigureAwait(false);
                    DisposeQuietly(p);
                }, oldPlayer);
                _player = _makePlayer(updated);
                PublishPlayer(_player);
            }
            if (!ConfigEquals(old.Config, updated.Config))
                SendLocked(new GameAction.Configure(updated.Config));
            Save();
        }
    }

    /// Stops playback and waits for it (quit). Never throws.
    public async Task ShutdownAsync()
    {
        Task stopped;
        lock (_gate)
        {
            _fetchCts?.Cancel();
            stopped = EnqueuePlayback((p, _) => p.StopAsync());
        }
        try { await stopped.ConfigureAwait(false); } catch { /* best effort */ }
    }

    // MARK: - Effects

    /// Runs one effect directly, bypassing the engine (tests).
    internal void RunForTesting(GameEffect effect)
    {
        lock (_gate) Run(effect);
    }

    /// Waits until every queued playback operation and the latest fetch have finished (tests).
    internal async Task DrainAsync()
    {
        while (true)
        {
            Task playback, fetch;
            lock (_gate) { playback = _playbackTask; fetch = _fetchTask; }
            await Task.WhenAll(playback, fetch).ConfigureAwait(false);
            lock (_gate)
            {
                if (ReferenceEquals(playback, _playbackTask) && ReferenceEquals(fetch, _fetchTask)) return;
            }
        }
    }

    private void SendLocked(GameAction action)
    {
        ActionSent?.Invoke(action);
        var effects = _engine.Send(action);
        Publish(_engine.State);
        foreach (var effect in effects) Run(effect);
    }

    private void Run(GameEffect effect)
    {
        switch (effect)
        {
            case GameEffect.Fetch fetch:
                _settings = _settings with { LastSource = fetch.Ref };
                PublishSettings(_settings);
                Save();
                _fetchCts?.Cancel();
                var cts = new CancellationTokenSource();
                _fetchCts = cts;
                _fetchTask = Task.Run(async () =>
                {
                    GameAction? result;
                    try
                    {
                        var listing = await _source.GetListingAsync(fetch.Ref, cts.Token).ConfigureAwait(false);
                        result = new GameAction.Loaded(listing);
                    }
                    catch (Exception error)
                    {
                        result = new GameAction.LoadFailed(AppMessages.Describe(error));
                    }
                    lock (_gate)
                    {
                        if (!cts.IsCancellationRequested) SendLocked(result);
                    }
                });
                break;

            case GameEffect.PlaySnippet snippet:
                EnqueuePlayback(async (player, token) =>
                {
                    GameAction? result;
                    try
                    {
                        await player.PlaySnippetAsync(snippet.Track, snippet.Start, snippet.Seconds, token)
                            .ConfigureAwait(false);
                        result = new GameAction.SnippetFinished();
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        return; // Superseded by a guess, a skip or a new snippet.
                    }
                    catch (Exception error)
                    {
                        result = new GameAction.PlaybackFailed(AppMessages.Describe(error));
                    }
                    lock (_gate)
                    {
                        // Checked under the lock: a cancel (always taken under the lock) either
                        // happened before this, or happens after the engine got the result.
                        if (!token.IsCancellationRequested) SendLocked(result);
                    }
                });
                break;

            case GameEffect.ContinuePlaying:
                EnqueuePlayback(async (player, token) =>
                {
                    try { await player.ContinuePlayingAsync(token).ConfigureAwait(false); }
                    catch (Exception error) { Trace.TraceWarning($"Notchle: continue playing failed: {error.Message}"); }
                });
                break;

            case GameEffect.RestartTrack restart:
                // Through the queue like everything else: the running snippet is cancelled and
                // has paused before the song starts over, so its pause can't cut the restart off.
                EnqueuePlayback(async (player, token) =>
                {
                    try { await player.RestartTrackAsync(restart.Track, token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { /* superseded */ }
                    catch (Exception error) { Trace.TraceWarning($"Notchle: restarting the song failed: {error.Message}"); }
                });
                break;

            case GameEffect.Stop:
                EnqueuePlayback((player, _) => player.StopAsync());
                break;

            case GameEffect.PersistProgress:
                Save();
                break;
        }
    }

    /// Must be called under the lock. Cancels the running operation, then runs
    /// <paramref name="operation"/> once that one has completed. Operations never throw.
    private Task EnqueuePlayback(Func<IPlayer, CancellationToken, Task> operation, IPlayer? on = null)
    {
        var previous = _playbackTask;
        _playbackCts.Cancel();
        var cts = new CancellationTokenSource();
        _playbackCts = cts;
        var player = on ?? _player;
        _playbackTask = Task.Run(async () =>
        {
            await previous.ConfigureAwait(false);
            try
            {
                await operation(player, cts.Token).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                Trace.TraceWarning($"Notchle: playback operation failed: {error.Message}");
            }
        });
        return _playbackTask;
    }

    // MARK: - Settings and persistence

    private static bool NeedsNewPlayer(AppSettings old, AppSettings updated) =>
        old.PlayerMode != updated.PlayerMode
        || (updated.PlayerMode == PlayerMode.SpotifyConnect && old.SpotifyClientId != updated.SpotifyClientId);

    /// GameConfig is a record over an IReadOnlyList, so its own Equals compares the list by
    /// reference; a config read back from JSON would always look "changed".
    internal static bool ConfigEquals(GameConfig a, GameConfig b) =>
        a.SetSize == b.SetSize
        && a.SnippetStart.Equals(b.SnippetStart)
        && a.Tiers.SequenceEqual(b.Tiers);

    private void Save()
    {
        try
        {
            _store.Save(new Progress { Settings = _settings, ClearedTrackIds = _engine.State.ClearedTrackIds });
        }
        catch (Exception error)
        {
            Trace.TraceError($"Notchle: saving progress failed: {error}");
        }
    }

    private static void DisposeQuietly(IPlayer player)
    {
        try { (player as IDisposable)?.Dispose(); } catch { /* best effort */ }
    }

    private static ulong RandomSeed() => BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8));

    // MARK: - Publishing (always in production order)

    private void Publish(GameState state) => Dispatch(() => _publishState(state));
    private void PublishSettings(AppSettings settings) => Dispatch(() => SettingsChanged?.Invoke(settings));
    private void PublishPlayer(IPlayer player) => Dispatch(() => PlayerChanged?.Invoke(player));

    private void Dispatch(Action callback)
    {
        // Always Post (FIFO), never "inline if already on the UI thread": that would let a
        // newer state overtake an older one still queued, leaving the UI on the stale one.
        if (_context is null) callback();
        else _context.Post(static c => ((Action)c!)(), callback);
    }
}

/// Persistence seam: ProgressStore in production, an in-memory fake in tests.
internal interface IProgressPersistence
{
    Progress Load();
    void Save(Progress progress);
}

internal sealed class ProgressStorePersistence(ProgressStore store) : IProgressPersistence
{
    public Progress Load() => store.Load();
    public void Save(Progress progress) => store.Save(progress);
}
