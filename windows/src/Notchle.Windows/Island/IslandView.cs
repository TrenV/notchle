using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Notchle.Core;
using Notchle.Core.Ui;

namespace Notchle.Windows.Island;

/// Everything drawn inside the island window: the black shape (morphing between lip, pill and
/// open island), its accent glow, the collapsed indicators and the expanded content. Pure
/// function of (session, view model, frame, time) via <see cref="Update"/>, so the live window
/// and the offscreen snapshots render the same thing.
internal sealed class IslandView : Grid
{
    private const double HeaderHeight = 34;
    private const double ContentPadding = 28;

    private readonly IslandSession _session;
    private readonly NotchViewModel _vm;
    private readonly IslandContext _ctx;
    private readonly Path _glow;
    private readonly DropShadowEffect _glowEffect;
    private readonly Path _celebrationGlow;
    private readonly DropShadowEffect _celebrationEffect;
    private readonly Path _shape;
    private readonly Path _border;
    private readonly Canvas _content;
    private readonly CollapsedPill _collapsed;
    private readonly ExpandedHost _expanded;
    /// The playlist name: wraps (two lines, then a little smaller) instead of being cut.
    private readonly FitText _headerTitle = Ui.Fit("", 11, IslandTheme.Secondary, IslandTextFit.DefaultLines, FontWeights.SemiBold);
    private readonly TextBlock _headerProgress = Ui.Text("", 11, IslandTheme.Secondary, FontWeights.SemiBold);
    private readonly Border _gear;
    private readonly QuitButton _quit;
    private readonly TabSwitch _tabs;
    private readonly FrameworkElement _gearIcon = Icons.Gear(IslandTheme.Secondary, 13);
    private readonly FrameworkElement _closeIcon = Icons.Close(IslandTheme.Secondary, 10);
    private readonly Border _body;
    private PhaseView? _phaseView;
    private Color _accent;

    /// <param name="artwork">Album covers; none (no disk, no network) when null.</param>
    public IslandView(IslandSession session, NotchViewModel vm, Color accent, ArtworkImages? artwork = null)
    {
        _session = session;
        _vm = vm;
        _accent = accent;
        Width = IslandGeometry.WindowSize.Width;
        Height = IslandGeometry.WindowSize.Height;
        Background = IslandTheme.Transparent;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.None);
        // Takes the keyboard when the History tab shows no text box, so Esc / Ctrl+1 still arrive.
        Focusable = true;
        FocusVisualStyle = null;

        _glowEffect = new DropShadowEffect { Color = accent, BlurRadius = 30, ShadowDepth = 0, Opacity = 0.75 };
        _glow = new Path { Fill = IslandTheme.Black, Effect = _glowEffect, Opacity = 0, IsHitTestVisible = false };
        _celebrationEffect = new DropShadowEffect { Color = IslandTheme.Green, BlurRadius = 36, ShadowDepth = 0, Opacity = 1 };
        _celebrationGlow = new Path { Fill = IslandTheme.Black, Effect = _celebrationEffect, Opacity = 0, IsHitTestVisible = false };
        _shape = new Path { Fill = IslandTheme.Black };
        // Open outline: sides and bottom only, never a line along the screen's top edge.
        _border = new Path { StrokeThickness = 1, IsHitTestVisible = false };
        _content = new Canvas { Width = Width, Height = Height, ClipToBounds = false };

        Fields = new IslandFieldSet(accent);
        _ctx = new IslandContext
        {
            Session = session, Vm = vm, Accent = accent,
            UrlField = Fields.Url, TitleField = Fields.Title, ArtistField = Fields.Artist,
            Changed = () => Changed?.Invoke(),
            Artwork = artwork ?? ArtworkImages.None,
        };
        _ctx.Artwork.Loaded += () => Changed?.Invoke();

        _collapsed = new CollapsedPill();
        _content.Children.Add(_collapsed);
        Canvas.SetLeft(_collapsed, (Width - IslandGeometry.CompactSize.Width) / 2);

