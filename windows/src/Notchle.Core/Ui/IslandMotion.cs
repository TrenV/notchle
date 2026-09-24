namespace Notchle.Core.Ui;

/// Spring maths for the island's open / close morph (same feel as SwiftUI's
/// .spring(response: 0.42, dampingFraction: 0.8) on macOS). Pure so it is tested headless.
public static class IslandMotion
{
    public const double Response = 0.42;
    public const double DampingFraction = 0.8;
    /// "Show animations" off: a short ease instead of a spring.
    public const double ReducedDuration = 0.15;

    /// Advances a damped spring by <paramref name="dt"/> seconds (semi-implicit Euler, 1/240 s
    /// substeps so a stalled frame cannot blow it up).
    public static (double Value, double Velocity) Step(double value, double velocity, double target, double dt,
        double response = Response, double dampingFraction = DampingFraction)
    {
        if (dt <= 0) return (value, velocity);
        dt = Math.Min(dt, 0.25);
        var stiffness = Math.Pow(2 * Math.PI / response, 2);
        var damping = 4 * Math.PI * dampingFraction / response;
        const double sub = 1.0 / 240;
        while (dt > 0)
        {
            var h = Math.Min(sub, dt);
            var accel = -stiffness * (value - target) - damping * velocity;
            velocity += accel * h;
            value += velocity * h;
            dt -= h;
        }
        return (value, velocity);
    }

    /// Linear approach used when animations are off: covers the whole 0..1 range in
    /// <see cref="ReducedDuration"/>.
    public static double Approach(double value, double target, double dt)
    {
        var step = dt / ReducedDuration;
        return value < target ? Math.Min(target, value + step) : Math.Max(target, value - step);
    }
}

/// The animated silhouette the WPF layer draws.
public readonly record struct IslandFrame(double Width, double Height, double BottomRadius, double EarRadius,
    /// 0 = collapsed content, 1 = expanded content.
    double Expansion)
{
    public static IslandFrame For(IslandMode mode)
    {
        var size = IslandGeometry.ShapeSize(mode);
        return new(size.Width, size.Height, IslandGeometry.BottomRadius(mode), IslandGeometry.EarRadius(mode),
            mode == IslandMode.Expanded ? 1 : 0);
    }
}

/// Springs the frame towards the target mode. One spring drives everything (a 0..1 progress
/// between the previous and the target frame), so width, height and radii stay in step.
public sealed class IslandAnimator
{
    private IslandFrame _from;
    private IslandFrame _to;
    private double _progress = 1;
    private double _velocity;

    public IslandAnimator(IslandMode initial)
    {
        Target = initial;
        _from = _to = IslandFrame.For(initial);
        Current = _to;
    }

    public IslandMode Target { get; private set; }
    public IslandFrame Current { get; private set; }
    public bool IsSettled => _progress == 1 && _velocity == 0;

    public void SetTarget(IslandMode mode)
    {
        if (mode == Target) return;
        Target = mode;
        _from = Current;
        _to = IslandFrame.For(mode);
        _progress = 0;
        // Keep momentum out of the new leg: a reversal mid-flight starts from rest.
        _velocity = 0;
    }

    /// Jumps straight to the target (snapshots).
    public void Snap()
    {
        _progress = 1;
        _velocity = 0;
        Current = _to;
    }

    public IslandFrame Update(double dt, bool reduceMotion)
    {
        if (IsSettled) return Current;
        if (reduceMotion)
        {
            _progress = IslandMotion.Approach(_progress, 1, dt);
            _velocity = 0;
        }
        else
        {
            (_progress, _velocity) = IslandMotion.Step(_progress, _velocity, 1, dt);
            if (Math.Abs(_progress - 1) < 0.0005 && Math.Abs(_velocity) < 0.005) { _progress = 1; _velocity = 0; }
        }
        Current = Lerp(_from, _to, _progress);
        return Current;
    }

    private static IslandFrame Lerp(IslandFrame a, IslandFrame b, double t) => new(
        Math.Max(0, a.Width + (b.Width - a.Width) * t),
        Math.Max(0, a.Height + (b.Height - a.Height) * t),
        Math.Max(0, a.BottomRadius + (b.BottomRadius - a.BottomRadius) * t),
        Math.Max(0, a.EarRadius + (b.EarRadius - a.EarRadius) * t),
        Math.Clamp(a.Expansion + (b.Expansion - a.Expansion) * t, 0, 1));
}
