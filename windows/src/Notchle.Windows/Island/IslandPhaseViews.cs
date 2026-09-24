using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using Notchle.Core;
using Notchle.Core.Ui;

namespace Notchle.Windows.Island;

/// What a phase view may use. The guess views never receive the track: only IslandScreen data.
internal sealed class IslandContext
{
    public required IslandSession Session { get; init; }
    public required NotchViewModel Vm { get; init; }
    public required Color Accent { get; init; }
    public required IslandTextField UrlField { get; init; }
    public required IslandTextField TitleField { get; init; }
    public required IslandTextField ArtistField { get; init; }
    /// Re-render after a click changed session state.
    public required Action Changed { get; init; }
    /// Album covers (answer screens and history rows only).
    public ArtworkImages Artwork { get; init; } = ArtworkImages.None;

    // One path for every action (the session forwards to the view model), so what a test or
    // the window hooks on Session.Send sees every button.
    public void Send(GameAction action) => Session.Send(action);

    public void SetPlayerMode(PlayerMode mode)
    {
        if (IslandRules.WithPlayerMode(Vm.Settings, mode) is { } updated) Vm.UpdateSettings(updated);
    }
}

/// One phase's content. Views either update in place (<see cref="Accepts"/> true for the whole
/// phase kind, e.g. the guess view across PlayingSnippet → Guessing so typing is never
/// interrupted) or are rebuilt when their data changes.
internal abstract class PhaseView : DockPanel
{
    protected PhaseView() => LastChildFill = true;

    public abstract bool Accepts(IslandScreen screen);
    public virtual void Update(IslandScreen screen, DateTimeOffset now, double seconds) { }

    protected void Top(UIElement e, double marginTop = 0)
    {
        if (e is FrameworkElement fe) fe.Margin = new Thickness(0, marginTop, 0, 0);
        SetDock(e, Dock.Top);
        Children.Add(e);
    }

    protected void Bottom(UIElement e, double marginTop = 0)
    {
        if (e is FrameworkElement fe) fe.Margin = new Thickness(0, marginTop, 0, 0);
        SetDock(e, Dock.Bottom);
        Children.Add(e);
    }

    /// Call last: the remaining space.
    protected void Fill(UIElement? e = null) => Children.Add(e ?? new Border());

    public static PhaseView Create(IslandScreen screen, IslandContext ctx) => screen switch
    {
        IslandScreen.SourceEntry => new SourceEntryView(ctx),
        IslandScreen.Loading l => new LoadingView(l),
        IslandScreen.Guess => new GuessView(ctx),
        IslandScreen.Wrong w => new WrongView(w, ctx),
        IslandScreen.Answer a => new AnswerView(a, ctx),
        IslandScreen.SetEnd s => new SetEndView(s, ctx),
        IslandScreen.Error e => new ErrorView(e, ctx),
        IslandScreen.Settings => new SettingsView(ctx),
        IslandScreen.History h => new HistoryView(h, ctx),
        _ => throw new ArgumentOutOfRangeException(nameof(screen), screen, null),
    };

    /// Wraps within <paramref name="maxLines"/> (shrinking a little first), then keeps wrapping:
    /// no height cap, the island grows instead.
    protected static FitText Wrapping(string text, double size, Brush brush, int maxLines)
    {
        var t = Ui.Fit(text, size, brush, maxLines);
        t.VerticalAlignment = VerticalAlignment.Top;
        return t;
    }
}

// MARK: Idle / Exhausted

internal sealed class SourceEntryView : PhaseView
{
    private readonly IslandContext _ctx;
    private readonly FitText _heading = Ui.Fit("", 15, IslandTheme.Primary, IslandTextFit.DefaultLines, FontWeights.SemiBold);
    private readonly FitText _subheading;
    private readonly DockPanel _message;
    private readonly TextBlock _messageText = Ui.Text("", 11, IslandTheme.RedBrush, FontWeights.Medium);
    private readonly FrameworkElement _messageIcon = Icons.ErrorCircle(IslandTheme.RedBrush, 11);

