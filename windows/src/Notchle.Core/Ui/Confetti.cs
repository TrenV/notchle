namespace Notchle.Core.Ui;

/// Deterministic (seeded) confetti physics, ported from Sources/NotchleMac/UI/ConfettiView.swift
/// so both platforms burst the same way. Units are DIPs and seconds; y grows downwards.
public static class Confetti
{
    public const int Count = 80;
    public const double Duration = 1.5;
    public const double Gravity = 820;

    /// Spotify green, yellow, pink, blue, purple, orange, white (ARGB).
    public static IReadOnlyList<uint> Colors { get; } =
        [0xFF1FD661, 0xFFFFCC33, 0xFFFF5C73, 0xFF59A6FF, 0xFFBF73FF, 0xFFFF8C33, 0xFFFFFFFF];

    public readonly record struct Particle(
        double VelocityX, double VelocityY, double Width, double Height,
        int Hue, double Spin, double Flutter, bool IsDot, double Delay);

    /// SplitMix64: tiny, seedable, same sequence as the Swift RNG.
    public struct Rng(ulong state)
    {
        private ulong _state = state;

        public ulong Next()
        {
            unchecked
            {
                _state += 0x9E3779B97F4A7C15;
                var z = _state;
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EB;
                return z ^ (z >> 31);
            }
        }

        public double Unit() => (Next() >> 11) / (double)(1UL << 53);
        public double Range(double lo, double hi) => lo + (hi - lo) * Unit();
    }

    public static IReadOnlyList<Particle> Particles(ulong seed, int count = Count)
    {
        var rng = new Rng(seed);
        var list = new Particle[count];
        for (var i = 0; i < count; i++)
        {
            // Mostly downward and sideways, a few thrown up first: "falling out of the island".
            var angle = rng.Range(-0.15 * Math.PI, 1.15 * Math.PI);
            var speed = rng.Range(160, 460);
            var dot = rng.Unit() < 0.25;
            var w = rng.Range(4, 7);
            list[i] = new Particle(
                VelocityX: Math.Cos(angle) * speed,
                VelocityY: Math.Sin(angle) * speed * 0.8 + 40,
                Width: dot ? w * 0.8 : w,
                Height: dot ? w * 0.8 : w * 1.9,
                Hue: (int)(rng.Next() % (ulong)Colors.Count),
                Spin: rng.Range(-9, 9),
                Flutter: rng.Range(6, 14),
                IsDot: dot,
                Delay: rng.Range(0, 0.12));
        }
        return list;
    }

    /// Offset of a particle <paramref name="t"/> seconds into the burst, relative to the origin.
    public static (double X, double Y) Offset(Particle p, double t)
    {
        t = Math.Max(0, t - p.Delay);
        var drag = 1 - Math.Min(0.45, t * 0.3);
        return (p.VelocityX * t * drag, p.VelocityY * t + 0.5 * Gravity * t * t);
    }

    public static double Opacity(double t)
    {
        const double fadeStart = Duration * 0.65;
        return t < fadeStart ? 1 : Math.Max(0, 1 - (t - fadeStart) / (Duration - fadeStart));
    }

    /// Horizontal squash that fakes a tumbling paper strip (dots don't tumble).
    public static double FlutterScale(Particle p, double t) =>
        p.IsDot ? 1 : Math.Abs(Math.Cos(p.Flutter * t)) * 0.8 + 0.2;

    /// Rotation in degrees at <paramref name="t"/>.
    public static double RotationDegrees(Particle p, double t) => p.Spin * t * 180 / Math.PI;
}

/// "Show animations" off: a soft glow swells and fades once around the island instead.
public static class CelebrationGlow
{
    public const double Duration = 1.4;

    /// 0 → 1 → 0 over <see cref="Duration"/>.
    public static double Strength(double t) =>
        t <= 0 || t >= Duration ? 0 : Math.Sin(t / Duration * Math.PI);
}
