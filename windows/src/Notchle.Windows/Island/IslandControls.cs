using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Notchle.Core.Ui;

namespace Notchle.Windows.Island;

// Small building blocks of the island, built in code (not XAML) so everything is type-checked
// at compile time on any OS; nothing depends on the default WPF control templates' look.

internal static class Ui
{
    public static TextBlock Text(string text, double size, Brush brush, FontWeight? weight = null) => new()
    {
        Text = text,
        FontFamily = IslandTheme.Font,
        FontSize = size,
        FontWeight = weight ?? FontWeights.Normal,
        Foreground = brush,
        TextTrimming = TextTrimming.CharacterEllipsis,
        VerticalAlignment = VerticalAlignment.Center,
        SnapsToDevicePixels = true,
    };

    public static StackPanel Row(double spacing, params UIElement[] children)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        for (var i = 0; i < children.Length; i++)
        {
            if (i > 0 && children[i] is FrameworkElement fe) fe.Margin = new Thickness(spacing, fe.Margin.Top, fe.Margin.Right, fe.Margin.Bottom);
            row.Children.Add(children[i]);
        }
        return row;
    }

    /// DockPanel whose last child fills; handy for "leading ... spacer ... trailing" rows.
    public static DockPanel Bar(UIElement? leading, UIElement? trailing, double height)
    {
        var bar = new DockPanel { Height = height, LastChildFill = true };
        if (trailing is not null) { DockPanel.SetDock(trailing, Dock.Right); bar.Children.Add(trailing); }
        if (leading is not null) { DockPanel.SetDock(leading, Dock.Left); bar.Children.Add(leading); }
        bar.Children.Add(new Border());
        return bar;
    }

    public static void Detach(UIElement element)
    {
        switch (VisualTreeHelper.GetParent(element) ?? LogicalTreeHelper.GetParent(element))
        {
            case Panel p: p.Children.Remove(element); break;
            case Decorator d: d.Child = null; break;
            case ContentControl c: c.Content = null; break;
        }
    }
}

/// Button drawn by hand: capsule, label and a dim keyboard hint ("Next Enter").
internal sealed class IslandButton : Border
{
    public enum Kind { Primary, Secondary, Quiet }

    private readonly Kind _kind;
    private readonly TextBlock _label;
    private readonly TextBlock _hint;
    private bool _pressed;
    private bool _enabled = true;

    public IslandButton(string label, string? hint, Kind kind, Action onClick)
    {
        _kind = kind;
        Click = onClick;
        Height = 26;
        CornerRadius = new CornerRadius(13);
        Padding = new Thickness(kind == Kind.Quiet ? 0 : 12, 0, kind == Kind.Quiet ? 0 : 12, 0);
        Background = kind switch
        {
            Kind.Primary => IslandTheme.GreenBrush,
            Kind.Secondary => IslandTheme.SecondaryFill,
            _ => IslandTheme.Transparent,
        };
        var fg = kind switch
        {
            Kind.Primary => IslandTheme.Black,
            Kind.Secondary => IslandTheme.Primary,
            _ => IslandTheme.Secondary,
        };
        _label = Ui.Text(label, 12, fg, FontWeights.SemiBold);
        _hint = Ui.Text(hint ?? "", 10, fg, FontWeights.SemiBold);
        _hint.Opacity = 0.55;
        _hint.Visibility = hint is null ? Visibility.Collapsed : Visibility.Visible;
        Child = Ui.Row(5, _label, _hint);
        Cursor = Cursors.Hand;
        VerticalAlignment = VerticalAlignment.Center;
        HorizontalAlignment = HorizontalAlignment.Left;
        RenderTransformOrigin = new Point(0.5, 0.5);
        AutomationProperties.SetName(this, label);
        MouseEnter += (_, _) => Restyle();
        MouseLeave += (_, _) => { _pressed = false; Restyle(); };
    }

