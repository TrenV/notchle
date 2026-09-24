using Notchle.Core;
using Notchle.Core.App.Playback;

namespace Notchle.Core.Tests;

/// Virtual time: DelayAsync advances Now instantly, so timing tests are exact and fast.
public sealed class AppFakeClock : IPlaybackClock
{
    public TimeSpan Now { get; private set; }
    public List<TimeSpan> Delays { get; } = new();
    /// Called after every advance, e.g. to cancel a token at a given virtual time.
    public Action<TimeSpan>? OnAdvance { get; set; }

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Delays.Add(delay);
        Now += delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
        OnAdvance?.Invoke(Now);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}

/// A media player that says "playing" at once but whose position only starts moving after
/// `latency`, like both measured macOS players (~250–300 ms).
public sealed class AppFakeMedia(AppFakeClock clock, double latency, double clipLength = 30)
{
    public TimeSpan? PlayCalledAt { get; private set; }
    public double StartAt { get; set; }
    public TimeSpan? StallFrom { get; set; }
    public bool WrongTrack { get; set; }

    public void Play() => PlayCalledAt = clock.Now;

    public double Position
    {
        get
        {
            if (PlayCalledAt is not { } played) return StartAt;
            var until = StallFrom is { } stall && stall < clock.Now ? stall : clock.Now;
            var moving = (until - played).TotalSeconds - latency;
            return Math.Min(clipLength, StartAt + Math.Max(0, moving));
        }
    }

    public Task<PlaybackSample> Sample(CancellationToken _) => Task.FromResult(new PlaybackSample(
        Position, IsPlaying: PlayCalledAt is not null, Ended: Position >= clipLength, WrongTrack: WrongTrack));
}

public class AppSnippetTimerTests
{
    private static async Task<(double Played, TimeSpan Elapsed)> PlaySnippet(AppFakeClock clock, AppFakeMedia media,
        double start, double seconds, SnippetTiming? timing = null, CancellationToken ct = default)
    {
        timing ??= SnippetTiming.Preview;
        media.StartAt = start;
        media.Play();
        var began = clock.Now;
        await SnippetTimer.WaitUntilAudioAdvancesAsync(media.Sample, start, timing, clock, "Fake", ct);
        await SnippetTimer.WaitUntilPlayedAsync(media.Sample, start + seconds, timing, clock, "Fake", ct);
        return (media.Position - start, clock.Now - began);
    }

    [Fact]
    public async Task SnippetIsTimedByPositionNotByWhenThePlayerSaysPlaying()
    {
        var clock = new AppFakeClock();
        var media = new AppFakeMedia(clock, latency: 0.3);

        var (played, elapsed) = await PlaySnippet(clock, media, start: 0, seconds: 5);

        // A wall-clock sleep from "playing" would have given 4.7 s of audio.
        Assert.InRange(played, 5.0 - 0.0005, 5.0 + 1e-6);
        Assert.True(elapsed.TotalSeconds >= 5.299, $"elapsed {elapsed.TotalSeconds}");
    }

    [Fact]
    public async Task EndLeadStopsEarlyByThatMuch()
    {
        var clock = new AppFakeClock();
        var media = new AppFakeMedia(clock, latency: 0.25);
        var timing = SnippetTiming.SpotifyConnect;

        var (played, _) = await PlaySnippet(clock, media, start: 0, seconds: 10, timing);

        Assert.InRange(played, 10 - timing.EndLead - 0.0005, 10 - timing.EndLead + 0.3);
    }

    [Fact]
    public async Task CancellationThrowsOperationCanceledPromptly()
    {
        var clock = new AppFakeClock();
        var media = new AppFakeMedia(clock, latency: 0.3);
        using var cts = new CancellationTokenSource();
        clock.OnAdvance = now => { if (now >= TimeSpan.FromSeconds(2)) cts.Cancel(); };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PlaySnippet(clock, media, 0, 15, ct: cts.Token));
        Assert.True(clock.Now < TimeSpan.FromSeconds(2.05), $"ran on to {clock.Now}");
    }

    [Fact]
    public async Task StallFails()
    {
        var clock = new AppFakeClock();
        var media = new AppFakeMedia(clock, latency: 0.1) { StallFrom = TimeSpan.FromSeconds(1) };

        var error = await Assert.ThrowsAsync<PlayerException>(() => PlaySnippet(clock, media, 0, 15));
        Assert.Equal((PlayerErrorKind.Failed, "Fake stalled"), (error.Kind, error.Message));
    }

    [Fact]
    public async Task NeverStartingTimesOut()
    {
        var clock = new AppFakeClock();
        var media = new AppFakeMedia(clock, latency: 60);

        var error = await Assert.ThrowsAsync<PlayerException>(() => PlaySnippet(clock, media, 0, 5));
        Assert.Equal("Fake didn't start playing within 5 s", error.Message);
    }

    [Fact]
    public async Task ClipEndingFinishesTheSnippet()
    {
        var clock = new AppFakeClock();
        var media = new AppFakeMedia(clock, latency: 0.1, clipLength: 3);

        var (played, _) = await PlaySnippet(clock, media, start: 1, seconds: 5);
        Assert.Equal(2, played, 3);
    }

    [Fact]
    public async Task WrongTrackWhilePlayingFails()
    {
        var clock = new AppFakeClock();
        var media = new AppFakeMedia(clock, latency: 0.1);
        clock.OnAdvance = now => media.WrongTrack = now > TimeSpan.FromSeconds(1);

        var error = await Assert.ThrowsAsync<PlayerException>(() => PlaySnippet(clock, media, 0, 5));
        Assert.Equal("Fake played a different track", error.Message);
    }

    [Fact]
    public async Task ReseeksWhenThePlayerIgnoredTheStart()
    {
        var clock = new AppFakeClock();
        var position = 0.0; // player ignored "start at 60" and plays from 0
        var seeks = 0;
        Task<PlaybackSample> Sample(CancellationToken _) => Task.FromResult(new PlaybackSample(position += 0.01, true));
        Task Reseek(CancellationToken _) { seeks++; position = 60; return Task.CompletedTask; }

        await SnippetTimer.WaitUntilAudioAdvancesAsync(Sample, 60, SnippetTiming.SpotifyConnect, clock, "Fake", default, Reseek);

        Assert.Equal(1, seeks);
        Assert.True(position >= 60.05);
    }

    [Fact]
    public void ClampsTheStartIntoTheClip()
    {
        // Same vectors as Tests/NotchleMacTests/PlaybackPreviewPlayerTests.swift.
        Assert.Equal(0, SnippetTimer.ClampedStart(0, 5, 30));
        Assert.Equal(10, SnippetTimer.ClampedStart(10, 5, 30));
        Assert.Equal(25, SnippetTimer.ClampedStart(28, 5, 30));
        Assert.Equal(15, SnippetTimer.ClampedStart(100, 15, 30));
        Assert.Equal(0, SnippetTimer.ClampedStart(5, 40, 30));
        Assert.Equal(0, SnippetTimer.ClampedStart(-3, 5, 30));
        Assert.Equal(7, SnippetTimer.ClampedStart(7, 5, null));
        Assert.Equal(0, SnippetTimer.ClampedStart(double.NaN, 5, 30));
    }
}
