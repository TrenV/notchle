using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Notchle.Core.Ui;

namespace Notchle.Windows.Island;

/// Segoe Fluent Icons glyphs (with Segoe MDL2 Assets as the fallback on Windows 10).
internal static class FluentIcons
{
    public static readonly FontFamily Font = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    public const string Play = "";
    public const string History = "";
    public const string Skip = "";
    public const string Wrong = "";

    public static TextBlock Glyph(string glyph, double size, Brush brush) => new()
    {
        Text = glyph,
        FontFamily = Font,
        FontSize = size,
        Foreground = brush,
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center,
        SnapsToDevicePixels = true,
    };
}

/// The "Play | History" switch in the island header (Ctrl+1 / Ctrl+2).
internal sealed class TabSwitch : Border
{
    private readonly (Border Segment, TextBlock Glyph, IslandTab Tab)[] _segments;
    private readonly Action<IslandTab> _select;

    public TabSwitch(Action<IslandTab> select)
    {
        _select = select;
        CornerRadius = new CornerRadius(8);
        Background = IslandTheme.FaintFill;
        Padding = new Thickness(2);
        VerticalAlignment = VerticalAlignment.Center;
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        _segments = new[]
            {
                (IslandTab.Play, FluentIcons.Play, HistoryRules.PlayLabel, KeyHints.Ctrl1),
                (IslandTab.History, FluentIcons.History, HistoryRules.HistoryLabel, KeyHints.Ctrl2),
            }
            .Select(x =>
            {
                var glyph = FluentIcons.Glyph(x.Item2, 10, IslandTheme.Secondary);
                var segment = new Border
                {
                    Width = 24, Height = 18, CornerRadius = new CornerRadius(6), Child = glyph,
                    Cursor = Cursors.Hand, Background = IslandTheme.Transparent, ToolTip = $"{x.Item3} ({x.Item4})",
                };
                AutomationProperties.SetName(segment, x.Item3);
                AutomationProperties.SetAcceleratorKey(segment, x.Item4);
                var tab = x.Item1;
                segment.MouseLeftButtonUp += (_, e) => { e.Handled = true; select(tab); };
                row.Children.Add(segment);
                return (segment, glyph, tab);
            }).ToArray();
        Child = row;
    }

    public IslandTab Selected { get; private set; }

    /// What a click on the segment of <paramref name="tab"/> does (tests).
    internal void Press(IslandTab tab) => _select(tab);

    public void Update(IslandTab selected)
    {
        Selected = selected;
        foreach (var (segment, glyph, tab) in _segments)
        {
            var on = tab == selected;
            segment.Background = on ? IslandTheme.Frozen(Colors.White, 0.2) : IslandTheme.Transparent;
            glyph.Foreground = on ? IslandTheme.Primary : IslandTheme.Secondary;
        }
    }
}

/// The History tab: stats, then every visible entry newest first grouped by day, then the
/// two-step "Clear history". Only the spoiler-filtered screen (HistoryRules.Screen) reaches it.
internal sealed class HistoryView : PhaseView
{
    private readonly IslandScreen.History _screen;
    private readonly IslandContext _ctx;
    private readonly List<(CoverImage Cover, HistoryRow Row)> _covers = [];

