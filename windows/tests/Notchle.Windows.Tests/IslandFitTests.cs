using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Notchle.Core.Ui;
using Notchle.Windows.Island;

namespace Notchle.Windows.Tests;

/// Nothing in the open island is cut off: on the real WPF tree, with very long titles, artist
/// lists, playlist names, typed guesses and error text, every shown text is untrimmed, gets the
/// room its full text needs, and lies inside the (taller) island shape.
public class IslandFitTests
{
    public static TheoryData<string> LongScenarios => new()
    {
        "51-long-correct", "52-long-revealed", "53-long-guessing", "54-long-wrong",
        "55-long-error", "56-long-history", "57-long-set-failed",
    };

    private static IslandSnapshots.Scenario Scenario(string name) => IslandSnapshots.Scenarios.Single(s => s.Name == name);

    /// Shown = this and every ancestor up to <paramref name="root"/> is Visible (IsVisible is
    /// always false offscreen, without a PresentationSource).
    private static bool Shown(FrameworkElement e, DependencyObject root)
    {
        for (DependencyObject? d = e; d is not null && d != root; d = VisualTreeHelper.GetParent(d))
            if (d is UIElement u && (u.Visibility != Visibility.Visible || u.Opacity == 0)) return false;
        return true;
    }

    private static Size Needed(TextBlock t, double width)
    {
        var ft = new FormattedText(t.Text, CultureInfo.CurrentUICulture, t.FlowDirection,
            new Typeface(t.FontFamily, t.FontStyle, t.FontWeight, t.FontStretch), t.FontSize, Brushes.Black, null,
            TextOptions.GetTextFormattingMode(t), VisualTreeHelper.GetDpi(t).PixelsPerDip)
        { MaxTextWidth = Math.Max(1, width), Trimming = TextTrimming.None };
        return new Size(ft.WidthIncludingTrailingWhitespace, ft.Height);
    }

    /// Every problem found: trimmed text, a box too small for its full text, a box outside the island.
    internal static List<string> Problems(IslandSnapshots.Scenario scenario, out double islandHeight, out List<string> texts)
    {
        var problems = new List<string>();
        var shown = new List<string>();
        var height = 0.0;
        IslandSta.Run(() =>
        {
            var (_, view, _) = IslandSnapshots.Compose(scenario);
            height = view.ExpandedHeight;
            var expanded = view.Expanded;
            var islandLeft = (IslandGeometry.WindowSize.Width - IslandGeometry.ExpandedSize.Width) / 2;
            // History scrolls inside the island: its rows are checked for width / trimming only.
            var scroller = IslandSnapshots.Descendants<ScrollViewer>(expanded).FirstOrDefault();
            foreach (var t in IslandSnapshots.Descendants<TextBlock>(expanded))
            {
                if (t.Text.Length == 0 || !Shown(t, view)) continue;
                shown.Add(t.Text);
                var what = $"\"{t.Text}\" ({t.FontSize:0.##} DIP)";
                if (t.TextTrimming != TextTrimming.None) problems.Add($"{what}: TextTrimming {t.TextTrimming}");
                if (t.ActualWidth <= 0) { problems.Add($"{what}: not laid out"); continue; }
                var need = Needed(t, t.ActualWidth);
                if (t.TextWrapping == TextWrapping.NoWrap && need.Width > t.ActualWidth + 0.5)
                    problems.Add($"{what}: needs {need.Width:0.#} wide, got {t.ActualWidth:0.#}");
                if (need.Height > t.ActualHeight + 0.5)
                    problems.Add($"{what}: needs {need.Height:0.#} tall, got {t.ActualHeight:0.#}");
                if (t.DesiredSize.Width > t.RenderSize.Width + t.Margin.Left + t.Margin.Right + 0.5
                    || t.DesiredSize.Height > t.RenderSize.Height + t.Margin.Top + t.Margin.Bottom + 0.5)
                    problems.Add($"{what}: desired {t.DesiredSize} > arranged {t.RenderSize}");
                var box = t.TransformToAncestor(view).TransformBounds(new Rect(t.RenderSize));
                var inScroller = scroller is not null && t.IsDescendantOf(scroller);
                if (box.Left < islandLeft - 0.5 || box.Right > islandLeft + IslandGeometry.ExpandedSize.Width + 0.5)
                    problems.Add($"{what}: outside the island horizontally ({box.Left:0.#}..{box.Right:0.#})");
                if (!inScroller && box.Bottom > height + 0.5)
                    problems.Add($"{what}: below the island ({box.Bottom:0.#} > {height:0.#})");
            }
            // Shrinking stops at IslandTextFit.MinimumSize (0.75×, never under 11 DIP).
            foreach (var f in IslandSnapshots.Descendants<FitText>(expanded))
                if (f.Block.FontSize < IslandTextFit.MinimumSize(f.DesignSize) - 0.001)
                    problems.Add($"\"{f.Text}\": {f.Block.FontSize} DIP < minimum {IslandTextFit.MinimumSize(f.DesignSize)}");
        });
        islandHeight = height;
        texts = shown;
        return problems;
    }

