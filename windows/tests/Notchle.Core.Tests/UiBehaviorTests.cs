using Notchle.Core.Ui;

namespace Notchle.Core.Tests;

/// Tren's interaction rules: expand on hover or click, type only after a click, collapse 0.4s
/// after the pointer leaves unless typing, re-entering cancels.
public class UiBehaviorTests
{
    private readonly UiManualClock _clock = new();
    private readonly IslandBehavior _b;

    public UiBehaviorTests() => _b = new IslandBehavior(_clock);

    private void Wait(double seconds)
    {
        // Tick like the UI timer does (every ~16 ms), not just once at the end.
        for (var t = 0.0; t < seconds; t += 0.016) { _clock.Advance(Math.Min(0.016, seconds - t)); _b.Tick(); }
    }

    [Fact]
    public void StartsCollapsedWithoutTheKeyboard()
    {
        Assert.False(_b.IsExpanded);
        Assert.False(_b.HasKeyboard);
    }

    [Fact]
    public void HoverExpandsButNeverTakesTheKeyboard()
    {
        _b.PointerMoved(true);
        Assert.True(_b.IsExpanded);
        Assert.False(_b.HasKeyboard);
    }

    [Fact]
    public void ClickExpandsAndTakesTheKeyboard()
    {
        _b.Click();
        Assert.True(_b.IsExpanded);
        Assert.True(_b.HasKeyboard);
    }

    [Fact]
    public void LeavingCollapsesAfterTheDelayNotBefore()
    {
        _b.PointerMoved(true);
        _b.PointerMoved(false);
        Wait(0.38);
        Assert.True(_b.IsExpanded);
        Wait(0.04);
        Assert.False(_b.IsExpanded);
    }

    [Fact]
    public void ReenteringCancelsThePendingCollapse()
    {
        _b.PointerMoved(true);
        _b.PointerMoved(false);
        Wait(0.3);
        _b.PointerMoved(true);
        Assert.Null(_b.CollapseDue);
        Wait(2);
        Assert.True(_b.IsExpanded);
    }

    [Fact]
    public void TypedTextInAFocusedFieldKeepsItOpenAfterLeaving()
    {
        _b.Click();
        _b.SetFieldState(focused: true, hasText: true);
        _b.PointerMoved(true);
        _b.PointerMoved(false);
        Wait(10);
        Assert.True(_b.IsExpanded);
        Assert.True(_b.HasKeyboard);

        // Once the field is cleared (and no key since long) the pending leave takes effect.
        _b.SetFieldState(focused: true, hasText: false);
        Wait(0.05);
        Assert.False(_b.IsExpanded);
        Assert.False(_b.HasKeyboard);
    }

    [Fact]
    public void ARecentKeyPressCountsAsTypingForTwoSeconds()
    {
        _b.Click();
        _b.SetFieldState(focused: true, hasText: false);
        _b.PointerMoved(true);
        _b.KeyPressed();
        _b.PointerMoved(false);
        Wait(1.9);
        Assert.True(_b.IsExpanded);
        Wait(0.15);
        Assert.False(_b.IsExpanded);
    }

    [Fact]
    public void AFocusedEmptyFieldWithoutKeysIsNotTyping()
    {
        _b.Click();
        _b.SetFieldState(focused: true, hasText: false);
        _b.PointerMoved(true);
        _b.PointerMoved(false);
        Wait(0.45);
        Assert.False(_b.IsExpanded);
    }

    [Fact]
    public void TextWithoutFocusIsNotTyping()
    {
        _b.PointerMoved(true);
        _b.SetFieldState(focused: false, hasText: true);
        _b.KeyPressed();
        _b.PointerMoved(false);
        Wait(0.45);
        Assert.False(_b.IsExpanded);
    }

    [Fact]
    public void CollapsingHandsTheKeyboardBackAndRaisesChanged()
    {
        var changes = 0;
        _b.Changed += () => changes++;
        _b.Click();
        Assert.Equal(1, changes);
        _b.Click();
        Assert.Equal(1, changes); // no change, no event
        _b.Collapse();
        Assert.Equal(2, changes);
        Assert.False(_b.HasKeyboard);
    }

    [Fact]
    public void HoverAfterAClickKeepsTheKeyboard()
    {
        _b.Click();
        _b.PointerMoved(true);
        Assert.True(_b.HasKeyboard);
    }

    [Fact]
    public void HotkeyOpensWithTheKeyboardAndTogglesClosed()
    {
        _b.Hotkey();
        Assert.True(_b.IsExpanded);
        Assert.True(_b.HasKeyboard);
        _b.Hotkey();
        Assert.False(_b.IsExpanded);
        Assert.False(_b.HasKeyboard);
    }

    [Fact]
    public void HotkeyOnAHoverOpenedIslandTakesTheKeyboardInsteadOfClosing()
    {
        _b.PointerMoved(true);
        _b.Hotkey();
        Assert.True(_b.IsExpanded);
        Assert.True(_b.HasKeyboard);
    }

    [Fact]
    public void HotkeyOpenedWithThePointerElsewhereStaysOpen()
    {
        // No leave happened, so the leave rule is not armed.
        _b.Hotkey();
        Wait(5);
        Assert.True(_b.IsExpanded);
    }

    [Fact]
    public void LosingActivationWithThePointerAwayCollapses()
    {
        _b.Hotkey();
        _b.KeyboardLost();
        Assert.False(_b.IsExpanded);
        Assert.False(_b.HasKeyboard);
    }

    [Fact]
    public void LosingActivationWhileHoveredKeepsItOpenWithoutTheKeyboard()
    {
        _b.Click();
        _b.PointerMoved(true);
        _b.KeyboardLost();
        Assert.True(_b.IsExpanded);
        Assert.False(_b.HasKeyboard);
        _b.PointerMoved(false);
        Wait(0.45);
        Assert.False(_b.IsExpanded);
    }

    [Fact]
    public void LeavingACollapsedIslandArmsNothing()
    {
        _b.PointerMoved(true);
        _b.Collapse();          // Esc while hovered
        _b.PointerMoved(false);
        Assert.Null(_b.CollapseDue);
    }

    [Fact]
    public void SuppressionCollapsesAndIgnoresInputUntilLifted()
    {
        _b.Click();
        _b.SetSuppressed(true);
        Assert.False(_b.IsExpanded);
        _b.PointerMoved(true);
        _b.Click();
        _b.Hotkey();
        Assert.False(_b.IsExpanded);
        _b.SetSuppressed(false);
        _b.PointerMoved(true);
        Assert.True(_b.IsExpanded);
    }
}