    public HistoryView(IslandScreen.History screen, IslandContext ctx)
    {
        _screen = screen;
        _ctx = ctx;

        if (screen.Stats is { } stats)
        {
            var correct = Ui.Text(stats.Correct, 12, IslandTheme.GreenBrush, FontWeights.SemiBold);
            Top(Ui.Row(14, correct,
                Ui.Text(stats.Accuracy, 12, IslandTheme.Primary, FontWeights.SemiBold),
                Ui.Text(stats.AverageTries, 11.5, IslandTheme.Secondary, FontWeights.Medium),
                Ui.Text(stats.BestStreak, 11.5, IslandTheme.Secondary, FontWeights.Medium)));
        }

        UIElement? note = screen.HiddenNote is { } n ? Ui.Text(n, 10.5, IslandTheme.Tertiary, FontWeights.Medium) : null;
        ClearButton = new IslandButton(
            screen.ClearArmed ? HistoryRules.ClearConfirmLabel : HistoryRules.ClearLabel, null,
            screen.ClearArmed ? IslandButton.Kind.Secondary : IslandButton.Kind.Quiet, () =>
            {
                ctx.Session.PressClearHistory();
                ctx.Changed();
            })
        { Visibility = screen.CanClear ? Visibility.Visible : Visibility.Collapsed };
        if (screen.ClearArmed) ClearButton.Background = IslandTheme.RedBrush;
        Bottom(Ui.Bar(note, ClearButton, 24), 4);

        if (screen.EmptyText is { } empty)
        {
            var text = Wrapping(empty, 12.5, IslandTheme.Secondary, 2);
            text.TextAlignment = TextAlignment.Center;
            text.HorizontalAlignment = HorizontalAlignment.Center;
            text.VerticalAlignment = VerticalAlignment.Center;
            Fill(text);
            return;
        }

        var list = new StackPanel();
        foreach (var day in screen.Days)
        {
            var heading = Ui.Text(day.Heading, 10.5, IslandTheme.Tertiary, FontWeights.SemiBold);
            heading.Margin = new Thickness(0, list.Children.Count == 0 ? 2 : 6, 0, 2);
            list.Children.Add(heading);
            foreach (var row in day.Rows) list.Children.Add(Row(row));
        }
        Fill(new ScrollViewer
        {
            Content = list,
            Margin = new Thickness(0, 4, 0, 0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, // wheel still scrolls
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
        });
        ShowCovers(animate: false);
    }

    internal IslandButton ClearButton { get; }

    private FrameworkElement Row(HistoryRow row)
    {
        var cover = new CoverImage(28, 5, shadow: false, placeholder: true) { Margin = new Thickness(0, 0, 8, 0) };
        _covers.Add((cover, row));

        var badge = new Border
        {
            CornerRadius = new CornerRadius(8), Padding = new Thickness(7, 1, 7, 1), Height = 17,
            VerticalAlignment = VerticalAlignment.Center,
            Background = row.Correct ? IslandTheme.Frozen(IslandTheme.Green, 0.18) : IslandTheme.Frozen(IslandTheme.Red, 0.16),
            Child = Ui.Text(row.Badge, 10.5, row.Correct ? IslandTheme.GreenBrush : IslandTheme.RedBrush, FontWeights.SemiBold),
        };
        var trailing = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        void Count(string glyph, int n, string label)
        {
            if (n <= 0) return;
            var item = Ui.Row(2, FluentIcons.Glyph(glyph, 9, IslandTheme.Tertiary), Ui.Text($"{n}", 10, IslandTheme.Tertiary, FontWeights.Medium));
            item.Margin = new Thickness(0, 0, 7, 0);
            item.ToolTip = $"{n} {label}";
            AutomationProperties.SetName(item, $"{n} {label}");
            trailing.Children.Add(item);
        }
        Count(FluentIcons.Skip, row.Skips, row.Skips == 1 ? "skip" : "skips");
        Count(FluentIcons.Wrong, row.WrongGuesses, row.WrongGuesses == 1 ? "wrong guess" : "wrong guesses");
        trailing.Children.Add(badge);

        var title = Ui.Text($"{row.Title} — {row.Artists}", 12, IslandTheme.Primary, FontWeights.SemiBold);
        var detail = Ui.Text(row.Listing.Length > 0 ? $"{row.Listing} · {row.When}" : row.When, 10.5, IslandTheme.Tertiary, FontWeights.Medium);
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        texts.Children.Add(title);
        texts.Children.Add(detail);

        var panel = new DockPanel { LastChildFill = true, Height = 34 };
        DockPanel.SetDock(cover, Dock.Left);
        panel.Children.Add(cover);
        DockPanel.SetDock(trailing, Dock.Right);
        panel.Children.Add(trailing);
        panel.Children.Add(texts);
        return panel;
    }

    public override bool Accepts(IslandScreen screen) =>
        screen is IslandScreen.History h && (ReferenceEquals(h, _screen) || h.Fingerprint == _screen.Fingerprint);

    public override void Update(IslandScreen screen, DateTimeOffset now, double seconds) => ShowCovers(animate: true);

    private void ShowCovers(bool animate)
    {
        foreach (var (cover, row) in _covers) cover.Show(_ctx.Artwork.For(row.TrackId, row.ArtworkUrl), animate);
    }
}
