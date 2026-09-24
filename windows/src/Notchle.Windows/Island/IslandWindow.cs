using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Notchle.Core;
using Notchle.Core.Ui;

namespace Notchle.Windows.Island;

/// The Windows "notch": a true-black island hanging from the top edge of the monitor the user
/// works on. Wire it with <c>new IslandWindow(vm).Show()</c>.
///
/// - Borderless, transparent, topmost, WS_EX_TOOLWINDOW (no taskbar button, no Alt+Tab).
/// - WS_EX_NOACTIVATE until the island is clicked (or Ctrl+Alt+N): it never steals focus; after
///   the click it activates so the fields take typing, and hands the foreground back on collapse.
/// - Only the shape takes the mouse: the window is WS_EX_TRANSPARENT (click-through) whenever
///   the pointer is not over the shape, polled with GetCursorPos.
/// - Expansion rules live in Notchle.Core.Ui.IslandBehavior (tested headless).
/// - Follows the monitor the cursor settles on, sits below a top-docked taskbar, hides for
///   fullscreen apps / games / presentations, confetti in a separate overlay window.
public sealed class IslandWindow : Window
{
    private const int HotkeyId = 0x4E4C;
    private static readonly TimeSpan MonitorRefresh = TimeSpan.FromSeconds(2);

    private readonly NotchViewModel _vm;
    private readonly IslandView _view;
    private readonly IslandAnimator _animator;
    private readonly MonitorFollower _follower = new();
    private readonly ConfettiOverlayWindow _confetti;
    private readonly DispatcherTimer _tick;
    private readonly DispatcherTimer _fullscreenPoll;
    private IntPtr _hwnd;
    private GameState? _lastState;
    private bool _keyboardTaken;
    private IntPtr _previousForeground;
    private IReadOnlyList<MonitorInfo> _monitors = [];
    private DateTime _monitorsReadAt = DateTime.MinValue;
    private DateTime _lastTick = DateTime.UtcNow;
    private bool _clickThrough;
    private bool _reduceMotion;
    private bool _dirty = true;
    private IslandIndicator? _lastIndicator;
    private int _appliedFocusToken = -1;
    /// Focus still has to land in a field (the expanded content may not be visible yet).
    private bool _focusPending;
    private IslandTextField? _clickTarget;
    private bool _hotkeyRegistered;

    public IslandWindow(NotchViewModel vm)
    {
        _vm = vm;
        Session = new IslandSession { Send = a => _vm.Send(a) };
        // Created after the base constructor so the island, not the overlay, is the app's first window.
        _confetti = new ConfettiOverlayWindow();

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Title = "Notchle";
        SizeToContent = SizeToContent.Manual;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Width = IslandGeometry.WindowSize.Width;
        Height = IslandGeometry.WindowSize.Height;
        Left = 0;
        Top = 0;

        _reduceMotion = IslandTheme.ReduceMotion;
        _view = new IslandView(Session, vm, IslandTheme.ReadAccent());
        Content = _view;
        _lastState = vm.State;
        Session.StateDidChange(null, vm.State);
        _animator = new IslandAnimator(Session.Mode);
        _view.Update(_animator.Current, DateTimeOffset.UtcNow, _reduceMotion);

        vm.PropertyChanged += OnViewModelChanged;
        Session.Behavior.Changed += OnBehaviorChanged;
        _view.Changed += MarkDirty;
        foreach (var field in _view.Fields.All) Hook(field);

        SourceInitialized += OnSourceInitialized;
        Closed += OnClosed;
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewMouseLeftButtonDown += OnPreviewMouseDown;
        Deactivated += (_, _) => { Session.Behavior.KeyboardLost(); MarkDirty(); };
        DpiChanged += (_, _) => Reposition();

        _tick = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        _tick.Tick += (_, _) => OnTick();
        _fullscreenPoll = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _fullscreenPoll.Tick += (_, _) => PollFullscreen();
    }

    /// UI state (fields, focus, expansion rules). Internal so the demo can swap the link parser.
    internal IslandSession Session { get; }