    public Action Click { get; set; }
    public string Label { get => _label.Text; set { _label.Text = value; AutomationProperties.SetName(this, value); } }
    public string Hint => _hint.Text;
    public bool Enabled { get => _enabled; set { _enabled = value; Restyle(); } }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!_enabled) return;
        _pressed = true;
        CaptureMouse();
        Restyle();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        var fire = _pressed && IsMouseOver && _enabled;
        _pressed = false;
        ReleaseMouseCapture();
        Restyle();
        if (fire) { e.Handled = true; Click(); }
    }

    private void Restyle()
    {
        Opacity = !_enabled ? 0.4 : _pressed ? 0.75 : IsMouseOver && _kind != Kind.Quiet ? 0.9 : 1;
        RenderTransform = _pressed ? new ScaleTransform(0.97, 0.97) : Transform.Identity;
        if (_kind == Kind.Quiet) _label.Foreground = IsMouseOver && _enabled ? IslandTheme.Primary : IslandTheme.Secondary;
    }
}

/// Round icon-only button (the ↺ restart): hover fill, tooltip, accessible name. Not focusable,
/// so clicking it leaves keyboard focus (and the caret) in the text box it was in.
internal sealed class IslandIconButton : Border
{
    private bool _pressed;
    private bool _highlighted;

    public IslandIconButton(FrameworkElement glyph, string label, string? shortcut, Action onClick)
    {
        Click = onClick;
        Width = Height = 22;
        CornerRadius = new CornerRadius(11);
        Background = IslandTheme.Transparent;
        Cursor = Cursors.Hand;
        Focusable = false;
        VerticalAlignment = VerticalAlignment.Center;
        glyph.HorizontalAlignment = HorizontalAlignment.Center;
        glyph.VerticalAlignment = VerticalAlignment.Center;
        Child = glyph;
        RenderTransformOrigin = new Point(0.5, 0.5);
        Label = label;
        if (shortcut is not null) AutomationProperties.SetAcceleratorKey(this, shortcut);
        ToolTipService.SetInitialShowDelay(this, 400);
        MouseEnter += (_, _) => Restyle();
        MouseLeave += (_, _) => { _pressed = false; Restyle(); };
    }

    public Action Click { get; set; }

    /// Tooltip and accessible name.
    public string Label
    {
        get => AutomationProperties.GetName(this);
        set
        {
            AutomationProperties.SetName(this, value);
            ToolTip = value;
        }
    }

    /// Draws the hover state without a pointer (snapshots).
    public bool Highlighted { get => _highlighted; set { _highlighted = value; Restyle(); } }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        _pressed = true;
        CaptureMouse();
        Restyle();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        var fire = _pressed && IsMouseOver;
        _pressed = false;
        ReleaseMouseCapture();
        Restyle();
        if (fire) { e.Handled = true; Click(); }
    }

    private void Restyle()
    {
        Background = _pressed ? IslandTheme.SecondaryFill : IsMouseOver || _highlighted ? IslandTheme.FaintFill : IslandTheme.Transparent;
        RenderTransform = _pressed ? new ScaleTransform(0.94, 0.94) : Transform.Identity;
    }
}

/// Rounded text box with its own placeholder and an accent focus ring.
internal sealed class IslandTextField : Grid
{
    private readonly Border _frame;
    private readonly TextBlock _placeholder;
    private readonly Brush _focusStroke;

