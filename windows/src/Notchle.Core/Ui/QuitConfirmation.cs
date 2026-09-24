namespace Notchle.Core.Ui;

/// Two-step "quit playlist" (Tren, 2026-09-24: "also missing a complete quit button to put a
/// diff playlist in"). The first press arms a red "Quit playlist?" capsule for
/// <see cref="Window"/>; a second press inside that window confirms (the session sends
/// GameAction.Reset: stop, back to the link field, cleared songs kept). Otherwise it reverts by
/// itself. Pure, clock injected, so the timing is tested on any OS.
public sealed class QuitConfirmation
{
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(3);

    private readonly TimeProvider _clock;
    private DateTimeOffset? _armedAt;

    public QuitConfirmation(TimeProvider? clock = null) => _clock = clock ?? TimeProvider.System;

    /// Armed and still inside the confirmation window.
    public bool IsArmed => _armedAt is { } at && _clock.GetUtcNow() - at < Window && _clock.GetUtcNow() >= at;

    /// Arms, or confirms when already armed. Returns true when this press confirms.
    public bool Press()
    {
        if (IsArmed)
        {
            _armedAt = null;
            return true;
        }
        _armedAt = _clock.GetUtcNow();
        return false;
    }

    public void Disarm() => _armedAt = null;
}