        _expanded = new ExpandedHost { Width = IslandGeometry.ExpandedSize.Width, VerticalAlignment = VerticalAlignment.Top };
        _expanded.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto, MinHeight = HeaderHeight });
        _expanded.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _gear = new Border
        {
            Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Background = IslandTheme.Transparent,
            Cursor = Cursors.Hand, Child = new Grid { Children = { _gearIcon, _closeIcon } },
        };
        _gearIcon.HorizontalAlignment = _closeIcon.HorizontalAlignment = HorizontalAlignment.Center;
        _gear.MouseEnter += (_, _) => _gear.Background = IslandTheme.FaintFill;
        _gear.MouseLeave += (_, _) => _gear.Background = IslandTheme.Transparent;
        _gear.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            _session.ShowingSettings = !_session.ShowingSettings;
            Changed?.Invoke();
        };
        System.Windows.Automation.AutomationProperties.SetName(_gear, "Settings");
        // Quit playlist: two presses (the second within 3 s) send Reset, back to the link field.
        _quit = new QuitButton(() =>
        {
            _session.PressQuit();
            Changed?.Invoke();
        });
        // Play | History (Ctrl+1 / Ctrl+2): the game keeps running while History shows.
        _tabs = new TabSwitch(tab =>
        {
            _session.ShowTab(tab);
            Changed?.Invoke();
        });
        var trailing = Ui.Row(8, _tabs, _quit, _headerProgress, _gear);
        trailing.Margin = new Thickness(8, 0, 0, 0);
        _headerTitle.Margin = new Thickness(0, 4, 0, 4);
        var header = Ui.Bar(Ui.Leading(6, Icons.Note(IslandTheme.GreenBrush, 12), _headerTitle), trailing, HeaderHeight);
        header.Margin = new Thickness(ContentPadding - 2, 0, ContentPadding - 6, 0);
        _expanded.Children.Add(header);
        _body = new Border { Margin = new Thickness(ContentPadding, 4, ContentPadding, 16) };
        Grid.SetRow(_body, 1);
        _expanded.Children.Add(_body);
        _content.Children.Add(_expanded);
        Canvas.SetLeft(_expanded, (Width - IslandGeometry.ExpandedSize.Width) / 2);

        Children.Add(_celebrationGlow);
        Children.Add(_glow);
        Children.Add(_shape);
        Children.Add(_content);
        Children.Add(_border);
        SetAccent(accent);
    }

    /// The three persistent text fields (kept across phase views so typing is never lost).
    public IslandFieldSet Fields { get; }
    /// A click inside changed session state: the window re-renders and re-evaluates.
    public event Action? Changed;
    public IslandFrame Frame { get; private set; }
    /// How tall the open island is for its current content (IslandGeometry.ExpandedHeight of
    /// what it measured); the window springs the shape to this.
    public double ExpandedHeight => _expanded.IslandHeight;
    /// The expanded content (tests).
    internal FrameworkElement Expanded => _expanded;

    public void SetAccent(Color accent)
    {
        _accent = accent;
        _glowEffect.Color = accent;
        _border.Stroke = IslandTheme.Frozen(accent, 0.5 * Frame.Expansion);
    }

    /// Renders the island for <paramref name="frame"/> at <paramref name="now"/>.
    /// <paramref name="reduceMotion"/>: celebration glow instead of confetti.
    public void Update(IslandFrame frame, DateTimeOffset now, bool reduceMotion)
    {
        if (frame != Frame || _shape.Data is null)
        {
            var geometry = ShapeGeometry(frame);
            _shape.Data = geometry;
            _glow.Data = geometry;
            _celebrationGlow.Data = geometry;
            _content.Clip = geometry;
            _border.Data = ShapeGeometry(frame, closed: false);
            _border.Stroke = IslandTheme.Frozen(_accent, 0.5 * frame.Expansion);
            _glow.Opacity = frame.Expansion;
        }
        Frame = frame;
        var seconds = (now - DateTimeOffset.UnixEpoch).TotalSeconds;

        var celebration = _session.CelebrationStart is { } cs ? (now - cs).TotalSeconds : double.NaN;
        _celebrationGlow.Opacity = reduceMotion && !double.IsNaN(celebration) ? CelebrationGlow.Strength(celebration) : 0;

        // Collapsed pill: fades out as the island opens.
        _collapsed.Opacity = Math.Clamp(1 - frame.Expansion * 2.5, 0, 1);
        _collapsed.Visibility = _collapsed.Opacity > 0 ? Visibility.Visible : Visibility.Hidden;
        _collapsed.Update(_session.Indicator(), seconds, reduceMotion);

        // Expanded content: fades in over the second half of the morph.
        _expanded.Opacity = Math.Clamp((frame.Expansion - 0.35) / 0.65, 0, 1);
        _expanded.Visibility = frame.Expansion > 0.01 ? Visibility.Visible : Visibility.Hidden;
        _expanded.IsHitTestVisible = frame.Expansion > 0.6;
        if (frame.Expansion > 0.01 || _phaseView is null) UpdateExpanded(now, seconds);
    }

    private void UpdateExpanded(DateTimeOffset now, double seconds)
    {
        var state = _session.State;
        var header = IslandScreens.Header(state);
        _headerTitle.Text = header.Title;
        _headerProgress.Text = header.Progress ?? "";
        _headerProgress.Visibility = header.Progress is null ? Visibility.Collapsed : Visibility.Visible;
        _gearIcon.Visibility = _session.ShowingSettings ? Visibility.Collapsed : Visibility.Visible;
        _closeIcon.Visibility = _session.ShowingSettings ? Visibility.Visible : Visibility.Collapsed;
        _quit.Update(_session.QuitLabel, _session.QuitArmed);
        _tabs.Update(_session.ShowingHistory ? IslandTab.History : IslandTab.Play);

        var screen = _session.Screen(_vm.Settings, _vm.PlayerName, _vm.PlayerPlaysFullTrack,
            _vm.History, artworkUrl: _vm.CurrentArtworkUrl);
        if (_phaseView is null || !_phaseView.Accepts(screen))
        {
            _phaseView = PhaseView.Create(screen, _ctx);
            _body.Child = _phaseView;
        }
        _phaseView.Update(screen, now, seconds);
        // Canvas children get an unbounded height: this is the same measure the layout pass
        // does (a no-op when nothing changed), so ExpandedHeight is current right away.
        _expanded.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
    }

    /// Text boxes of the current phase, for focus handling.
    public IslandTextField? FieldFor(IslandField field) => field switch
    {
        _ when _session.ShowingSettings || _session.ShowingHistory => null,
        IslandField.Url when IslandRules.ShowsUrlField(_session.State.Phase) => Fields.Url,
        IslandField.Title when IslandRules.ShowsGuessFields(_session.State.Phase) => Fields.Title,
        IslandField.Artist when IslandRules.ShowsGuessFields(_session.State.Phase) => Fields.Artist,
        _ => null,
    };

    /// The header switch (tests).
    internal TabSwitch Tabs => _tabs;

    /// The notch silhouette in window DIPs: flat top that flares into the top edge with small
    /// concave ears, straight sides, rounded bottom corners (port of NotchShape.swift).
    public static Geometry ShapeGeometry(IslandFrame f, bool closed = true)
    {
        var (left, _, width, height) = IslandGeometry.ShapeRect(f.Width, f.Height);
        var right = left + width;
        var t = Math.Min(f.EarRadius, width / 4);
        var b = Math.Max(0, Math.Min(f.BottomRadius, Math.Min((width - 2 * t) / 2, height - t)));
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(left, 0), closed, closed);
            ctx.QuadraticBezierTo(new Point(left + t, 0), new Point(left + t, t), true, true);
            ctx.LineTo(new Point(left + t, height - b), true, true);
            ctx.QuadraticBezierTo(new Point(left + t, height), new Point(left + t + b, height), true, true);
            ctx.LineTo(new Point(right - t - b, height), true, true);
            ctx.QuadraticBezierTo(new Point(right - t, height), new Point(right - t, height - b), true, true);
            ctx.LineTo(new Point(right - t, t), true, true);
            ctx.QuadraticBezierTo(new Point(right - t, 0), new Point(right, 0), true, true);
        }
        g.Freeze();
        return g;
    }
}