    public IslandTextField(IslandField field, string placeholder, Color accent)
    {
        Field = field;
        Height = 30;
        _focusStroke = IslandTheme.Frozen(accent, 0.85);
        _frame = new Border
        {
            CornerRadius = new CornerRadius(9),
            Background = IslandTheme.FieldFill,
            BorderBrush = IslandTheme.FieldStroke,
            BorderThickness = new Thickness(1),
        };
        _placeholder = Ui.Text(placeholder, 13, IslandTheme.Tertiary, FontWeights.Medium);
        _placeholder.Margin = new Thickness(11, 0, 10, 0);
        _placeholder.IsHitTestVisible = false;
        Box = new TextBox
        {
            FontFamily = IslandTheme.Font,
            FontSize = 13,
            FontWeight = FontWeights.Medium,
            Foreground = IslandTheme.Primary,
            CaretBrush = IslandTheme.Primary,
            SelectionBrush = IslandTheme.Frozen(accent),
            Background = IslandTheme.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(7, 0, 8, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            AcceptsReturn = false,
            AcceptsTab = false,
            FocusVisualStyle = null,
            Tag = field,
        };
        SpellCheck.SetIsEnabled(Box, false);
        AutomationProperties.SetName(Box, placeholder);
        Children.Add(_frame);
        Children.Add(_placeholder);
        Children.Add(Box);
        Box.TextChanged += (_, _) => UpdatePlaceholder();
        Box.GotKeyboardFocus += (_, _) => _frame.BorderBrush = _focusStroke;
        Box.LostKeyboardFocus += (_, _) => _frame.BorderBrush = IslandTheme.FieldStroke;
    }

    public IslandField Field { get; }
    public TextBox Box { get; }
    public string Placeholder { get => _placeholder.Text; set => _placeholder.Text = value; }

    /// Pushes model text in without a feedback loop.
    public void Sync(string text)
    {
        if (Box.Text != text) Box.Text = text;
        UpdatePlaceholder();
    }

    private void UpdatePlaceholder() =>
        _placeholder.Visibility = Box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
}

/// Vector glyphs on a 16×16 grid (no icon font needed: Segoe Fluent Icons is not on every box).
internal static class Icons
{
    private static FrameworkElement Box(double size, params UIElement[] parts)
    {
        var canvas = new Canvas { Width = 16, Height = 16 };
        foreach (var p in parts) canvas.Children.Add(p);
        return new Viewbox { Width = size, Height = size, Child = canvas, VerticalAlignment = VerticalAlignment.Center };
    }

    private static Path Fill(Geometry g, Brush b) => new() { Data = g, Fill = b };
    private static Path Stroke(Geometry g, Brush b, double w) => new()
    {
        Data = g, Stroke = b, StrokeThickness = w, StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
    };

    public static FrameworkElement Note(Brush b, double size = 13)
    {
        var g = new GeometryGroup { FillRule = FillRule.Nonzero };
        g.Children.Add(new EllipseGeometry(new Point(4.6, 12.3), 2.7, 2.1));
        g.Children.Add(new EllipseGeometry(new Point(12.6, 10.6), 2.7, 2.1));
        g.Children.Add(new RectangleGeometry(new Rect(5.9, 3.4, 1.5, 9)));
        g.Children.Add(new RectangleGeometry(new Rect(13.9, 1.6, 1.5, 9)));
        g.Children.Add(Geometry.Parse("M5.9,3.4 L15.4,1.2 L15.4,3.6 L5.9,5.8 Z"));
        return Box(size, Fill(g, b));
    }

    public static FrameworkElement CheckCircle(Brush b, double size = 14) =>
        Box(size, Fill(new EllipseGeometry(new Point(8, 8), 8, 8), b),
            Stroke(Geometry.Parse("M4.6,8.3 L7,10.6 L11.5,5.6"), IslandTheme.Black, 1.9));

    public static FrameworkElement CrossCircle(Brush b, double size = 14) =>
        Box(size, Fill(new EllipseGeometry(new Point(8, 8), 8, 8), b),
            Stroke(Geometry.Parse("M5.4,5.4 L10.6,10.6 M10.6,5.4 L5.4,10.6"), IslandTheme.Black, 1.9));

    /// Octagon with a cross (Wrong).
    public static FrameworkElement CrossOctagon(Brush b, double size = 15) =>
        Box(size, Fill(Geometry.Parse("M5,0 L11,0 L16,5 L16,11 L11,16 L5,16 L0,11 L0,5 Z"), b),
            Stroke(Geometry.Parse("M5.4,5.4 L10.6,10.6 M10.6,5.4 L5.4,10.6"), IslandTheme.Black, 1.9));

