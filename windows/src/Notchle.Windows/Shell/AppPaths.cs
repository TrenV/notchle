using System.IO;

namespace Notchle.Windows.Shell;

public static class AppPaths
{
    /// %APPDATA%\Notchle: progress.json (ProgressStore) and spotify-tokens.bin (DPAPI).
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Notchle");

    /// The running executable (the single-file publish, or Notchle.Windows.exe in bin).
    public static string ExecutablePath => Environment.ProcessPath
        ?? Path.Combine(AppContext.BaseDirectory, "Notchle.Windows.exe");
}
