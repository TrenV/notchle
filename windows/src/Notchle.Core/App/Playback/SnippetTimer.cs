using System.Diagnostics;

namespace Notchle.Core.App.Playback;

/// One reading of a player's own position.
/// <param name="Position">Seconds into the track (or clip), as the player reports it.</param>
/// <param name="IsPlaying">The player says it is playing. Not proof that audio moves: both macOS
/// players reported "playing" ~250–300 ms before the position started advancing.</param>
/// <param name="Ended">The clip reached its end (the preview is only ~30 s).</param>
/// <param name="WrongTrack">The player is on a different track than the snippet's.</param>
/// <param name="Failure">A fatal problem the player reported (media failed, an ad is playing).</param>
public readonly record struct PlaybackSample(
    double Position,
    bool IsPlaying,
    bool Ended = false,
    bool WrongTrack = false,
    string? Failure = null);

public sealed record SnippetTiming
{
    /// How long playback gets to actually start moving.
    public TimeSpan StartTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(10);
    /// Longest single sleep while a snippet plays (bounds the overshoot).
    public double MaxSleepSlice { get; init; } = 0.02;
    /// A snippet fails if the position doesn't move for this long (network stall).
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(5);
    /// Stop this many seconds before the target, to absorb the pause command's own latency.
    public double EndLead { get; init; }
    /// Playback counts as started once the position is this far past the start.
    public double AudibleProgress { get; init; } = 0.001;
    /// While starting: re-seek when the position is further than this from the start (the
    /// player ignored the seek). Infinity disables re-seeking.
    public double PositionTolerance { get; init; } = double.PositiveInfinity;
    public TimeSpan ReseekInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    /// Local media player: cheap position reads, so poll fast and stop within a few ms.
    public static SnippetTiming Preview { get; } = new();

    /// Web API: every reading is an HTTP round trip (and counts against the rate limit).
    public static SnippetTiming SpotifyConnect { get; } = new()
    {
        StartTimeout = TimeSpan.FromSeconds(8),
        PollInterval = TimeSpan.FromMilliseconds(250),
        MaxSleepSlice = 0.5,
        StallTimeout = TimeSpan.FromSeconds(4),
        EndLead = 0.1,
        AudibleProgress = 0.05,
        PositionTolerance = 1.0,
        ReseekInterval = TimeSpan.FromMilliseconds(750),
    };
}

/// Monotonic time plus a cancellable sleep; faked in tests so timing runs in virtual time.
public interface IPlaybackClock
{
    TimeSpan Now { get; }
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public sealed class SystemPlaybackClock : IPlaybackClock
{
    public static SystemPlaybackClock Instance { get; } = new();
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    public TimeSpan Now => _watch.Elapsed;
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        Task.Delay(delay < TimeSpan.Zero ? TimeSpan.Zero : delay, cancellationToken);
}

/// Times snippets by the player's own position rather than the wall clock. A wall-clock sleep
/// started when the player says "playing" makes every snippet ~0.3 s short (measured on both
/// macOS players), so: wait until the position really moves, then until it reaches the target.
/// Cancellation surfaces as OperationCanceledException from the clock's delay.
public static class SnippetTimer
{
    private const double DoneTolerance = 0.0005;

    /// Where the snippet starts: <paramref name="start"/>, pulled back so <paramref name="seconds"/>
    /// fit in the clip if possible, never before 0 or past the end.
    public static double ClampedStart(double start, double seconds, double? clipLength)
    {
        var requested = double.IsFinite(start) ? Math.Max(0, start) : 0;
        if (clipLength is not { } length || !double.IsFinite(length) || length <= 0) return requested;
        return Math.Min(requested, Math.Max(0, length - Math.Max(0, seconds)));
    }

    /// Returns once the player is playing the right track and its position has moved past
    /// <paramref name="start"/>. <paramref name="what"/> names the player in error messages.
    public static async Task WaitUntilAudioAdvancesAsync(
        Func<CancellationToken, Task<PlaybackSample>> sample,
        double start,
        SnippetTiming timing,
        IPlaybackClock clock,
        string what,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task>? reseek = null)
    {
        var deadline = clock.Now + timing.StartTimeout;
        var lastSeek = clock.Now;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = await sample(cancellationToken).ConfigureAwait(false);
            if (current.Failure is { } failure) throw new PlayerException(PlayerErrorKind.Failed, failure);
            if (current.IsPlaying && !current.WrongTrack)
            {
                if (reseek is not null && Math.Abs(current.Position - start) > timing.PositionTolerance)
                {
                    // The player ignored the seek (sent before the track had loaded): seek again,
                    // but give each seek time to land before judging it.
                    if (clock.Now - lastSeek >= timing.ReseekInterval)
                    {
                        lastSeek = clock.Now;
                        await reseek(cancellationToken).ConfigureAwait(false);
                    }
                }
                else if (current.Position >= start + timing.AudibleProgress)
                {
                    return;
                }
            }
            if (clock.Now >= deadline)
            {
                throw new PlayerException(PlayerErrorKind.Failed, current.WrongTrack && current.IsPlaying
                    ? $"{what} played a different track"
                    : $"{what} didn't start playing within {timing.StartTimeout.TotalSeconds:0.#} s");
            }
            await clock.DelayAsync(timing.PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    /// Sleeps (cancellably) until the player's position reaches <paramref name="target"/> (minus
    /// EndLead), so the snippet is that much audio even across brief stalls. Returns early if the
    /// clip ends; fails if the position stops moving for StallTimeout or the track changes.
    public static async Task WaitUntilPlayedAsync(
        Func<CancellationToken, Task<PlaybackSample>> sample,
        double target,
        SnippetTiming timing,
        IPlaybackClock clock,
        string what,
        CancellationToken cancellationToken)
    {
        var lastPosition = double.NegativeInfinity;
        var lastProgress = clock.Now;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = await sample(cancellationToken).ConfigureAwait(false);
            if (current.Failure is { } failure) throw new PlayerException(PlayerErrorKind.Failed, failure);
            if (current.WrongTrack) throw new PlayerException(PlayerErrorKind.Failed, $"{what} played a different track");
            var remaining = target - current.Position;
            // Within half a millisecond is done: a shorter sleep rounds to zero and would spin.
            if (remaining <= timing.EndLead + DoneTolerance || current.Ended) return;
            if (current.Position > lastPosition)
            {
                lastPosition = current.Position;
                lastProgress = clock.Now;
            }
            else if (clock.Now - lastProgress > timing.StallTimeout)
            {
                throw new PlayerException(PlayerErrorKind.Failed, $"{what} stalled");
            }
            // Short slices near the end keep the overshoot to one slice (or one round trip).
            var sleep = Math.Min(remaining - timing.EndLead, timing.MaxSleepSlice);
            await clock.DelayAsync(TimeSpan.FromSeconds(sleep), cancellationToken).ConfigureAwait(false);
        }
    }
}