    /// Seal with a check (Correct).
    public static FrameworkElement CheckSeal(Brush b, double size = 15)
    {
        var seal = new StreamGeometry();
        using (var ctx = seal.Open())
        {
            const int points = 16;
            for (var i = 0; i < points; i++)
            {
                var r = i % 2 == 0 ? 8 : 6.9;
                var a = Math.PI * 2 * i / points;
                var p = new Point(8 + r * Math.Cos(a), 8 + r * Math.Sin(a));
                if (i == 0) ctx.BeginFigure(p, true, true); else ctx.LineTo(p, true, true);
            }
        }
        seal.Freeze();
        return Box(size, Fill(seal, b), Stroke(Geometry.Parse("M4.8,8.3 L7,10.4 L11.3,5.8"), IslandTheme.Black, 1.8));
    }

    public static FrameworkElement Eye(Brush b, double size = 15) =>
        Box(size, Fill(Geometry.Parse("M0.5,8 C3,3 13,3 15.5,8 C13,13 3,13 0.5,8 Z"), b),
            Fill(new EllipseGeometry(new Point(8, 8), 2.6, 2.6), IslandTheme.Black));

    /// Speech bubble with "?" (a guess is awaited).
    public static FrameworkElement QuestionBubble(Brush b, double size = 15) =>
        Box(size, Fill(Geometry.Parse("M3,1 L13,1 Q15,1 15,3 L15,10 Q15,12 13,12 L7,12 L3.5,15 L4,12 L3,12 Q1,12 1,10 L1,3 Q1,1 3,1 Z"), b),
            Stroke(Geometry.Parse("M6.1,4.9 Q6.2,3.3 8,3.3 Q9.9,3.3 9.9,4.9 Q9.9,6 8.6,6.6 Q8,6.9 8,7.8"), IslandTheme.Black, 1.5),
            Fill(new EllipseGeometry(new Point(8, 9.8), 0.95, 0.95), IslandTheme.Black));

    public static FrameworkElement Warning(double size = 13) =>
        Box(size, Fill(Geometry.Parse("M8,0.8 Q8.9,0.8 9.4,1.7 L15.6,13.4 Q16,14.8 14.6,15 L1.4,15 Q0,14.8 0.4,13.4 L6.6,1.7 Q7.1,0.8 8,0.8 Z"), IslandTheme.YellowBrush),
            Stroke(Geometry.Parse("M8,5.2 L8,9.4"), IslandTheme.Black, 1.8),
            Fill(new EllipseGeometry(new Point(8, 12.1), 1.05, 1.05), IslandTheme.Black));

    public static FrameworkElement Info(Brush b, double size = 12) =>
        Box(size, Stroke(new EllipseGeometry(new Point(8, 8), 7, 7), b, 1.5),
            Stroke(Geometry.Parse("M8,7.2 L8,11.6"), b, 1.6), Fill(new EllipseGeometry(new Point(8, 4.7), 1, 1), b));

    public static FrameworkElement ErrorCircle(Brush b, double size = 12) =>
        Box(size, Fill(new EllipseGeometry(new Point(8, 8), 8, 8), b),
            Stroke(Geometry.Parse("M8,4 L8,9"), IslandTheme.Black, 1.8), Fill(new EllipseGeometry(new Point(8, 11.8), 1.05, 1.05), IslandTheme.Black));

    public static FrameworkElement Gear(Brush b, double size = 13)
    {
        var body = new GeometryGroup { FillRule = FillRule.Nonzero };
        body.Children.Add(new EllipseGeometry(new Point(8, 8), 5.4, 5.4));
        for (var i = 0; i < 8; i++)
        {
            var tooth = new RectangleGeometry(new Rect(6.7, 0.4, 2.6, 3.6), 0.6, 0.6)
            {
                Transform = new RotateTransform(i * 45, 8, 8),
            };
            body.Children.Add(tooth);
        }
        var gear = new CombinedGeometry(GeometryCombineMode.Exclude, body, new EllipseGeometry(new Point(8, 8), 2.3, 2.3));
        gear.Freeze();
        return Box(size, Fill(gear, b));
    }