    // MARK: Setup

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        IslandNative.SetExStyleBits(_hwnd, IslandNative.WS_EX_TOOLWINDOW | IslandNative.WS_EX_NOACTIVATE | IslandNative.WS_EX_TRANSPARENT, on: true);
        _clickThrough = true;
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        _hotkeyRegistered = IslandNative.RegisterHotKey(_hwnd, HotkeyId,
            IslandNative.MOD_CONTROL | IslandNative.MOD_ALT | IslandNative.MOD_NOREPEAT, IslandNative.VK_N);
        new WindowInteropHelper(_confetti).EnsureHandle();
        RefreshMonitors();
        _follower.Update(_monitors, IslandNative.CursorPx());
        Reposition();
        _tick.Start();
        _fullscreenPoll.Start();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _tick.Stop();
        _fullscreenPoll.Stop();
        _vm.PropertyChanged -= OnViewModelChanged;
        if (_hotkeyRegistered) IslandNative.UnregisterHotKey(_hwnd, HotkeyId);
        _confetti.Close();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case IslandNative.WM_HOTKEY when wParam.ToInt32() == HotkeyId:
                handled = true;
                Session.Behavior.Hotkey();
                MarkDirty();
                break;
            case IslandNative.WM_MOUSEACTIVATE when !Session.Behavior.HasKeyboard:
                // A click does not activate on its own; OnPreviewMouseDown decides.
                handled = true;
                return new IntPtr(IslandNative.MA_NOACTIVATE);
            case IslandNative.WM_DISPLAYCHANGE:
                _monitorsReadAt = DateTime.MinValue;
                break;
            case IslandNative.WM_SETTINGCHANGE:
                _monitorsReadAt = DateTime.MinValue; // taskbar moved / work area changed
                _reduceMotion = IslandTheme.ReduceMotion;
                _view.SetAccent(IslandTheme.ReadAccent());
                MarkDirty();
                break;
        }
        return IntPtr.Zero;
    }

    private void Hook(IslandTextField field)
    {
        field.Box.TextChanged += (_, _) =>
        {
            var text = field.Box.Text;
            switch (field.Field)
            {
                case IslandField.Url:
                    if (Session.UrlText != text) Session.UrlMessage = null;
                    Session.UrlText = text;
                    break;
                case IslandField.Title: Session.TitleText = text; break;
                case IslandField.Artist: Session.ArtistText = text; break;
            }
            UpdateFieldState();
            MarkDirty();
        };
        field.Box.GotKeyboardFocus += (_, _) => { Session.FocusedField = field.Field; UpdateFieldState(); };
        field.Box.LostKeyboardFocus += (_, _) =>
        {
            if (Session.FocusedField == field.Field) Session.FocusedField = null;
            UpdateFieldState();
        };
    }

    private void UpdateFieldState() =>
        Session.Behavior.SetFieldState(Session.FocusedField is not null, Session.HasTextInVisibleFields);

    // MARK: Game state

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnViewModelChanged(sender, e));
            return;
        }
        if (e.PropertyName == nameof(NotchViewModel.State) && !ReferenceEquals(_vm.State, _lastState))
        {
            var old = _lastState;
            _lastState = _vm.State;
            if (Session.StateDidChange(old, _vm.State)) Celebrate();
            UpdateFieldState();
        }
        MarkDirty();
    }

    private void Celebrate()
    {
        // Reduce Motion: the view pulses a glow from Session.CelebrationStart instead.
        if (_reduceMotion || Session.Behavior.IsSuppressed || !IsVisible || _follower.Current is not { } monitor) return;
        _confetti.Burst(monitor, IslandGeometry.ConfettiOrigin(monitor, _animator.Current.Height), Session.CelebrationSeed);
    }

    // MARK: Keyboard

    private void OnBehaviorChanged()
    {
        if (Session.Behavior.HasKeyboard && !_keyboardTaken) TakeKeyboard();
        else if (!Session.Behavior.HasKeyboard && _keyboardTaken) ReleaseKeyboard();
        MarkDirty();
    }

    /// Clicked (or hotkey): drop WS_EX_NOACTIVATE and activate so the fields take typing.
    private void TakeKeyboard()
    {
        _keyboardTaken = true;
        var foreground = IslandNative.GetForegroundWindow();
        if (foreground != _hwnd) _previousForeground = foreground;
        IslandNative.SetExStyleBits(_hwnd, IslandNative.WS_EX_NOACTIVATE, on: false);
        Activate();
        IslandNative.SetForegroundWindow(_hwnd);
        // Applied by the frame loop once the expanded content (and its fields) is visible.
        _focusPending = true;
    }

    /// Collapsed: back to non-activating, and give the foreground back to where it was.
    private void ReleaseKeyboard()
    {
        _keyboardTaken = false;
        IslandNative.SetExStyleBits(_hwnd, IslandNative.WS_EX_NOACTIVATE, on: true);
        if (IslandNative.GetForegroundWindow() == _hwnd && _previousForeground != IntPtr.Zero
            && IslandNative.IsWindow(_previousForeground))
            IslandNative.SetForegroundWindow(_previousForeground);
        _previousForeground = IntPtr.Zero;
        Keyboard.ClearFocus();
    }

    /// Moves keyboard focus to the clicked / requested / default field. Returns false while
    /// that field is not on screen yet (the frame loop retries).
    private bool ApplyFocus()
    {
        if (!_keyboardTaken || !Session.Behavior.IsExpanded) return true;
        _view.UpdateLayout();
        var requested = Session.RequestedFocus;
        var target = _clickTarget is { } clicked && _view.FieldFor(clicked.Field) is not null ? clicked
            : requested is { } r ? _view.FieldFor(r)
            : Session.FocusedField is { } f ? _view.FieldFor(f)
            : _view.FieldFor(IslandField.Url) ?? _view.FieldFor(IslandField.Title);
        if (target is null) { _clickTarget = null; return true; } // no field in this phase
        if (!target.IsVisible) return false;
        _clickTarget = null;
        _appliedFocusToken = Session.FocusToken;
        if (!target.Box.IsKeyboardFocused)
        {
            target.Box.Focus();
            Keyboard.Focus(target.Box);
            target.Box.CaretIndex = target.Box.Text.Length;
        }
        return true;
    }

    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        _clickTarget = FindField(e.OriginalSource as DependencyObject);
        var had = Session.Behavior.HasKeyboard;
        Session.Behavior.Click();
        if (had) _clickTarget = null; // already active: WPF focuses the clicked box itself
        MarkDirty();
    }

    private IslandTextField? FindField(DependencyObject? d)
    {
        while (d is not null)
        {
            if (d is IslandTextField field) return field;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return null;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.ImeProcessed or Key.DeadCharProcessed) return;
        Session.Behavior.KeyPressed();
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        var islandKey = IslandKeys.FromVirtualKey(KeyInterop.VirtualKeyFromKey(key),
            mods.HasFlag(ModifierKeys.Control), mods.HasFlag(ModifierKeys.Alt), mods.HasFlag(ModifierKeys.Shift));
        if (islandKey is not { } k) return;
        if (Session.HandleKey(k)) { e.Handled = true; MarkDirty(); }
        else if (k is IslandKey.Tab or IslandKey.BackTab) e.Handled = true; // keep focus in the island
    }

    // MARK: Frame loop

    private void MarkDirty()
    {
        _dirty = true;
        if (_tick.IsEnabled) _tick.Interval = TimeSpan.FromMilliseconds(16);
    }

    private void OnTick()
    {
        var utc = DateTime.UtcNow;
        var dt = Math.Clamp((utc - _lastTick).TotalSeconds, 0, 0.1);
        _lastTick = utc;

        if (utc - _monitorsReadAt > MonitorRefresh)
        {
            RefreshMonitors();
            IslandNative.BringToTopmost(_hwnd); // stay above other topmost windows that came later
        }
        var cursor = IslandNative.CursorPx();
        if (_follower.Update(_monitors, cursor)) Reposition();

        var inside = false;
        if (IsVisible && !Session.Behavior.IsSuppressed)
        {
            var rect = IslandNative.WindowRectPx(_hwnd);
            var scale = rect.Width > 0 ? rect.Width / IslandGeometry.WindowSize.Width : 1;
            inside = IslandGeometry.ShapeContains(Session.Mode, IslandGeometry.ToWindowDip(cursor, rect, scale));
        }
        SetClickThrough(!inside);
        Session.Behavior.PointerMoved(inside);
        Session.Behavior.Tick();

        _animator.SetTarget(Session.Mode);
        var settled = _animator.IsSettled;
        var indicator = Session.Indicator();
        var live = LiveAnimation();
        if (_dirty || !settled || live || !Equals(indicator, _lastIndicator))
        {
            var frame = _animator.Update(dt, _reduceMotion);
            _view.Update(frame, DateTimeOffset.UtcNow, _reduceMotion);
            _lastIndicator = indicator;
            _dirty = false;
        }
        if (_keyboardTaken && Session.FocusToken != _appliedFocusToken) _focusPending = true;
        if (_focusPending && _view.Frame.Expansion > 0.3) _focusPending = !ApplyFocus();
        // 60 fps while morphing, ~30 for the small live glyphs, a relaxed poll otherwise.
        var interval = !_animator.IsSettled ? 16 : live ? 33 : 50;
        if ((int)_tick.Interval.TotalMilliseconds != interval) _tick.Interval = TimeSpan.FromMilliseconds(interval);
    }

    /// Something on screen moves by itself (equaliser, ring, spinner, progress bar, glow).
    private bool LiveAnimation()
    {
        if (Session.Behavior.IsSuppressed) return false;
        var phase = Session.State.Phase;
        if (phase is GamePhase.PlayingSnippet or GamePhase.Loading) return true;
        if (phase is GamePhase.Correct or GamePhase.Revealed) return !_reduceMotion;
        return _reduceMotion && Session.CelebrationStart is { } c
            && (DateTimeOffset.UtcNow - c).TotalSeconds < CelebrationGlow.Duration + 0.1;
    }

    private void SetClickThrough(bool on)
    {
        if (on == _clickThrough || _hwnd == IntPtr.Zero) return;
        _clickThrough = on;
        IslandNative.SetExStyleBits(_hwnd, IslandNative.WS_EX_TRANSPARENT, on);
    }

    private void RefreshMonitors()
    {
        _monitorsReadAt = DateTime.UtcNow;
        _monitors = IslandNative.Monitors();
    }

    private void Reposition()
    {
        if (_hwnd == IntPtr.Zero || _follower.Current is not { } monitor) return;
        IslandNative.PlaceTopmost(_hwnd, IslandGeometry.WindowRectPx(monitor));
    }

    private void PollFullscreen()
    {
        var hide = IslandRules.HidesForNotificationState(IslandNative.UserNotificationState());
        if (hide == Session.Behavior.IsSuppressed) return;
        Session.Behavior.SetSuppressed(hide);
        if (hide) Hide();
        else
        {
            Show();
            Reposition();
        }
        MarkDirty();
    }
}
