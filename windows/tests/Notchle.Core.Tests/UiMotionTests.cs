using Notchle.Core.Ui;

namespace Notchle.Core.Tests;

public class UiMotionTests
{
    private static (IslandAnimator Animator, List<IslandFrame> Frames) Run(IslandMode from, IslandMode to, bool reduce, double seconds = 1.5)
    {
        var a = new IslandAnimator(from);
        a.SetTarget(to);
        var frames = new List<IslandFrame>();
        for (var t = 0.0; t < seconds; t += 1.0 / 60) frames.Add(a.Update(1.0 / 60, reduce));
        return (a, frames);
    }

    [Fact]
    public void SpringsOpenAndSettlesOnTheExpandedShape()
    {
        var (a, frames) = Run(IslandMode.Compact, IslandMode.Expanded, reduce: false);
        Assert.True(a.IsSettled);
        Assert.Equal(IslandFrame.For(IslandMode.Expanded), a.Current);
        // A spring: it overshoots a little (that is the "spring" feel) but not wildly.
        var maxWidth = frames.Max(f => f.Width);
        Assert.True(maxWidth > 460, $"no overshoot: {maxWidth}");
        Assert.True(maxWidth < 460 + 0.1 * (460 - 230), $"overshoot too big: {maxWidth}");
        // Mostly there within ~0.4 s.
        Assert.True(frames[24].Width > 440, $"slow: {frames[24].Width}");
    }

    [Fact]
    public void WithAnimationsOffItEasesWithoutOvershootInAboutOneSixthOfASecond()
    {
        var (a, frames) = Run(IslandMode.Compact, IslandMode.Expanded, reduce: true, seconds: 0.2);
        Assert.True(a.IsSettled);
        Assert.True(frames.Max(f => f.Width) <= 460);
        Assert.True(frames.Zip(frames.Skip(1)).All(p => p.Second.Width >= p.First.Width));
    }

    [Fact]
    public void ExpansionStaysInRangeAndStartsFromTheCurrentFrameWhenReversed()
    {
        var a = new IslandAnimator(IslandMode.Compact);
        a.SetTarget(IslandMode.Expanded);
        for (var i = 0; i < 10; i++) a.Update(1.0 / 60, false);
        var mid = a.Current;
        a.SetTarget(IslandMode.Compact);
        var next = a.Update(0.001, false);
        Assert.InRange(Math.Abs(next.Width - mid.Width), 0, 1);
        for (var i = 0; i < 120; i++)
        {
            var f = a.Update(1.0 / 60, false);
            Assert.InRange(f.Expansion, 0, 1);
        }
        Assert.Equal(IslandFrame.For(IslandMode.Compact), a.Current);
    }

    [Fact]
    public void SnapJumpsToTheTarget()
    {
        var a = new IslandAnimator(IslandMode.Lip);
        a.SetTarget(IslandMode.Expanded);
        a.Snap();
        Assert.Equal(IslandFrame.For(IslandMode.Expanded), a.Current);
        Assert.True(a.IsSettled);
    }

    [Fact]
    public void AStalledFrameDoesNotExplode()
    {
        var (v, _) = IslandMotion.Step(0, 0, 1, dt: 5);
        Assert.InRange(v, 0.5, 1.5);
    }
}

public class UiConfettiTests
{
    [Fact]
    public void EightyParticlesDeterministicPerSeed()
    {
        Assert.Equal(80, Confetti.Particles(42).Count);
        Assert.Equal(Confetti.Particles(42), Confetti.Particles(42));
        Assert.NotEqual(Confetti.Particles(42), Confetti.Particles(43));
    }

    /// Same SplitMix64 as Swift's Confetti.RNG: first outputs for seed 0 are the published
    /// reference values of the algorithm.
    [Fact]
    public void RngIsSplitMix64()
    {
        var rng = new Confetti.Rng(0);
        Assert.Equal(0xE220A8397B1DCDAFUL, rng.Next());
        Assert.Equal(0x6E789E6AA1B965F4UL, rng.Next());
    }

    [Fact]
    public void BurstLastsOneAndAHalfSecondsAndFallsOutOfTheIsland()
    {
        Assert.Equal(1, Confetti.Opacity(0.1));
        Assert.Equal(0, Confetti.Opacity(Confetti.Duration));
        var p = Confetti.Particles(1);
        Assert.All(p, x => Assert.True(Confetti.Offset(x, 1.2).Y > 0));
        Assert.All(p, x => Assert.InRange(Confetti.FlutterScale(x, 0.7), 0.2, 1));
        Assert.All(p, x => Assert.InRange(x.Hue, 0, Confetti.Colors.Count - 1));
    }

    [Fact]
    public void GlowSwellsAndFades()
    {
        Assert.Equal(0, CelebrationGlow.Strength(0));
        Assert.Equal(1, CelebrationGlow.Strength(CelebrationGlow.Duration / 2), 6);
        Assert.Equal(0, CelebrationGlow.Strength(CelebrationGlow.Duration));
    }
}
