namespace Notchle.Core.Ui;

// STUB: agent W2 replaces this. Lives in Core (not the WPF project) so it is unit-testable on
// any OS. Platform-neutral rules for the Windows "island" (same rules as the macOS notch):
// expand on hover or click; collapse ~0.4s after the pointer leaves unless the user is typing
// (a field has focus AND has text or a key was pressed in the last ~2s); phase changes do not
// auto-expand; typing only after a click.
public sealed class IslandBehavior
{
}
