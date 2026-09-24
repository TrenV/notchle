namespace Notchle.Core;

// STUB: agent W1 replaces this. Public API frozen.
/// JSON file persistence; on Windows the directory is %APPDATA%\Notchle.
public sealed class ProgressStore(string directory)
{
    public string FilePath { get; } = Path.Combine(directory, "progress.json");

    /// Missing or unreadable file yields a default Progress, never a throw.
    public Progress Load() => new();

    public void Save(Progress progress) { }
}