    public SourceEntryView(IslandContext ctx)
    {
        _ctx = ctx;
        _subheading = Wrapping("", 11.5, IslandTheme.Secondary, 2);
        Top(_heading);
        Top(_subheading, 3);
        _message = Ui.Leading(5, _messageIcon, _messageText);
        _message.MinHeight = 16;
        Bottom(_message, 8);
        var field = new DockPanel { LastChildFill = true };
        var load = new IslandButton(IslandScreen.SourceEntry.LoadLabel, KeyHints.Enter, IslandButton.Kind.Primary, () =>
        {
            ctx.Session.Load();
            ctx.Changed();
        }) { Margin = new Thickness(8, 0, 0, 0) };
        DockPanel.SetDock(load, Dock.Right);
        field.Children.Add(load);
        Ui.Detach(ctx.UrlField);
        field.Children.Add(ctx.UrlField);
        Bottom(field);
        Fill();
    }

    public override bool Accepts(IslandScreen screen) => screen is IslandScreen.SourceEntry;

    public override void Update(IslandScreen screen, DateTimeOffset now, double seconds)
    {
        var s = (IslandScreen.SourceEntry)screen;
        _heading.Text = s.Heading;
        _subheading.Text = s.Subheading;
        _ctx.UrlField.Placeholder = IslandScreen.SourceEntry.Placeholder;
        _ctx.UrlField.Sync(_ctx.Session.UrlText);
        if (s.UrlMessage is { } message)
        {
            _messageText.Text = message;
            _messageText.Foreground = IslandTheme.RedBrush;
            _messageIcon.Visibility = Visibility.Visible;
        }
        else
        {
            _messageText.Text = IslandScreen.SourceEntry.Footnote;
            _messageText.Foreground = IslandTheme.Tertiary;
            _messageIcon.Visibility = Visibility.Collapsed;
        }
    }
}

// MARK: Loading

internal sealed class LoadingView : PhaseView
{
    private readonly IslandScreen.Loading _screen;
    private readonly Spinner _spinner = new(22, 2.5);

    public LoadingView(IslandScreen.Loading screen)
    {
        _screen = screen;
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        _spinner.HorizontalAlignment = HorizontalAlignment.Center;
        stack.Children.Add(_spinner);
        var text = Ui.Text(screen.Text, 13, IslandTheme.Secondary, FontWeights.Medium);
        text.Margin = new Thickness(0, 12, 0, 0);
        text.HorizontalAlignment = HorizontalAlignment.Center;
        text.TextAlignment = TextAlignment.Center;
        stack.Children.Add(text);
        Fill(stack);
    }

    public override bool Accepts(IslandScreen screen) => Equals(screen, _screen);
    public override void Update(IslandScreen screen, DateTimeOffset now, double seconds) => _spinner.Update(seconds);
}

// MARK: PlayingSnippet / Guessing

internal sealed class GuessView : PhaseView
{
    private readonly IslandContext _ctx;
    private readonly EqualizerGlyph _eq = new(IslandTheme.GreenBrush);
    private readonly FrameworkElement _question = Icons.QuestionBubble(IslandTheme.Secondary);
    private readonly TextBlock _status = Ui.Text("", 12.5, IslandTheme.Primary, FontWeights.SemiBold);
    private readonly AttemptDotsView _dots = new();
    private readonly SnippetProgressBar _bar = new();
    private readonly IslandButton _submit;
    private readonly IslandButton _skip;

