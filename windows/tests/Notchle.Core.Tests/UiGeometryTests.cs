using Notchle.Core.Ui;

namespace Notchle.Core.Tests;

public class UiGeometryTests
{
    private static MonitorInfo Monitor(int left, int top, int w, int h, double scale = 1, int taskbarTop = 0, bool primary = false) =>
        new(new PxRect(left, top, w, h), new PxRect(left, top + taskbarTop, w, h - taskbarTop - (taskbarTop == 0 ? 48 : 0)), scale, primary);

    [Fact]
    public void SizesMatchTheDesign()
    {
        Assert.Equal(new DipSize(170, 8), IslandGeometry.ShapeSize(IslandMode.Lip));
        Assert.Equal(new DipSize(230, 34), IslandGeometry.ShapeSize(IslandMode.Compact));
        Assert.Equal(new DipSize(460, 190), IslandGeometry.ShapeSize(IslandMode.Expanded));
        Assert.True(IslandGeometry.WindowSize.Width > 460 && IslandGeometry.WindowSize.Height > 190);
    }

    [Fact]
    public void WindowHangsTopCentreInPhysicalPixels()
    {
        var m = Monitor(0, 0, 3840, 2160, scale: 1.5);
        var r = IslandGeometry.WindowRectPx(m);
        Assert.Equal((int)Math.Round(IslandGeometry.WindowSize.Width * 1.5), r.Width);
        Assert.Equal(0, r.Top);
        Assert.InRange(r.Left + r.Width / 2, 3840 / 2 - 1, 3840 / 2 + 1);
    }

    [Fact]
    public void SecondaryMonitorWithNegativeOrigin()
    {
        var m = Monitor(-1920, -300, 1920, 1080);
        var r = IslandGeometry.WindowRectPx(m);
        Assert.Equal(-300, r.Top);
        Assert.InRange(r.Left + r.Width / 2, -961, -959);
    }

    [Fact]
    public void SitsBelowATaskbarDockedAtTheTop()
    {
        var m = Monitor(0, 0, 1920, 1080, taskbarTop: 48);
        Assert.Equal(48, IslandGeometry.AnchorTopPx(m));
        Assert.Equal(48, IslandGeometry.WindowRectPx(m).Top);
        Assert.Equal(0, IslandGeometry.AnchorTopPx(Monitor(0, 0, 1920, 1080)));
    }

    [Fact]
    public void CursorMapsIntoWindowDips()
    {
        var window = new PxRect(100, 50, 810, 345);
        Assert.Equal(new DipPoint(10, 20), IslandGeometry.ToWindowDip(new PxPoint(115, 80), window, 1.5));
    }

    [Fact]
    public void HitTestFollowsTheShapeOnly()
    {
        var cx = IslandGeometry.WindowSize.Width / 2;
        // Centre of the compact pill: inside. Far corner of the window (transparent): outside.
        Assert.True(IslandGeometry.ShapeContains(IslandMode.Compact, new DipPoint(cx, 17)));
        Assert.False(IslandGeometry.ShapeContains(IslandMode.Compact, new DipPoint(5, 150)));
        // Just past the slack at the side.
        Assert.False(IslandGeometry.ShapeContains(IslandMode.Compact, new DipPoint(cx + 115 + 7, 10)));
        Assert.True(IslandGeometry.ShapeContains(IslandMode.Compact, new DipPoint(cx + 115 + 5, 10)));
        // Rounded bottom corner: the square corner is outside, the middle of the bottom edge inside.
        Assert.False(IslandGeometry.ShapeContains(IslandMode.Expanded, new DipPoint(cx - 230 + 1, 189), slack: 0));
        Assert.True(IslandGeometry.ShapeContains(IslandMode.Expanded, new DipPoint(cx, 189), slack: 0));
        // Pressed against the top edge (y < 0) still counts.
        Assert.True(IslandGeometry.ShapeContains(IslandMode.Lip, new DipPoint(cx, -3)));
        // The slim lip gets a taller hover band than its 8 px.
        Assert.True(IslandGeometry.ShapeContains(IslandMode.Lip, new DipPoint(cx, 13), slack: 0));
        Assert.False(IslandGeometry.ShapeContains(IslandMode.Lip, new DipPoint(cx, 40)));
    }

    [Fact]
    public void ConfettiStartsUnderTheShape()
    {
        var m = Monitor(-1920, 0, 1920, 1080, scale: 2, taskbarTop: 40);
        Assert.Equal(new DipPoint(480, 20 + 190), IslandGeometry.ConfettiOrigin(m, 190));
    }