internal sealed class IslandFieldSet
{
    public IslandFieldSet(Color accent)
    {
        Url = new IslandTextField(IslandField.Url, IslandScreen.SourceEntry.Placeholder, accent);
        Title = new IslandTextField(IslandField.Title, IslandScreen.Guess.TitlePlaceholder, accent);
        Artist = new IslandTextField(IslandField.Artist, "Artist(s)", accent);
    }

    public IslandTextField Url { get; }
    public IslandTextField Title { get; }
    public IslandTextField Artist { get; }
    public IEnumerable<IslandTextField> All => [Url, Title, Artist];
}

/// The collapsed pill: glyph left, optional flash caption in the middle, progress right.
internal sealed class CollapsedPill : Grid
{
    private readonly Grid _glyphHost = new() { Width = 44, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _caption = Ui.Text("", 11.5, IslandTheme.Primary, FontWeights.SemiBold);
    private readonly TextBlock _text = Ui.Text("", 11.5, IslandTheme.Frozen(Colors.White, 0.85), FontWeights.SemiBold);
    private readonly Ring _ring = new(15, 2.2, IslandTheme.TrackFill);
    private readonly System.Windows.Shapes.Ellipse _pulse = new() { Width = 5, Height = 5, Fill = IslandTheme.GreenBrush };
    private readonly EqualizerGlyph _eq = new(IslandTheme.GreenBrush);
    private readonly Spinner _spinner = new(12, 2);
    private readonly Dictionary<IndicatorGlyph, FrameworkElement> _glyphs;
    private IndicatorGlyph _shown = (IndicatorGlyph)(-1);

    public CollapsedPill()
    {
        Width = IslandGeometry.CompactSize.Width;
        Height = IslandGeometry.CompactSize.Height;
        IsHitTestVisible = false;
        var ringWithPulse = new Grid { Children = { _ring, _pulse } };
        _pulse.HorizontalAlignment = HorizontalAlignment.Center;
        _pulse.VerticalAlignment = VerticalAlignment.Center;
        _glyphs = new()
        {
            [IndicatorGlyph.Spinner] = _spinner,
            [IndicatorGlyph.SnippetRing] = ringWithPulse,
            [IndicatorGlyph.Question] = QuestionMark(IslandTheme.GreenBrush),
            [IndicatorGlyph.QuestionAfterWrong] = QuestionMark(IslandTheme.RedBrush),
            [IndicatorGlyph.Check] = Icons.CheckCircle(IslandTheme.GreenBrush, 15),
            [IndicatorGlyph.Equalizer] = _eq,
            [IndicatorGlyph.PerfectSet] = Icons.CheckSeal(IslandTheme.GreenBrush, 15),
            [IndicatorGlyph.SetOver] = Icons.Note(IslandTheme.Secondary, 13),
            [IndicatorGlyph.Note] = Icons.Note(IslandTheme.GreenBrush, 13),
            [IndicatorGlyph.Warning] = Icons.Warning(13),
        };
        foreach (var g in _glyphs.Values)
        {
            g.HorizontalAlignment = HorizontalAlignment.Center;
            g.VerticalAlignment = VerticalAlignment.Center;
            g.Visibility = Visibility.Collapsed;
            _glyphHost.Children.Add(g);
        }
        _glyphHost.Margin = new Thickness(8, 0, 0, 0);
        _caption.HorizontalAlignment = HorizontalAlignment.Center;
        _text.HorizontalAlignment = HorizontalAlignment.Right;
        _text.Margin = new Thickness(0, 0, 18, 0);
        Children.Add(_glyphHost);
        Children.Add(_caption);
        Children.Add(_text);
    }

    private static FrameworkElement QuestionMark(Brush brush)
    {
        var t = Ui.Text("?", 15, brush, FontWeights.Bold);
        t.Margin = new Thickness(0, -2, 0, 0);
        return t;
    }

    public void Update(IslandIndicator indicator, double seconds, bool reduceMotion)
    {
        if (indicator.Glyph != _shown)
        {
            foreach (var (glyph, element) in _glyphs)
                element.Visibility = glyph == indicator.Glyph ? Visibility.Visible : Visibility.Collapsed;
            _shown = indicator.Glyph;
        }
        _caption.Text = indicator.Caption ?? "";
        _caption.Foreground = indicator.Glyph is IndicatorGlyph.Check or IndicatorGlyph.PerfectSet ? IslandTheme.GreenBrush : IslandTheme.Primary;
        _text.Text = indicator.Text;
        switch (indicator.Glyph)
        {
            case IndicatorGlyph.SnippetRing:
                _ring.Update(indicator.Progress, IslandTheme.GreenBrush);
                var pulse = reduceMotion ? 1 : 0.75 + 0.25 * Math.Sin(seconds * Math.PI * 2 / 1.2);
                _pulse.RenderTransformOrigin = new Point(0.5, 0.5);
                _pulse.RenderTransform = new ScaleTransform(pulse, pulse);
                break;
            case IndicatorGlyph.Equalizer:
                _eq.Update(seconds, playing: !reduceMotion);
                break;
            case IndicatorGlyph.Spinner:
                _spinner.Update(seconds);
                break;
        }
    }
}

/// The open island's content grid: measures what its content wants at the island's width, then
/// lays it out at IslandGeometry.ExpandedHeight of that (never below the design height, so the
/// bottom buttons stay at the bottom; capped, so a long History list scrolls).
internal sealed class ExpandedHost : Grid
{
    public double IslandHeight { get; private set; } = IslandGeometry.ExpandedSize.Height;

    protected override Size MeasureOverride(Size constraint)
    {
        var natural = base.MeasureOverride(new Size(constraint.Width, double.PositiveInfinity));
        IslandHeight = IslandGeometry.ExpandedHeight(natural.Height);
        var fitted = base.MeasureOverride(new Size(constraint.Width, IslandHeight));
        return new Size(fitted.Width, IslandHeight);
    }
}
