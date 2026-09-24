namespace Notchle.Windows.Shell;

/// One Notchle per Windows session: a named mutex. A second launch signals the first (which
/// shows the island) through a named event and exits.
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activate;
    private readonly RegisteredWaitHandle? _registration;

    public bool IsFirst { get; }

    /// <param name="name">Unique per app; "Local\" scopes it to this logon session.</param>
    /// <param name="onActivate">Called (on a thread-pool thread) when another launch asks.</param>
    public SingleInstance(string name, Action onActivate)
    {
        _mutex = new Mutex(initiallyOwned: true, $@"Local\{name}.instance", out var createdNew);
        _activate = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\{name}.activate");
        IsFirst = createdNew;
        if (IsFirst)
        {
            _registration = ThreadPool.RegisterWaitForSingleObject(
                _activate, (_, _) => onActivate(), null, Timeout.Infinite, executeOnlyOnce: false);
        }
    }

    /// From a second instance: ask the first one to come forward.
    public void ActivateFirst() => _activate.Set();

    public void Dispose()
    {
        _registration?.Unregister(null);
        if (IsFirst) _mutex.ReleaseMutex();
        _mutex.Dispose();
        _activate.Dispose();
    }
}
