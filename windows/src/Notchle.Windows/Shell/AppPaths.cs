using System.IO;

namespace Notchle.Windows.Shell;

public static class AppPaths
{
    /// %APPDATA%\Notchle: progress.json (ProgressStore), history.json (HistoryStore) and
    /// spotify-tokens.bin (DPAPI).
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Notchle");

    /// %LOCALAPPDATA%\Notchle\artwork: album covers, one &lt;trackId&gt;.jpg each (a cache).
    public static string ArtworkDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Notchle", "artwork");

    /// The running executable (the single-file publish, or Notchle.Windows.exe in bin).
    public static string ExecutablePath => Environment.ProcessPath
        ?? Path.Combine(AppContext.BaseDirectory, "Notchle.Windows.exe");
}