    [Theory]
    [MemberData(nameof(LongScenarios))]
    public void LongTextsAreNeverCutOff(string name)
    {
        var problems = Problems(Scenario(name), out _, out var texts);
        Assert.True(texts.Count > 3, $"control: the walk found text ({texts.Count})");
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public void TheAnswerShowsTheWholeTitleArtistsAndPlaylistAndTheIslandGrows()
    {
        var problems = Problems(Scenario("51-long-correct"), out var height, out var texts);
        Assert.Empty(problems);
        Assert.Contains(IslandSnapshots.LongTitle, texts);
        Assert.Contains(string.Join(", ", IslandSnapshots.LongArtists), texts);
        Assert.Contains(IslandSnapshots.LongPlaylist, texts);
        Assert.True(height > IslandGeometry.ExpandedSize.Height, $"island did not grow: {height}");
        Assert.True(height <= IslandGeometry.MaxExpandedHeight);
    }

    [Fact]
    public void ShortContentKeepsTheDesignHeight()
    {
        Problems(Scenario("13-revealed-gave-up"), out var height, out _);
        Assert.Equal(IslandGeometry.ExpandedSize.Height, height);
    }

    [Fact]
    public void AHistoryRowWithALongTitleShowsItAll()
    {
        var problems = Problems(Scenario("56-long-history"), out _, out var texts);
        Assert.Empty(problems);
        Assert.Contains(texts, t => t.Contains(IslandSnapshots.LongTitle) && t.Contains("Peso Pluma"));
    }

    [Fact]
    public void ALongTypedGuessShowsInFullInTheVerdictChips()
    {
        var problems = Problems(Scenario("54-long-wrong"), out _, out var texts);
        Assert.Empty(problems);
        Assert.Contains(IslandSnapshots.LongGuess, texts);
        Assert.Contains(IslandSnapshots.LongGuessArtist, texts);
    }

    [Fact]
    public void ALongErrorShowsInFull()
    {
        var problems = Problems(Scenario("55-long-error"), out _, out var texts);
        Assert.Empty(problems);
        Assert.Contains(IslandSnapshots.LongError, texts);
    }

    /// Secrecy still holds with the long track: during a guess none of it is anywhere in the tree.
    [Theory]
    [InlineData("53-long-guessing")]
    [InlineData("54-long-wrong")]
    public void TheLongTrackStaysSecretWhileGuessing(string name)
    {
        var all = "";
        IslandSta.Run(() => all = string.Join(" | ", IslandSta.AllText(IslandSnapshots.Compose(Scenario(name)).Root)));
        Assert.Contains(IslandSnapshots.LongPlaylist, all); // control: the walk reads the island
        // (Not words the typed guess itself contains: the player's own text may show.)
        foreach (var secret in new[] { "Remaster", "(Extended", "KAROL G", "Tiësto" })
            Assert.DoesNotContain(secret, all, StringComparison.Ordinal);
    }
}
