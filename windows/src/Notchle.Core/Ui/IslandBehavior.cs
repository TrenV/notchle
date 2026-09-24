namespace Notchle.Core.Ui;

/// Expansion and keyboard rules of the Windows island. Pure (clock injected) so every rule is
/// unit-tested on any OS; the WPF window only feeds it pointer / key / focus events and applies
/// <see cref="IsExpanded"/> and <see cref="HasKeyboard"/>.
///
/// Rules (Tren, 2026-09-24, both platforms):
/// - Hover or click expands. Hover alone never takes the keyboard: typing starts only after
///   the user clicks the island (or presses the hotkey, which counts as a click). Until then the
///   window stays WS_EX_NOACTIVATE and never steals focus.
/// - "auto close on leaving with the mouse cursor please unless typing": the island collapses
///   <see cref="CollapseDelay"/> after the pointer leaves, unless the user is typing (a field has
///   focus AND has text, or a key was pressed in the last <see cref="TypingWindow"/>). Re-entering
///   cancels. The collapse is only armed by an actual leave, so a hotkey-opened island with the
///   pointer elsewhere stays open until Esc, a click elsewhere, or a leave.
/// - Collapsing always hands the keyboard back.
/// - Phase changes never expand the island (the collapsed pill shows state instead).
public sealed class IslandBehavior
{
    public static readonly TimeSpan CollapseDelay = TimeSpan.FromSeconds(0.4);
    public static readonly TimeSpan TypingWindow = TimeSpan.FromSeconds(2);

    private readonly TimeProvider _clock;
    private DateTimeOffset? _leftAt;
    private DateTimeOffset? _lastKeyAt;

    public IslandBehavior(TimeProvider? clock = null) => _clock = clock ?? TimeProvider.System;

    public bool IsExpanded { get; private set; }
    /// The island has been clicked (or opened by hotkey): the window may activate and take typing.
    public bool HasKeyboard { get; private set; }
    public bool IsPointerInside { get; private set; }
    public bool FieldFocused { get; private set; }
    public bool FieldsHaveText { get; private set; }
    /// Hidden for a fullscreen app / game / presentation.
    public bool IsSuppressed { get; private set; }

    /// Raised when <see cref="IsExpanded"/> or <see cref="HasKeyboard"/> changes.
    public event Action? Changed;

    private DateTimeOffset Now => _clock.GetUtcNow();

    public bool IsTyping =>
        FieldFocused && (FieldsHaveText || (_lastKeyAt is { } k && Now - k < TypingWindow));

    /// When a pending leave-collapse is due (it still waits while the user is typing).
    public DateTimeOffset? CollapseDue => _leftAt + CollapseDelay;

    /// The pointer is (not) over the island shape, hover slack included.
    public void PointerMoved(bool inside)
    {
        if (IsSuppressed) inside = false;
        if (inside == IsPointerInside) return;
        IsPointerInside = inside;
        if (inside)
        {
            _leftAt = null;
            Apply(expanded: true, keyboard: HasKeyboard);
        }
        else if (IsExpanded)
        {
            _leftAt = Now;
        }
    }

    /// A click on the island: expand and take the keyboard.
    public void Click()
    {
        if (IsSuppressed) return;
        _leftAt = null;
        Apply(expanded: true, keyboard: true);
    }

    /// Ctrl+Alt+N. Opens with the keyboard (counts as a click); closes when already open with it.
    public void Hotkey()
    {
        if (IsSuppressed) return;
        if (IsExpanded && HasKeyboard) { Collapse(); return; }
        _leftAt = null;
        Apply(expanded: true, keyboard: true);
    }

    public void KeyPressed() => _lastKeyAt = Now;

    /// Focus / text of the fields the current phase shows.
    public void SetFieldState(bool focused, bool hasText)
    {
        FieldFocused = focused;
        FieldsHaveText = hasText;
    }

    /// The window lost activation (the user clicked elsewhere). With the pointer away there is
    /// nothing left to keep the island open.
    public void KeyboardLost()
    {
        FieldFocused = false;
        if (!IsPointerInside) { Collapse(); return; }
        Apply(expanded: IsExpanded, keyboard: false);
    }

    /// Esc, or a collapse from the leave rule.
    public void Collapse()
    {
        _leftAt = null;
        Apply(expanded: false, keyboard: false);
    }

    /// Evaluates the leave rule; call it on every UI tick.
    public void Tick()
    {
        if (_leftAt is not { } left || IsPointerInside) return;
        if (Now - left < CollapseDelay || IsTyping) return;
        Collapse();
    }

    /// Fullscreen app in front: hide and drop everything.
    public void SetSuppressed(bool suppressed)
    {
        if (suppressed == IsSuppressed) return;
        IsSuppressed = suppressed;
        if (suppressed)
        {
            IsPointerInside = false;
            FieldFocused = false;
            Collapse();
        }
    }

    private void Apply(bool expanded, bool keyboard)
    {
        if (!expanded) keyboard = false;
        if (expanded == IsExpanded && keyboard == HasKeyboard) return;
        IsExpanded = expanded;
        HasKeyboard = keyboard;
        if (!expanded) FieldFocused = false;
        Changed?.Invoke();
    }
}
