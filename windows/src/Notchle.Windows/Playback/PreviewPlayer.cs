using Notchle.Core;
using Notchle.Core.App.Playback;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace Notchle.Windows.Playback;

/// Plays the ~30 s preview clip (Track.PreviewUrl) with the WinRT MediaPlayer. No login, no
/// Spotify app, and nothing in the Windows media flyout: the system media transport controls
/// integration (CommandManager) is switched off, because the flyout would show the answer.
///
/// Timing lives in Notchle.Core (SnippetTimer, unit-tested on any OS); this class only adapts
/// MediaPlayer to a position sampler.
public sealed class PreviewPlayer : IPlayer, IDisposable
{
    private readonly double _volume;
    private readonly IPlaybackClock _clock;
    private readonly SnippetTiming _timing;
    private MediaPlayer? _player;
    private volatile bool _ended;
    private volatile string? _failure;
    /// Bumped by every public operation, so a cancelled snippet's late cleanup can't pause a
    /// newer snippet or the song ContinuePlayingAsync just resumed.
    private int _generation;

    public PreviewPlayer(double volume = 1, IPlaybackClock? clock = null, SnippetTiming? timing = null)
    {
        _volume = Math.Clamp(volume, 0, 1);
        _clock = clock ?? SystemPlaybackClock.Instance;
        _timing = timing ?? SnippetTiming.Preview;
    }

    public string DisplayName => "30-second previews";
    public bool PlaysFullTrack => false;

    public async Task PlaySnippetAsync(Track track, double start, double seconds, CancellationToken cancellationToken)
    {
        if (track.PreviewUrl is not { } url) throw new PlayerException(PlayerErrorKind.NoPreview);
        var myGeneration = Interlocked.Increment(ref _generation);
        var player = MediaPlayer();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            player.Pause();
            var clipLength = await OpenAsync(player, url, cancellationToken).ConfigureAwait(false);
            var startAt = SnippetTimer.ClampedStart(start, seconds, clipLength);
            player.PlaybackSession.Position = TimeSpan.FromSeconds(startAt);
            player.Play();
            await SnippetTimer.WaitUntilAudioAdvancesAsync(Sample, startAt, _timing, _clock, "Preview clip", cancellationToken)
                .ConfigureAwait(false);
            await SnippetTimer.WaitUntilPlayedAsync(Sample, startAt + Math.Max(0, seconds), _timing, _clock, "Preview clip",
                cancellationToken).ConfigureAwait(false);
            PauseIfStill(myGeneration);
        }
        catch
        {
            PauseIfStill(myGeneration);
            throw;
        }
    }

    public Task ContinuePlayingAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _generation);
        if (_player is not { Source: not null } player)
            throw new PlayerException(PlayerErrorKind.Failed, "No preview clip loaded");
        player.Play();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        Interlocked.Increment(ref _generation);
        if (_player is { } player)
        {
            player.Pause();
            player.Source = null;
        }
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _player?.Dispose();
        _player = null;
    }

    // MARK: - Internals

    internal MediaPlayer MediaPlayer()
    {
        if (_player is { } existing) return existing;
        var created = CreateMediaPlayer(_volume);
        created.MediaEnded += (_, _) => _ended = true;
        created.MediaFailed += (_, e) => _failure = $"Preview clip failed: {e.ErrorMessage}";
        _player = created;
        return created;
    }

    /// A MediaPlayer that stays out of the system media controls (flyout, lock screen, media keys).
    internal static MediaPlayer CreateMediaPlayer(double volume) => new()
    {
        AutoPlay = false,
        IsLoopingEnabled = false,
        Volume = volume,
        CommandManager = { IsEnabled = false },
    };

    private Task<PlaybackSample> Sample(CancellationToken _)
    {
        var session = _player?.PlaybackSession;
        if (session is null) return Task.FromResult(new PlaybackSample(0, false, Failure: _failure ?? "Preview player closed"));
        return Task.FromResult(new PlaybackSample(
            session.Position.TotalSeconds,
            IsPlaying: session.PlaybackState == MediaPlaybackState.Playing,
            Ended: _ended,
            Failure: _failure));
    }

    /// Loads the clip and waits for MediaOpened; returns its length in seconds if known.
    private async Task<double?> OpenAsync(MediaPlayer player, Uri url, CancellationToken ct)
    {
        _ended = false;
        _failure = null;
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnOpened(MediaPlayer s, object e) => opened.TrySetResult();
        void OnFailed(MediaPlayer s, MediaPlayerFailedEventArgs e) =>
            opened.TrySetException(new PlayerException(PlayerErrorKind.Failed, $"Couldn't load the preview clip: {e.ErrorMessage}"));
        player.MediaOpened += OnOpened;
        player.MediaFailed += OnFailed;
        try
        {
            player.Source = MediaSource.CreateFromUri(url);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_timing.StartTimeout);
            try
            {
                await opened.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new PlayerException(PlayerErrorKind.Failed,
                    $"Preview clip didn't load within {_timing.StartTimeout.TotalSeconds:0} s");
            }
        }
        finally
        {
            player.MediaOpened -= OnOpened;
            player.MediaFailed -= OnFailed;
        }
        var length = player.PlaybackSession.NaturalDuration;
        return length > TimeSpan.Zero ? length.TotalSeconds : null;
    }

    private void PauseIfStill(int myGeneration)
    {
        if (Volatile.Read(ref _generation) != myGeneration) return;
        _player?.Pause();
    }
}