    /// ↺: an open circle running anticlockwise, the arrowhead at the top pointing into the gap.
    public static FrameworkElement Restart(Brush b, double size = 13) =>
        Box(size,
            // From the top (8,2.5) clockwise round to the upper left (angle -150°): 300° of arc.
            Stroke(Geometry.Parse("M8,2.5 A5.5,5.5 0 1 1 3.24,5.25"), b, 1.8),
            Fill(Geometry.Parse("M4.9,2.5 L8.6,0 L8.6,5 Z"), b));

    public static FrameworkElement Close(Brush b, double size = 11) =>
        Box(size, Stroke(Geometry.Parse("M3,3 L13,13 M13,3 L3,13"), b, 2));
}

/// "5s 10s 15s": used tiers red, current white, later dim.
internal sealed class AttemptDotsView : StackPanel
{
    public AttemptDotsView() => Orientation = Orientation.Horizontal;

    public void Update(IReadOnlyList<AttemptPill> pills)
    {
        if (Children.Count != pills.Count)
        {
            Children.Clear();
            foreach (var _ in pills)
                Children.Add(new Border
                {
                    Height = 16, CornerRadius = new CornerRadius(8), Padding = new Thickness(6, 0, 6, 0),
                    Margin = new Thickness(4, 0, 0, 0), Child = Ui.Text("", 9.5, IslandTheme.Primary, FontWeights.Bold),
                });
        }
        for (var i = 0; i < pills.Count; i++)
        {
            var border = (Border)Children[i];
            var text = (TextBlock)border.Child;
            text.Text = pills[i].Label;
            (text.Foreground, border.Background) = pills[i].State switch
            {
                AttemptState.Current => (IslandTheme.Black, IslandTheme.Primary),
                AttemptState.Missed => (IslandTheme.RedBrush, IslandTheme.Frozen(IslandTheme.Red, 0.16)),
                _ => (IslandTheme.Tertiary, IslandTheme.FaintFill),
            };
        }
        AutomationProperties.SetName(this, $"Attempt {pills.Count(p => p.State != AttemptState.Later)} of {pills.Count}");
    }
}

/// Thin bar filling over the snippet (the engine has no timers; the view animates locally).
internal sealed class SnippetProgressBar : Grid
{
    private readonly Border _fill;

    public SnippetProgressBar()
    {
        Height = 4;
        Children.Add(new Border { CornerRadius = new CornerRadius(2), Background = IslandTheme.TrackFill });
        _fill = new Border { CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left, Background = IslandTheme.GreenBrush };
        Children.Add(_fill);
    }

    public void Update(double fraction, bool finished)
    {
        fraction = finished ? 1 : Math.Clamp(fraction, 0, 1);
        _fill.Background = finished ? IslandTheme.Frozen(Colors.White, 0.35) : IslandTheme.GreenBrush;
        var width = ActualWidth > 0 ? ActualWidth : Width;
        _fill.Width = double.IsNaN(width) ? 4 : Math.Max(4, width * fraction);
    }
}

/// Little animated equaliser shown while something plays.
internal sealed class EqualizerGlyph : StackPanel
{
    private readonly Rectangle[] _bars = new Rectangle[4];

    public EqualizerGlyph(Brush brush)
    {
        Orientation = Orientation.Horizontal;
        Height = 14;
        Width = 18;
        VerticalAlignment = VerticalAlignment.Center;
        for (var i = 0; i < _bars.Length; i++)
        {
            _bars[i] = new Rectangle
            {
                Width = 2.5, Height = 4, RadiusX = 1.25, RadiusY = 1.25, Fill = brush,
                Margin = new Thickness(i == 0 ? 0 : 2, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            };
            Children.Add(_bars[i]);
        }
    }

    public void Update(double seconds, bool playing)
    {
        for (var i = 0; i < _bars.Length; i++)
        {
            var phase = seconds * (5.0 + i * 1.3) + i * 1.7;
            _bars[i].Height = playing ? 4 + 8 * (0.5 + 0.5 * Math.Sin(phase)) : 4;
        }
    }
}

/// Light spinner.
internal sealed class Spinner : Grid
{
    private readonly RotateTransform _rotate = new();

