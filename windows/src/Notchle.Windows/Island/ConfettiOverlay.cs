using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Notchle.Core.Ui;

namespace Notchle.Windows.Island;

/// Draws one confetti burst (Notchle.Core.Ui.Confetti physics) at a given moment. Used live by
/// the overlay window and frozen by the snapshots.
internal sealed class ConfettiLayer : FrameworkElement
{
    private static readonly Brush[] Brushes = Confetti.Colors.Select(c => IslandTheme.Frozen(IslandTheme.FromArgb(c))).ToArray();
    private IReadOnlyList<Confetti.Particle> _particles = [];

    public ConfettiLayer() => IsHitTestVisible = false;

    public Point Origin { get; set; }
    /// Seconds into the burst; null draws nothing.
    public double? Time { get; set; }

    public void Start(ulong seed, Point origin)
    {
        _particles = Confetti.Particles(seed);
        Origin = origin;
        Time = 0;
        InvalidateVisual();
    }

    public bool IsRunning => Time is { } t && t < Confetti.Duration;

    protected override void OnRender(DrawingContext dc)
    {
        if (Time is not { } t || t >= Confetti.Duration || _particles.Count == 0) return;
        dc.PushOpacity(Confetti.Opacity(t));
        foreach (var p in _particles)
        {
            if (t < p.Delay) continue;
            var (x, y) = Confetti.Offset(p, t);
            var m = Matrix.Identity;
            m.Scale(Confetti.FlutterScale(p, t), 1);
            m.Rotate(Confetti.RotationDegrees(p, t));
            m.Translate(Origin.X + x, Origin.Y + y);
            dc.PushTransform(new MatrixTransform(m));
            var rect = new Rect(-p.Width / 2, -p.Height / 2, p.Width, p.Height);
            var brush = Brushes[p.Hue];
            if (p.IsDot) dc.DrawEllipse(brush, null, new Point(0, 0), p.Width / 2, p.Height / 2);
            else dc.DrawRoundedRectangle(brush, null, rect, 1, 1);
            dc.Pop();
        }
        dc.Pop();
    }
}

/// Transparent, click-through, never-activating window over the island's monitor so the burst
/// falls out of the island onto the desktop. It covers the full monitor height and a column
/// wide enough for everything a particle can reach (±~380 DIP in 1.5 s), which keeps the
/// per-frame layered-window update small on 4K screens.
internal sealed class ConfettiOverlayWindow : Window
{
    public const double ColumnWidth = 1000;

    private readonly ConfettiLayer _layer = new();
    private DateTime _started;
    private IntPtr _hwnd;

    public ConfettiOverlayWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        IsHitTestVisible = false;
        Title = "Notchle confetti";
        Content = _layer;
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            IslandNative.SetExStyleBits(_hwnd, IslandNative.WS_EX_TRANSPARENT | IslandNative.WS_EX_NOACTIVATE
                | IslandNative.WS_EX_TOOLWINDOW | IslandNative.WS_EX_LAYERED, on: true);
        };
    }

    /// Bursts from <paramref name="originOnMonitor"/> (DIPs from the monitor's top-left).
    public void Burst(MonitorInfo monitor, DipPoint originOnMonitor, ulong seed)
    {
        var widthDip = Math.Min(ColumnWidth, monitor.Bounds.Width / monitor.Scale);
        var leftDip = originOnMonitor.X - widthDip / 2;
        var rect = new PxRect(
            monitor.Bounds.Left + (int)Math.Round(leftDip * monitor.Scale), monitor.Bounds.Top,
            (int)Math.Round(widthDip * monitor.Scale), monitor.Bounds.Height);
        if (!IsVisible)
        {
            Width = widthDip;
            Height = monitor.Bounds.Height / monitor.Scale;
            Show();
        }
        IslandNative.PlaceTopmost(_hwnd, rect);
        _layer.Start(seed, new Point(originOnMonitor.X - leftDip, originOnMonitor.Y));
        _started = DateTime.UtcNow;
        CompositionTarget.Rendering -= OnFrame;
        CompositionTarget.Rendering += OnFrame;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        var t = (DateTime.UtcNow - _started).TotalSeconds;
        _layer.Time = t;
        _layer.InvalidateVisual();
        if (t < Confetti.Duration) return;
        CompositionTarget.Rendering -= OnFrame;
        _layer.Time = null;
        Hide();
    }
}