    public GuessView(IslandContext ctx)
    {
        _ctx = ctx;
        // ↺ replays the snippet at the same tier; the typed text and focus stay where they are.
        var replay = RestartButton.Create(IslandScreen.Guess.RestartLabel, ctx);
        Top(Ui.Bar(Ui.Leading(8, Ui.Row(8, _eq, _question), _status), Ui.Row(6, replay, _dots), 22));
        Top(_bar, 9);
        var fields = new Grid();
        fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Ui.Detach(ctx.TitleField);
        Ui.Detach(ctx.ArtistField);
        Grid.SetColumn(ctx.TitleField, 0);
        Grid.SetColumn(ctx.ArtistField, 2);
        fields.Children.Add(ctx.TitleField);
        fields.Children.Add(ctx.ArtistField);
        Top(fields, 12);
        var giveUp = new IslandButton(IslandScreen.Guess.GiveUpLabel, KeyHints.Esc, IslandButton.Kind.Quiet,
            () => ctx.Send(new GameAction.GiveUp()));
        // Forfeit this attempt for the next, longer tier; typed text and focus stay (the button
        // is not focusable). Hidden at the last tier, where only Give up is left.
        _skip = new IslandButton("Skip", KeyHints.CtrlShiftS, IslandButton.Kind.Secondary,
            () => ctx.Send(new GameAction.Skip())) { Margin = new Thickness(12, 0, 0, 0) };
        _submit = new IslandButton(IslandScreen.Guess.SubmitLabel, KeyHints.Enter, IslandButton.Kind.Primary, () =>
        {
            ctx.Session.SubmitGuess();
            ctx.Changed();
        });
        var leading = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        leading.Children.Add(giveUp);
        leading.Children.Add(_skip);
        Bottom(Ui.Bar(leading, _submit, 26));
        Fill();
    }

    public override bool Accepts(IslandScreen screen) => screen is IslandScreen.Guess;

    public override void Update(IslandScreen screen, DateTimeOffset now, double seconds)
    {
        var s = (IslandScreen.Guess)screen;
        _status.Text = s.Status;
        _eq.Visibility = s.Playing ? Visibility.Visible : Visibility.Collapsed;
        _question.Visibility = s.Playing ? Visibility.Collapsed : Visibility.Visible;
        _eq.Update(seconds, s.Playing);
        _dots.Update(s.Attempts);
        var elapsed = _ctx.Session.SnippetStart is { } start ? (now - start).TotalSeconds : 0;
        _bar.Update(s.Seconds > 0 ? elapsed / s.Seconds : 0, finished: !s.Playing);
        _ctx.TitleField.Placeholder = IslandScreen.Guess.TitlePlaceholder;
        _ctx.ArtistField.Placeholder = s.ArtistPlaceholder;
        _ctx.TitleField.Sync(_ctx.Session.TitleText);
        _ctx.ArtistField.Sync(_ctx.Session.ArtistText);
        _submit.Enabled = _ctx.Session.CanSubmitGuess;
        if (s.SkipLabel is { } skip && _skip.Label != skip) _skip.Label = skip;
        _skip.Visibility = s.SkipLabel is null ? Visibility.Collapsed : Visibility.Visible;
    }
}

/// The ↺ button of the guess, wrong and answer views. In Wrong it only replays the snippet;
/// Retry or Give up still decide.
internal static class RestartButton
{
    public static IslandIconButton Create(string label, IslandContext ctx) =>
        new(Icons.Restart(IslandTheme.Secondary), label, KeyHints.CtrlShiftR, () =>
        {
            ctx.Session.Restart();
            ctx.Changed();
        });
}

// MARK: Wrong

internal sealed class WrongView : PhaseView
{
    private readonly IslandScreen.Wrong _screen;

    public WrongView(IslandScreen.Wrong screen, IslandContext ctx)
    {
        _screen = screen;
        var dots = new AttemptDotsView();
        dots.Update(screen.Attempts);
        var replay = RestartButton.Create(IslandScreen.Guess.RestartLabel, ctx);
        Top(Ui.Bar(Ui.Leading(8, Icons.CrossOctagon(IslandTheme.RedBrush), Ui.Text(screen.Headline, 12.5, IslandTheme.Primary, FontWeights.SemiBold)), Ui.Row(6, replay, dots), 22));
        var chips = new Grid();
        chips.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        chips.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        chips.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var title = new VerdictChipView(screen.TitleChip);
        var artist = new VerdictChipView(screen.ArtistChip);
        Grid.SetColumn(artist, 2);
        chips.Children.Add(title);
        chips.Children.Add(artist);
        Top(chips, 12);
        Bottom(Ui.Bar(
            new IslandButton(IslandScreen.Wrong.GiveUpLabel, KeyHints.Esc, IslandButton.Kind.Quiet, () => ctx.Send(new GameAction.GiveUp())),
            new IslandButton(screen.RetryLabel, KeyHints.CtrlR, IslandButton.Kind.Primary, () => ctx.Send(new GameAction.Retry())),
            26));
        Fill();
    }

