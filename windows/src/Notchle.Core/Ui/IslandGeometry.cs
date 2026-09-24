namespace Notchle.Core.Ui;

/// How big the island is.
public enum IslandMode
{
    /// No game: a slim lip you can barely see.
    Lip,
    /// A game is on: the pill with the collapsed indicators.
    Compact,
    /// Hovered / clicked open.
    Expanded,
}

/// Physical-pixel rectangle (what Win32 reports under per-monitor DPI v2 awareness).
public readonly record struct PxRect(int Left, int Top, int Width, int Height)
{
    public int Right => Left + Width;
    public int Bottom => Top + Height;
    public bool Contains(PxPoint p) => p.X >= Left && p.X < Right && p.Y >= Top && p.Y < Bottom;
}

public readonly record struct PxPoint(int X, int Y);
public readonly record struct DipSize(double Width, double Height);
public readonly record struct DipPoint(double X, double Y);

/// One display as the window layer sees it.
/// <param name="Scale">DPI / 96 of that monitor.</param>
public readonly record struct MonitorInfo(PxRect Bounds, PxRect WorkArea, double Scale, bool IsPrimary);

/// Pure placement maths. The island window is a fixed transparent canvas (DIPs) that hangs
/// top-centre on a monitor; the black shape morphs inside it and only the shape takes the mouse.
public static class IslandGeometry
{
    public static readonly DipSize LipSize = new(170, 8);
    public static readonly DipSize CompactSize = new(230, 34);
    /// The open island at its smallest; it grows taller (never wider) when its content needs it.
    public static DipSize ExpandedSize => new(460, 190);
    /// The tallest the open island gets: long titles wrap and the island grows up to this; a
    /// longer History list scrolls inside it.
    public const double MaxExpandedHeight = 420;
    /// Room around the expanded shape for the accent glow.
    public const double GlowMargin = 40;
    /// The window is a fixed transparent canvas sized for the tallest island; only the shape
    /// takes the mouse, so the extra height is click-through.
    public static DipSize WindowSize => new(ExpandedSize.Width + 2 * GlowMargin, MaxExpandedHeight + GlowMargin);
    /// Hover slack around the shape so the edge does not flicker.
    public const double HoverSlack = 6;
    /// A slim lip is hard to hit: give it a taller hover band.
    public const double LipHoverHeight = 14;

    public static DipSize ShapeSize(IslandMode mode) => ShapeSize(mode, ExpandedSize.Height);

    /// <paramref name="expandedHeight"/>: what the open content measured (see <see cref="ExpandedHeight"/>).
    public static DipSize ShapeSize(IslandMode mode, double expandedHeight) => mode switch
    {
        IslandMode.Lip => LipSize,
        IslandMode.Compact => CompactSize,
        _ => new(ExpandedSize.Width, ExpandedHeight(expandedHeight)),
    };

    /// Height of the open island for content that wants <paramref name="contentHeight"/> DIPs:
    /// never below the design height, never above <see cref="MaxExpandedHeight"/>, whole DIPs.
    public static double ExpandedHeight(double contentHeight) =>
        double.IsNaN(contentHeight) ? ExpandedSize.Height
            : Math.Clamp(Math.Ceiling(contentHeight - 0.01), ExpandedSize.Height, MaxExpandedHeight);

    public static double BottomRadius(IslandMode mode) => mode switch
    {
        IslandMode.Lip => 4,
        IslandMode.Compact => 17,
        _ => 26,
    };

    /// Concave "ears" where the shape meets the top edge, like a hardware notch.
    public static double EarRadius(IslandMode mode) => mode switch
    {
        IslandMode.Lip => 3,
        IslandMode.Compact => 6,
        _ => 10,
    };

    /// The y the island hangs from: the monitor top, or just below a taskbar docked at the top
    /// (the work area starts lower than the monitor then).
    public static int AnchorTopPx(MonitorInfo m) => m.WorkArea.Top > m.Bounds.Top ? m.WorkArea.Top : m.Bounds.Top;

    /// Window rectangle in physical pixels, centred on the monitor.
    public static PxRect WindowRectPx(MonitorInfo m)
    {
        var w = (int)Math.Round(WindowSize.Width * m.Scale);
        var h = (int)Math.Round(WindowSize.Height * m.Scale);
        var left = m.Bounds.Left + (m.Bounds.Width - w) / 2;
        return new PxRect(left, AnchorTopPx(m), w, h);
    }

    /// A screen point in the window's own DIP space.
    public static DipPoint ToWindowDip(PxPoint p, PxRect window, double scale) =>
        new((p.X - window.Left) / scale, (p.Y - window.Top) / scale);

    /// Shape rectangle (left, top, width, height) in window DIPs: centred, hanging from y = 0.
    public static (double Left, double Top, double Width, double Height) ShapeRect(double width, double height) =>
        ((WindowSize.Width - width) / 2, 0, width, height);

    /// Is the window-DIP point over the shape (slack included)? The bottom corners are rounded;
    /// everything above y = 0 counts (the pointer pressed against the top edge).
    public static bool ShapeContains(IslandMode mode, DipPoint p, double slack = HoverSlack) =>
        ShapeContains(mode, ExpandedSize.Height, p, slack);

