using System.Windows;
using Notchle.Windows.Island;

namespace Notchle.Windows.Shell;

/// The only place the app shell touches the island UI (Island/*).
internal static class UiHooks
{
    /// The island, bound to the one view model. The shell shows/activates it for "New link…".
    public static Window CreateIsland(NotchViewModel model) => new IslandWindow(model);

    /// --ui-demo: fake state, no audio. Runs inside the WPF Application, which keeps running
    /// until the demo's last window closes.
    public static void RunDemo(string[] args) => IslandDemo.Run(args);

    /// --ui-snapshots <dir>: render every island screen to PNG, then the app exits.
    public static void RenderSnapshots(string directory) => IslandSnapshots.RenderAll(directory);
}