    public override bool Accepts(IslandScreen screen) =>
        screen is IslandScreen.Wrong w && Equals(w with { Attempts = _screen.Attempts }, _screen)
        && w.Attempts.SequenceEqual(_screen.Attempts);
}

// MARK: Correct / Revealed

internal sealed class AnswerView : PhaseView
{
    private readonly IslandScreen.Answer _screen;
    private readonly EqualizerGlyph _eq = new(IslandTheme.Secondary);

    public AnswerView(IslandScreen.Answer screen, IslandContext ctx)
    {
        _screen = screen;
        var headline = Ui.Text(screen.Headline, 12, screen.Correct ? IslandTheme.GreenBrush : IslandTheme.Secondary, FontWeights.SemiBold);
        var icon = screen.Correct ? Icons.CheckSeal(IslandTheme.GreenBrush) : Icons.Eye(IslandTheme.Secondary);
        // ↺ restarts the whole song from 0:00; the answer stays on screen.
        var restart = RestartButton.Create(IslandScreen.Answer.RestartLabel, ctx);
        Top(Ui.Bar(Ui.Leading(6, icon, headline), Ui.Row(8, restart, _eq), 22));
        // Cover left of title / artists. Without one (not resolved, offline) it collapses and
        // the texts sit where they always did.
        _ctx = ctx;
        _cover = new CoverImage(64, 8, shadow: true) { Margin = new Thickness(0, 0, 12, 0) };
        _coverTrackId = ctx.Session.State.CurrentTrack?.Id ?? "";
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        // Long titles wrap (3 lines), then shrink a little, then the island grows: never "…".
        texts.Children.Add(Ui.Fit(screen.Title, 19, IslandTheme.Primary, IslandTextFit.AnswerTitleLines, FontWeights.Bold));
        var artists = Ui.Fit(screen.Artists, 13, IslandTheme.Secondary, IslandTextFit.AnswerArtistLines, FontWeights.Medium);
        artists.Margin = new Thickness(0, 2, 0, 0);
        texts.Children.Add(artists);
        var row = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(_cover, Dock.Left);
        row.Children.Add(_cover);
        row.Children.Add(texts);
        Top(row, 10);
        ShowCover(animate: false);
        UIElement? hint = screen.PreviewHint is { } h
            ? Ui.Leading(5, Icons.Info(IslandTheme.Tertiary, 11), Ui.Text(h, 11, IslandTheme.Tertiary, FontWeights.Medium))
            : null;
        Bottom(Ui.Bar(hint, new IslandButton(IslandScreen.Answer.NextLabel, KeyHints.Enter, IslandButton.Kind.Primary,
            () => ctx.Send(new GameAction.Next())), 26));
        Fill();
    }

    private readonly IslandContext _ctx;
    private readonly CoverImage _cover;
    private readonly string _coverTrackId;

    public override bool Accepts(IslandScreen screen) => Equals(screen, _screen);

    public override void Update(IslandScreen screen, DateTimeOffset now, double seconds)
    {
        _eq.Update(seconds, playing: true);
        ShowCover(animate: true);
    }

    /// Only this screen's ArtworkUrl (set through IslandRules.RevealedArtwork) is ever drawn.
    private void ShowCover(bool animate)
    {
        var image = _ctx.Artwork.For(_coverTrackId, _screen.ArtworkUrl);
        _cover.Show(image, animate);
        _cover.Visibility = image is null ? Visibility.Collapsed : Visibility.Visible;
    }
}

/// Rounded album cover (or, with a placeholder, a note while there is none). Fades in when the
/// image arrives after the view was built.
internal sealed class CoverImage : Border
{
    private readonly FrameworkElement? _placeholder;
    private ImageSource? _shown;

    public CoverImage(double size, double radius, bool shadow, bool placeholder = false)
    {
        Width = Height = size;
        CornerRadius = new CornerRadius(radius);
        VerticalAlignment = VerticalAlignment.Center;
        Background = IslandTheme.FaintFill;
        if (shadow) Effect = new DropShadowEffect { BlurRadius = 10, ShadowDepth = 2, Opacity = 0.45, Color = Colors.Black };
        if (placeholder)
        {
            _placeholder = Icons.Note(IslandTheme.Tertiary, size * 0.45);
            _placeholder.HorizontalAlignment = HorizontalAlignment.Center;
            Child = _placeholder;
        }
    }

