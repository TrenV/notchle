using Microsoft.Win32;

namespace Notchle.Windows.Shell;

/// "Start with Windows": a value under HKCU\Software\Microsoft\Windows\CurrentVersion\Run.
/// Per user, no admin rights. The key path is injectable so tests use a scratch key.
public sealed class StartupRegistration(
    string executablePath,
    string valueName = "Notchle",
    string runKeyPath = StartupRegistration.RunKey)
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// Quoted, so a path with spaces isn't split.
    public string Command => $"\"{executablePath}\"";

    /// True only when the value points at this executable (a stale path from an old copy
    /// counts as off, and turning it on rewrites it).
    public bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(runKeyPath);
            return key?.GetValue(valueName) is string value
                   && string.Equals(value, Command, StringComparison.OrdinalIgnoreCase);
        }
    }

    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(runKeyPath, writable: true);
        if (enabled) key.SetValue(valueName, Command, RegistryValueKind.String);
        else key.DeleteValue(valueName, throwOnMissingValue: false);
    }
}
