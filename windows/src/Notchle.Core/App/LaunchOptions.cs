namespace Notchle.Core.App;

public enum LaunchMode { Game, UiDemo, UiSnapshots }

/// Command line of Notchle.Windows.exe (same flags as the macOS app):
///   (none)                 run the game (tray icon + island)
///   --ui-demo [...]        W2's island demo; the remaining flags go to it untouched
///   --ui-snapshots &lt;dir&gt;   render every island screen to PNG in &lt;dir&gt; and exit
public sealed record LaunchOptions(LaunchMode Mode, string? SnapshotDirectory, IReadOnlyList<string> Arguments)
{
    public static LaunchOptions Parse(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] == "--ui-snapshots")
            {
                if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException("--ui-snapshots needs a directory");
                return new LaunchOptions(LaunchMode.UiSnapshots, args[i + 1], args);
            }
        }
        return args.Contains("--ui-demo")
            ? new LaunchOptions(LaunchMode.UiDemo, null, args)
            : new LaunchOptions(LaunchMode.Game, null, args);
    }
}
