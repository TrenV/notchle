using System.Windows;

namespace Notchle.Windows.Shell;

/// The only place the app shell touches agent W2's island UI. On this branch W2's types
/// (IslandWindow, IslandDemo, IslandSnapshots in namespace Notchle.Windows) don't exist yet, so
/// the calls are behind NOTCHLE_ISLAND_UI and a placeholder stands in.
///
/// INTEGRATOR: after merging W2's branch, delete the #else branches (and PlaceholderIslandWindow.cs),
/// or define NOTCHLE_ISLAND_UI in Notchle.Windows.csproj. Nothing else in the shell changes.
internal static class UiHooks
{
    /// The island, bound to the one view model. The shell shows/activates it for "New link…".
    public static Window CreateIsland(NotchViewModel model) =>
#if NOTCHLE_ISLAND_UI
        new IslandWindow(model);
#else
        new PlaceholderIslandWindow(model);
#endif

    /// --ui-demo: W2's demo (fake state, no audio). Runs inside the WPF Application, which keeps
    /// running until the demo's last window closes.
    public static void RunDemo(string[] args)
    {
#if NOTCHLE_ISLAND_UI
        IslandDemo.Run(args);
#else
        new PlaceholderIslandWindow(new NotchViewModel()) { Title = "Notchle --ui-demo (placeholder)" }.Show();
#endif
    }

    /// --ui-snapshots &lt;dir&gt;: render every island screen to PNG, then the app exits.
    public static void RenderSnapshots(string directory)
    {
#if NOTCHLE_ISLAND_UI
        IslandSnapshots.RenderAll(directory);
#else
        PlaceholderIslandWindow.RenderPlaceholderSnapshot(directory);
#endif
    }
}
