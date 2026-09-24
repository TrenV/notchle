using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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

    public void Send(GameAction action) => Vm.Send(action);

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
        _ => throw new ArgumentOutOfRangeException(nameof(screen), screen, null),
    };

    protected static TextBlock Wrapping(string text, double size, Brush brush, int maxLines)
    {
        var t = Ui.Text(text, size, brush);
        t.TextWrapping = TextWrapping.Wrap;
        t.MaxHeight = Math.Ceiling(size * 1.36 * maxLines);
        t.VerticalAlignment = VerticalAlignment.Top;
        return t;
    }
}

// MARK: Idle / Exhausted

internal sealed class SourceEntryView : PhaseView
{
    private readonly IslandContext _ctx;
    private readonly TextBlock _heading = Ui.Text("", 15, IslandTheme.Primary, FontWeights.SemiBold);
    private readonly TextBlock _subheading;
    private readonly StackPanel _message;
    private readonly TextBlock _messageText = Ui.Text("", 11, IslandTheme.RedBrush, FontWeights.Medium);
    private readonly FrameworkElement _messageIcon = Icons.ErrorCircle(IslandTheme.RedBrush, 11);

    public SourceEntryView(IslandContext ctx)
    {
        _ctx = ctx;
        _subheading = Wrapping("", 11.5, IslandTheme.Secondary, 2);
        Top(_heading);
        Top(_subheading, 3);
        _message = Ui.Row(5, _messageIcon, _messageText);
        _message.Height = 16;
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

    public GuessView(IslandContext ctx)
    {
        _ctx = ctx;
        Top(Ui.Bar(Ui.Row(8, _eq, _question, _status), _dots, 18));
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
        _submit = new IslandButton(IslandScreen.Guess.SubmitLabel, KeyHints.Enter, IslandButton.Kind.Primary, () =>
        {
            ctx.Session.SubmitGuess();
            ctx.Changed();
        });
        Bottom(Ui.Bar(giveUp, _submit, 26));
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
    }
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
        Top(Ui.Bar(Ui.Row(8, Icons.CrossOctagon(IslandTheme.RedBrush), Ui.Text(screen.Headline, 12.5, IslandTheme.Primary, FontWeights.SemiBold)), dots, 18));
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
        Top(Ui.Bar(Ui.Row(6, icon, headline), _eq, 18));
        Top(Ui.Text(screen.Title, 19, IslandTheme.Primary, FontWeights.Bold), 10);
        Top(Ui.Text(screen.Artists, 13, IslandTheme.Secondary, FontWeights.Medium), 2);
        UIElement? hint = screen.PreviewHint is { } h
            ? Ui.Row(5, Icons.Info(IslandTheme.Tertiary, 11), Ui.Text(h, 11, IslandTheme.Tertiary, FontWeights.Medium))
            : null;
        Bottom(Ui.Bar(hint, new IslandButton(IslandScreen.Answer.NextLabel, KeyHints.Enter, IslandButton.Kind.Primary,
            () => ctx.Send(new GameAction.Next())), 26));
        Fill();
    }

    public override bool Accepts(IslandScreen screen) => Equals(screen, _screen);
    public override void Update(IslandScreen screen, DateTimeOffset now, double seconds) => _eq.Update(seconds, playing: true);
}

// MARK: SetComplete / SetFailed

internal sealed class SetEndView : PhaseView
{
    private readonly IslandScreen.SetEnd _screen;

    public SetEndView(IslandScreen.SetEnd screen, IslandContext ctx)
    {
        _screen = screen;
        var ring = new Ring(78, 5, IslandTheme.TrackFill);
        ring.Update(screen.Total > 0 ? (double)screen.Correct / screen.Total : 0,
            screen.Complete ? IslandTheme.GreenBrush : IslandTheme.Frozen(Colors.White, 0.8));
        var count = Ui.Text($"{screen.Correct}/{screen.Total}", 17, IslandTheme.Primary, FontWeights.Bold);
        count.HorizontalAlignment = HorizontalAlignment.Center;
        ring.Children.Add(count);
        ring.VerticalAlignment = VerticalAlignment.Center;
        ring.Margin = new Thickness(0, 0, 18, 0);
        SetDock(ring, Dock.Left);
        Children.Add(ring);

        var right = new DockPanel { LastChildFill = true };
        var headline = Ui.Text(screen.Headline, 16, screen.Complete ? IslandTheme.GreenBrush : IslandTheme.Primary, FontWeights.Bold);
        SetDock(headline, Dock.Top);
        right.Children.Add(headline);
        var body = Wrapping(screen.Body, 12, IslandTheme.Secondary, 2);
        body.Margin = new Thickness(0, 4, 0, 0);
        SetDock(body, Dock.Top);
        right.Children.Add(body);
        var button = new IslandButton(screen.ButtonLabel, KeyHints.Enter, IslandButton.Kind.Primary,
            () => ctx.Send(screen.Complete ? new GameAction.NextSet() : new GameAction.ReplaySet()))
        { HorizontalAlignment = HorizontalAlignment.Right };
        SetDock(button, Dock.Bottom);
        right.Children.Add(button);
        right.Children.Add(new Border());
        right.Margin = new Thickness(0, 8, 0, 0);
        Fill(right);
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
        texts.Children.Add(Ui.Text(IslandScreen.Error.Heading, 14, IslandTheme.Primary, FontWeights.SemiBold));
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
    private readonly TextBlock _hint;
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
                    CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 0, 10, 0), Height = 22,
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