    public bool HasImage => _shown is not null;

    public void Show(ImageSource? image, bool animate)
    {
        if (ReferenceEquals(image, _shown)) return;
        _shown = image;
        Background = image is null ? IslandTheme.FaintFill : new ImageBrush(image) { Stretch = Stretch.UniformToFill };
        if (_placeholder is not null) _placeholder.Visibility = image is null ? Visibility.Visible : Visibility.Collapsed;
        if (image is not null && animate)
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(250)));
        else
        {
            BeginAnimation(OpacityProperty, null);
            Opacity = 1;
        }
    }
}

// MARK: SetComplete / SetFailed

internal sealed class SetEndView : PhaseView
{
    private readonly IslandScreen.SetEnd _screen;

    public SetEndView(IslandScreen.SetEnd screen, IslandContext ctx)
    {
        _screen = screen;
        // Choices along the bottom, primary (Enter) on the right. Only the primary draws its key
        // hint (three hints do not fit the island's width); the others carry theirs as a tooltip.
        // A WrapPanel: long labels move a button to a second line instead of running off the island.
        var buttons = new WrapPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var ordered = new[] { screen.Tertiary, screen.Secondary, screen.Primary };
        foreach (var option in ordered)
        {
            if (option is null) continue;
            var primary = ReferenceEquals(option, screen.Primary);
            var choice = option.Choice;
            var button = new IslandButton(option.Label, option.KeyHint,
                primary ? IslandButton.Kind.Primary : option.Choice == SetChoice.Replay ? IslandButton.Kind.Secondary : IslandButton.Kind.Quiet,
                () => ctx.Send(new GameAction.StartSet(choice)))
            { Margin = new Thickness(buttons.Children.Count == 0 ? 0 : 8, 2, 0, 2), ShowsHint = primary, ToolTip = option.KeyHint };
            buttons.Children.Add(button);
        }
        Bottom(Ui.Bar(null, buttons, 26), 8);

        var top = new DockPanel { LastChildFill = true };
        var ring = new Ring(64, 5, IslandTheme.TrackFill);
        ring.Update(screen.Total > 0 ? (double)screen.Correct / screen.Total : 0,
            screen.Complete ? IslandTheme.GreenBrush : IslandTheme.Frozen(Colors.White, 0.8));
        var count = Ui.Text($"{screen.Correct}/{screen.Total}", 15, IslandTheme.Primary, FontWeights.Bold);
        count.HorizontalAlignment = HorizontalAlignment.Center;
        ring.Children.Add(count);
        ring.VerticalAlignment = VerticalAlignment.Center;
        ring.Margin = new Thickness(0, 0, 16, 0);
        DockPanel.SetDock(ring, Dock.Left);
        top.Children.Add(ring);

        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(Ui.Fit(screen.Headline, 16, screen.Complete ? IslandTheme.GreenBrush : IslandTheme.Primary, IslandTextFit.DefaultLines, FontWeights.Bold));
        var body = Wrapping(screen.Body, 12, IslandTheme.Secondary, 2);
        body.Margin = new Thickness(0, 4, 0, 0);
        texts.Children.Add(body);
        top.Children.Add(texts);
        Fill(top);
    }

    public override bool Accepts(IslandScreen screen) => Equals(screen, _screen);
}

// MARK: Error

internal sealed class ErrorView : PhaseView
{
    private readonly IslandScreen.Error _screen;

    public ErrorView(IslandScreen.Error screen, IslandContext ctx)
    {
        _screen = screen;
        var texts = new StackPanel { Margin = new Thickness(10, 0, 0, 0) };
        texts.Children.Add(Ui.Fit(IslandScreen.Error.Heading, 14, IslandTheme.Primary, IslandTextFit.DefaultLines, FontWeights.SemiBold));
        var message = Wrapping(screen.Message, 12, IslandTheme.Secondary, 3);
        message.Margin = new Thickness(0, 3, 0, 0);
        texts.Children.Add(message);
        var icon = Icons.Warning(18);
        icon.VerticalAlignment = VerticalAlignment.Top;
        var row = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);
        row.Children.Add(texts);
        Top(row);
        Bottom(Ui.Bar(
            new IslandButton(IslandScreen.Error.ResetLabel, null, IslandButton.Kind.Secondary, () => ctx.Send(new GameAction.Reset())),
            new IslandButton(IslandScreen.Error.SkipLabel, KeyHints.Enter, IslandButton.Kind.Primary, () => ctx.Send(new GameAction.Next())),
            26));
        Fill();
    }

    public override bool Accepts(IslandScreen screen) => Equals(screen, _screen);
}

