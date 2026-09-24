using Notchle.Core.Ui;

namespace Notchle.Core.Tests;

/// Long titles fit (wrap, then shrink a little, then the island grows); nothing is trimmed.
public class UiTextFitTests
{
    /// A fake measurement: a text of <paramref name="chars"/> characters in a box
    /// <paramref name="width"/> DIPs wide, each character 0.55 em.
    private static Func<double, int> Lines(int chars, double width) =>
        size => Math.Max(1, (int)Math.Ceiling(chars * 0.55 * size / width));

    [Fact]
    public void TextThatFitsKeepsItsDesignSize()
    {
        Assert.Equal(19, IslandTextFit.FontSize(19, IslandTextFit.AnswerTitleLines, Lines(20, 330)));
    }

    [Fact]
    public void ShrinksJustEnoughToStayWithinTheLineBudget()
    {
        // 99 chars in 330 DIPs: 3.1 lines at 19, 3.05 at 18.5, 2.97 at 18.
        var linesAt = Lines(99, 330);
        Assert.Equal(4, linesAt(19));
        var size = IslandTextFit.FontSize(19, 3, linesAt);
        Assert.Equal(18, size);
        Assert.True(linesAt(size) <= 3);
        Assert.True(linesAt(size + IslandTextFit.Step) > 3);
    }

    [Fact]
    public void NeverShrinksBelowThreeQuartersOrElevenDips()
    {
        var huge = Lines(10_000, 330);
        Assert.Equal(19 * 0.75, IslandTextFit.FontSize(19, 3, huge));
        Assert.Equal(11, IslandTextFit.FontSize(13, 2, huge));  // 0.75 × 13 = 9.75 → 11
        Assert.Equal(10.5, IslandTextFit.FontSize(10.5, 2, huge)); // already under 11: unchanged
        Assert.Equal(11, IslandTextFit.MinimumSize(12));
        Assert.Equal(14.25, IslandTextFit.MinimumSize(19));
    }

    [Fact]
    public void TheIslandGrowsTallerForItsContentWithinBounds()
    {
        Assert.Equal(190, IslandGeometry.ExpandedHeight(120));
        Assert.Equal(190, IslandGeometry.ExpandedHeight(190.000001));
        Assert.Equal(247, IslandGeometry.ExpandedHeight(246.3));
        Assert.Equal(IslandGeometry.MaxExpandedHeight, IslandGeometry.ExpandedHeight(5000));
        Assert.Equal(190, IslandGeometry.ExpandedHeight(double.NaN));
        Assert.Equal(new DipSize(460, 247), IslandGeometry.ShapeSize(IslandMode.Expanded, 246.3));
        // Only the open island grows.
        Assert.Equal(IslandGeometry.CompactSize, IslandGeometry.ShapeSize(IslandMode.Compact, 300));
        // The window has room for the tallest island plus its glow.
        Assert.True(IslandGeometry.WindowSize.Height >= IslandGeometry.MaxExpandedHeight + IslandGeometry.GlowMargin);
    }

    [Fact]
    public void HitTestingFollowsTheTallerShape()
    {
        var cx = IslandGeometry.WindowSize.Width / 2;
        var below = new DipPoint(cx, 260);
        Assert.False(IslandGeometry.ShapeContains(IslandMode.Expanded, below, slack: 0));
        Assert.True(IslandGeometry.ShapeContains(IslandMode.Expanded, 280, below, slack: 0));
        Assert.False(IslandGeometry.ShapeContains(IslandMode.Expanded, 280, new DipPoint(cx, 290), slack: 0));
    }

    [Fact]
    public void TheAnimatorFollowsTheContentHeight()
    {
        var a = new IslandAnimator(IslandMode.Compact);
        a.SetTarget(IslandMode.Expanded, 260);
        a.Snap();
        Assert.Equal(260, a.Current.Height);
        // Same mode, taller content: it retargets and springs there.
        a.SetTarget(IslandMode.Expanded, 300);
        Assert.False(a.IsSettled);
        for (var i = 0; i < 120; i++) a.Update(1.0 / 60, reduceMotion: false);
        Assert.Equal(IslandFrame.For(IslandMode.Expanded, 300), a.Current);
        // Collapsing ignores the content height.
        a.SetTarget(IslandMode.Compact, 300);
        a.Snap();
        Assert.Equal(IslandFrame.For(IslandMode.Compact), a.Current);
    }
}