    public Spinner(double size, double thickness)
    {
        Width = Height = size;
        var circumference = Math.PI * (size - thickness);
        Children.Add(new Ellipse
        {
            Stroke = IslandTheme.Primary, StrokeThickness = thickness,
            StrokeDashArray = new DoubleCollection { circumference * 0.7 / thickness, 100 },
            StrokeDashCap = PenLineCap.Round,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = _rotate,
        });
        AutomationProperties.SetName(this, "Loading");
    }

    public void Update(double seconds) => _rotate.Angle = seconds % 1 * 360;
}

/// Circular progress (set result, snippet ring).
internal sealed class Ring : Grid
{
    private readonly Path _arc;
    private readonly double _size;
    private readonly double _thickness;

    public Ring(double size, double thickness, Brush track)
    {
        _size = size;
        _thickness = thickness;
        Width = Height = size;
        Children.Add(new Ellipse { Stroke = track, StrokeThickness = thickness });
        _arc = new Path { StrokeThickness = thickness, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        Children.Add(_arc);
    }

    public void Update(double fraction, Brush brush)
    {
        _arc.Stroke = brush;
        _arc.Data = Arc(_size, _thickness, Math.Clamp(fraction, 0, 1));
    }

    public static Geometry Arc(double size, double thickness, double fraction)
    {
        var r = (size - thickness) / 2;
        var c = size / 2;
        if (fraction <= 0) return Geometry.Empty;
        if (fraction >= 0.9999)
            return new EllipseGeometry(new Point(c, c), r, r);
        var angle = fraction * 2 * Math.PI - Math.PI / 2;
        var start = new Point(c, c - r);
        var end = new Point(c + r * Math.Cos(angle), c + r * Math.Sin(angle));
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(start, false, false);
            ctx.ArcTo(end, new Size(r, r), 0, fraction > 0.5, SweepDirection.Clockwise, true, true);
        }
        g.Freeze();
        return g;
    }
}

/// ✓/✗ chip for one half of a verdict, with what the player typed.
internal sealed class VerdictChipView : Border
{
    public VerdictChipView(VerdictChip chip)
    {
        var tint = chip.Correct ? IslandTheme.Green : IslandTheme.Red;
        Background = IslandTheme.Frozen(tint, 0.1);
        CornerRadius = new CornerRadius(10);
        Height = 40;
        Padding = new Thickness(10, 0, 10, 0);
        var label = Ui.Row(4, Ui.Text(chip.Label, 10, IslandTheme.Secondary, FontWeights.SemiBold));
        if (chip.Hint is { } hint) label.Children.Add(new TextBlock
        {
            Text = $"· {hint}", FontFamily = IslandTheme.Font, FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = IslandTheme.RedBrush, Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
        });
        var guess = Ui.Text(chip.Guess.Length == 0 ? "–" : chip.Guess, 12, chip.Correct ? IslandTheme.Primary : IslandTheme.Frozen(Colors.White, 0.7), FontWeights.Medium);
        if (!chip.Correct)
            guess.TextDecorations = new TextDecorationCollection
            {
                new TextDecoration(TextDecorationLocation.Strikethrough, new Pen(IslandTheme.Frozen(IslandTheme.Red, 0.7), 1), 0,
                    TextDecorationUnit.FontRecommended, TextDecorationUnit.FontRecommended),
            };
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(7, 0, 0, 0) };
        texts.Children.Add(label);
        texts.Children.Add(guess);
        var icon = chip.Correct ? Icons.CheckCircle(IslandTheme.GreenBrush) : Icons.CrossCircle(IslandTheme.RedBrush);
        var dock = new DockPanel();
        DockPanel.SetDock(icon, Dock.Left);
        dock.Children.Add(icon);
        dock.Children.Add(texts);
        Child = dock;
    }
}