// MARK: Settings

internal sealed class SettingsView : PhaseView
{
    private readonly TextBlock _nowUsing = Ui.Text("", 11, IslandTheme.Tertiary, FontWeights.Medium);
    private readonly TextBlock _snippets = Ui.Text("", 12, IslandTheme.Frozen(Colors.White, 0.85), FontWeights.SemiBold);
    private readonly FitText _hint;
    private readonly (Border Segment, TextBlock Label, PlayerMode Mode)[] _segments;

    public SettingsView(IslandContext ctx)
    {
        Top(Ui.Bar(Ui.Text(IslandScreen.Settings.Heading, 15, IslandTheme.Primary, FontWeights.SemiBold), _nowUsing, 20));

        var picker = new StackPanel { Orientation = Orientation.Horizontal };
        var pickerFrame = new Border
        {
            CornerRadius = new CornerRadius(8), Background = IslandTheme.FaintFill, Padding = new Thickness(2),
            Child = picker, VerticalAlignment = VerticalAlignment.Center,
        };
        _segments = new[] { (PlayerMode.Preview, IslandScreen.Settings.PreviewLabel), (PlayerMode.SpotifyConnect, IslandScreen.Settings.ConnectLabel) }
            .Select(x =>
            {
                var label = Ui.Text(x.Item2, 11.5, IslandTheme.Secondary, FontWeights.SemiBold);
                var segment = new Border
                {
                    CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 2, 10, 2), MinHeight = 22,
                    Child = label, Cursor = Cursors.Hand, Background = IslandTheme.Transparent,
                };
                var mode = x.Item1;
                segment.MouseLeftButtonUp += (_, e) => { e.Handled = true; ctx.SetPlayerMode(mode); ctx.Changed(); };
                picker.Children.Add(segment);
                return (segment, label, mode);
            }).ToArray();

        Top(Ui.Bar(Ui.Text(IslandScreen.Settings.PlayWithLabel, 12, IslandTheme.Secondary, FontWeights.Medium), pickerFrame, 26), 8);
        Top(Ui.Bar(Ui.Text(IslandScreen.Settings.SnippetsLabel, 12, IslandTheme.Secondary, FontWeights.Medium), _snippets, 24), 4);

        _hint = Wrapping("", 10.5, IslandTheme.Tertiary, 2);
        _hint.VerticalAlignment = VerticalAlignment.Center;
        _hint.Margin = new Thickness(0, 0, 12, 0);
        var done = new IslandButton(IslandScreen.Settings.DoneLabel, KeyHints.Esc, IslandButton.Kind.Secondary, () =>
        {
            ctx.Session.ShowingSettings = false;
            ctx.Changed();
        });
        var footer = Ui.Bar(null, done, 30);
        // The hint takes the space left of the button.
        footer.Children.RemoveAt(footer.Children.Count - 1);
        footer.Children.Add(_hint);
        Bottom(footer);
        Fill();
    }

    public override bool Accepts(IslandScreen screen) => screen is IslandScreen.Settings;

    public override void Update(IslandScreen screen, DateTimeOffset now, double seconds)
    {
        var s = (IslandScreen.Settings)screen;
        _nowUsing.Text = s.NowUsing;
        _snippets.Text = s.Snippets;
        _hint.Text = s.ConnectHint;
        foreach (var (segment, label, mode) in _segments)
        {
            var selected = mode == s.Mode;
            segment.Background = selected ? IslandTheme.Frozen(Colors.White, 0.2) : IslandTheme.Transparent;
            label.Foreground = selected ? IslandTheme.Primary : IslandTheme.Secondary;
        }
    }
}