    [Fact]
    public void MonitorLookup()
    {
        var list = new[] { Monitor(0, 0, 1920, 1080), Monitor(1920, 0, 2560, 1440, primary: true) };
        Assert.Equal(1, IslandGeometry.IndexContaining(list, new PxPoint(2000, 5)));
        Assert.Equal(-1, IslandGeometry.IndexContaining(list, new PxPoint(-5, 5)));
        Assert.Equal(1, IslandGeometry.PrimaryIndex(list));
    }

    [Fact]
    public void AccentFallsBackToSpotifyGreen()
    {
        Assert.Equal(0xFF0078D4u, IslandAccent.Pick(0xFF0078D4));
        Assert.Equal(0xFF0078D4u, IslandAccent.Pick(null, 0xFF0078D4));
        Assert.Equal(IslandAccent.SpotifyGreen, IslandAccent.Pick(null, 0x00FFFFFF, 0xFF101010));
        Assert.Equal(IslandAccent.SpotifyGreen, IslandAccent.Pick());
    }
}

public class UiMonitorFollowerTests
{
    private readonly UiManualClock _clock = new();
    private static readonly MonitorInfo Left = new(new PxRect(0, 0, 1920, 1080), new PxRect(0, 0, 1920, 1032), 1, true);
    private static readonly MonitorInfo Right = new(new PxRect(1920, 0, 2560, 1440), new PxRect(1920, 0, 2560, 1392), 1.5, false);
    private static readonly MonitorInfo[] Both = [Left, Right];

    [Fact]
    public void StartsOnTheCursorsMonitor()
    {
        var f = new MonitorFollower(_clock);
        Assert.True(f.Update(Both, new PxPoint(2500, 700)));
        Assert.Equal(Right, f.Current);
    }

    [Fact]
    public void MovesOnlyAfterTheCursorSettlesAtTheTopEdge()
    {
        var f = new MonitorFollower(_clock);
        f.Update(Both, new PxPoint(100, 500));
        Assert.Equal(Left, f.Current);
        Assert.False(f.Update(Both, new PxPoint(2500, 5)));
        _clock.Advance(0.2);
        Assert.False(f.Update(Both, new PxPoint(2500, 5)));
        _clock.Advance(0.2);
        Assert.True(f.Update(Both, new PxPoint(2500, 5)));
        Assert.Equal(Right, f.Current);
    }

    [Fact]
    public void PassingThroughTheOtherMonitorDoesNotMoveIt()
    {
        var f = new MonitorFollower(_clock);
        f.Update(Both, new PxPoint(100, 500));
        f.Update(Both, new PxPoint(2500, 5));
        _clock.Advance(0.2);
        f.Update(Both, new PxPoint(2500, 400)); // dropped below the top band: resets
        _clock.Advance(0.3);
        Assert.False(f.Update(Both, new PxPoint(2500, 5)));
        _clock.Advance(0.1);
        Assert.False(f.Update(Both, new PxPoint(2500, 5)));
        Assert.Equal(Left, f.Current);
        // Anywhere low on the other monitor never moves it.
        _clock.Advance(5);
        Assert.False(f.Update(Both, new PxPoint(2500, 800)));
    }

    [Fact]
    public void TopBandScalesWithTheMonitor()
    {
        var f = new MonitorFollower(_clock);
        f.Update(Both, new PxPoint(100, 500));
        f.Update(Both, new PxPoint(2500, 17)); // 12 DIP * 1.5 = 18 px
        _clock.Advance(0.4);
        Assert.True(f.Update(Both, new PxPoint(2500, 17)));
    }

    [Fact]
    public void RecoversWhenTheMonitorGoesAway()
    {
        var f = new MonitorFollower(_clock);
        f.Update(Both, new PxPoint(2500, 500));
        Assert.True(f.Update([Left], new PxPoint(100, 100)));
        Assert.Equal(Left, f.Current);
    }

    [Fact]
    public void ReportsAWorkAreaOrDpiChangeOnTheSameMonitor()
    {
        var f = new MonitorFollower(_clock);
        f.Update(Both, new PxPoint(100, 500));
        var topTaskbar = Left with { WorkArea = new PxRect(0, 48, 1920, 1032) };
        Assert.True(f.Update([topTaskbar, Right], new PxPoint(100, 500)));
        Assert.Equal(48, IslandGeometry.AnchorTopPx(f.Current!.Value));
        Assert.False(f.Update([topTaskbar, Right], new PxPoint(100, 500)));
    }
}