    /// Same, for an open island that grew to <paramref name="expandedHeight"/>.
    public static bool ShapeContains(IslandMode mode, double expandedHeight, DipPoint p, double slack = HoverSlack)
    {
        var size = ShapeSize(mode, expandedHeight);
        var height = mode == IslandMode.Lip ? Math.Max(size.Height, LipHoverHeight) : size.Height;
        return RoundedBottomContains(size.Width, height, BottomRadius(mode), p, slack);
    }

    public static bool RoundedBottomContains(double width, double height, double radius, DipPoint p, double slack)
    {
        var (left, _, _, _) = ShapeRect(width, height);
        var right = left + width;
        if (p.X < left - slack || p.X > right + slack || p.Y > height + slack) return false;
        var r = Math.Min(radius, Math.Min(width, height) / 2);
        if (p.Y <= height - r) return true;
        // Bottom band: inside the straight middle, or within the corner circle (+ slack).
        var cx = p.X < left + r ? left + r : p.X > right - r ? right - r : p.X;
        var cy = height - r;
        var dx = p.X - cx;
        var dy = p.Y - cy;
        return dx * dx + dy * dy <= (r + slack) * (r + slack);
    }

    /// Where confetti starts, in DIPs relative to the monitor's top-left: the bottom centre of
    /// the shape.
    public static DipPoint ConfettiOrigin(MonitorInfo m, double shapeHeight) =>
        new(m.Bounds.Width / m.Scale / 2, (AnchorTopPx(m) - m.Bounds.Top) / m.Scale + shapeHeight);

    public static int IndexContaining(IReadOnlyList<MonitorInfo> monitors, PxPoint p)
    {
        for (var i = 0; i < monitors.Count; i++)
            if (monitors[i].Bounds.Contains(p)) return i;
        return -1;
    }

    public static int PrimaryIndex(IReadOnlyList<MonitorInfo> monitors)
    {
        for (var i = 0; i < monitors.Count; i++)
            if (monitors[i].IsPrimary) return i;
        return monitors.Count > 0 ? 0 : -1;
    }
}

/// Follows the monitor the user works on: the island moves once the cursor settles against the
/// top edge of another monitor (a quick pass across the edge does not move it).
public sealed class MonitorFollower(TimeProvider? clock = null)
{
    public static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(0.35);
    /// How close to the anchor edge counts as "at the top", in DIPs of that monitor.
    public const double TopBand = 12;

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private int _candidate = -1;
    private DateTimeOffset _candidateSince;

    public MonitorInfo? Current { get; private set; }

    /// Returns true when the island should move to <see cref="Current"/>.
    public bool Update(IReadOnlyList<MonitorInfo> monitors, PxPoint cursor)
    {
        if (monitors.Count == 0) return false;
        var currentIndex = Current is { } c ? IndexOfBounds(monitors, c.Bounds) : -1;
        if (currentIndex < 0)
        {
            // First run, or the monitor went away / changed: start where the cursor is.
            var start = IslandGeometry.IndexContaining(monitors, cursor);
            if (start < 0) start = IslandGeometry.PrimaryIndex(monitors);
            Current = monitors[start];
            _candidate = -1;
            return true;
        }
        if (!Equals(monitors[currentIndex], Current))
        {
            // Same monitor, new work area or DPI (taskbar moved, scale changed).
            Current = monitors[currentIndex];
            return true;
        }
        var here = IslandGeometry.IndexContaining(monitors, cursor);
        if (here < 0 || here == currentIndex || !AtTopEdge(monitors[here], cursor))
        {
            _candidate = -1;
            return false;
        }
        var now = _clock.GetUtcNow();
        if (_candidate != here)
        {
            _candidate = here;
            _candidateSince = now;
            return false;
        }
        if (now - _candidateSince < SettleTime) return false;
        Current = monitors[here];
        _candidate = -1;
        return true;
    }

    private static bool AtTopEdge(MonitorInfo m, PxPoint cursor) =>
        cursor.Y <= IslandGeometry.AnchorTopPx(m) + TopBand * m.Scale;

    private static int IndexOfBounds(IReadOnlyList<MonitorInfo> monitors, PxRect bounds)
    {
        for (var i = 0; i < monitors.Count; i++)
            if (monitors[i].Bounds == bounds) return i;
        return -1;
    }
}

/// Picks the glow colour: the Windows accent colour if one is readable and visible on black,
/// else Spotify green.
public static class IslandAccent
{
    public const uint SpotifyGreen = 0xFF1DB954;

    public static uint Pick(params uint?[] candidates)
    {
        foreach (var c in candidates)
            if (c is { } argb && IsUsable(argb)) return argb | 0xFF000000;
        return SpotifyGreen;
    }

    /// Not (nearly) transparent and not (nearly) black: a glow must show on the black pill.
    public static bool IsUsable(uint argb)
    {
        var a = (argb >> 24) & 0xFF;
        var r = (argb >> 16) & 0xFF;
        var g = (argb >> 8) & 0xFF;
        var b = argb & 0xFF;
        var luma = (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255;
        return a >= 0x80 && luma >= 0.12;
    }
}
