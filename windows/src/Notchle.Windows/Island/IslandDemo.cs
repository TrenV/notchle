using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using Notchle.Core;
using Notchle.Core.Ui;

namespace Notchle.Windows.Island;

/// Debug harness for the island: a fake game (no Spotify, no audio) behind the real window
/// (port of Sources/NotchleMac/UI/UIDemo.swift).
///
///     Notchle.Windows.exe --ui-demo                 interactive: paste anything with "spotify" or "demo"
///     options: --ui-demo-auto            scripted cycle through every GamePhase
///              --ui-quit-after &lt;seconds&gt;
///              --ui-start guessing       skip the link step
///
/// The right answer is the title and every artist shown on the Correct / Revealed screens;
/// type "error" as the link to see the error screen. Diagnostics go to stderr and Debug.
public static class IslandDemo
{
    /// Shows the demo island. Inside a running WPF Application (e.g. from App.OnStartup) it just
    /// opens the window and returns; without one it creates the Application and runs it
    /// (blocking; the calling thread must be STA, as a WPF Main is).
    public static void Run(string[] args)
    {
        if (Application.Current is null)
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnLastWindowClose };
            app.Startup += (_, _) => Start(args);
            app.Run();
            return;
        }
        Start(args);
    }

    private static void Log(string line)
    {
        var text = $"[notchle-ui] {line}";
        Console.Error.WriteLine(text);
        Debug.WriteLine(text);
    }

    private static void After(TimeSpan delay, Action action)
    {
        var timer = new DispatcherTimer { Interval = delay };
        timer.Tick += (_, _) => { timer.Stop(); action(); };
        timer.Start();
    }

    private static void Start(string[] args)
    {
        string? Value(string flag)
        {
            var i = Array.IndexOf(args, flag);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        var vm = new NotchViewModel { PlayerName = "30-second previews (demo)", PlayerPlaysFullTrack = false };
        var game = new IslandDemoGame(() => vm.State, s => vm.State = s, After) { Log = Log };
        vm.Send = game.Send;
        vm.UpdateSettings = settings =>
        {
            vm.Settings = settings;
            var preview = settings.PlayerMode == PlayerMode.Preview;
            vm.PlayerName = preview ? "30-second previews (demo)" : "Spotify Connect (demo)";
            vm.PlayerPlaysFullTrack = !preview;
            Log($"updateSettings: {settings.PlayerMode}");
        };

        var window = new IslandWindow(vm);
        window.Session.ParseSource = IslandDemoGame.Parse;
        window.Show();
        Log("demo started (hover the top edge; click to type; Ctrl+Alt+N toggles)");

        if (Value("--ui-start") == "guessing") game.Send(new GameAction.Load(IslandDemoGame.DemoSource));
        if (args.Contains("--ui-demo-auto")) RunScript(window, game);
        if (Value("--ui-quit-after") is { } q && double.TryParse(q, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
            After(TimeSpan.FromSeconds(seconds), () =>
            {
                Log($"quitting after {seconds}s");
                window.Close();
                Application.Current?.Shutdown();
            });
    }

    /// Scripted cycle through every screen (the island stays collapsed unless hovered: phase
    /// changes never expand it, so this shows the collapsed indicators).
    private static void RunScript(IslandWindow window, IslandDemoGame g)
    {
        var s = window.Session;
        var steps = new List<(double Delay, Action Run)>
        {
            (0.5, () => s.UrlText = "https://open.spotify.com/playlist/demo"),
            (1.5, s.Load),                                                        // loading → playing(0)
            (3.0, () => { s.TitleText = "Paper"; s.ArtistText = "Kites"; }),      // typing during the snippet
            (4.0, () => g.Send(new GameAction.Submit(new Guess("Paper", "Kites")))), // wrong(0)
            (3.0, () => g.Send(new GameAction.Retry())),                          // playing(1)
            (2.0, () => { if (g.Current is { } t) g.Send(new GameAction.Submit(new Guess(t.Title, string.Join(", ", t.Artists)))); }), // correct
            (4.0, () => g.Send(new GameAction.Next())),                           // playing(0)
            (6.0, () => g.Send(new GameAction.GiveUp())),                         // revealed
            (3.0, () => g.ForceSetEnd(complete: true)),
            (5.0, () => g.ForceSetEnd(complete: false)),
            (5.0, () => g.SetPhase(new GamePhase.Exhausted())),
            (3.0, () => g.SetPhase(new GamePhase.Error("Spotify is not reachable. Check your connection, then press Skip."))),
            (3.0, () => g.Send(new GameAction.Reset())),
        };
        var total = 0.0;
        foreach (var (delay, run) in steps)
        {
            total += delay;
            After(TimeSpan.FromSeconds(total), run);
        }
    }
}
