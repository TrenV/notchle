using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Notchle.Core;
using Notchle.Core.Ui;

namespace Notchle.Windows.Island;

/// Renders every island screen offscreen to PNG (RenderTargetBitmap, 2× DPI) over a mock
/// desktop, for visual review on CI: <c>Notchle.Windows.exe --ui-snapshots &lt;dir&gt;</c>.
public static class IslandSnapshots
{
    internal sealed record Scenario(string Name, GamePhase Phase)
    {
        public bool Expanded { get; init; } = true;
        public string Title { get; init; } = "";
        public string Artist { get; init; } = "";
        public string Url { get; init; } = "";
        public string? UrlMessage { get; init; }
        public bool Settings { get; init; }
        public PlayerMode PlayerMode { get; init; } = PlayerMode.Preview;
        /// Seconds since the celebration started (confetti mid-burst / ✓ flash / glow).
        public double? CelebrationAgo { get; init; }
        public double? SetEndAgo { get; init; }
        public bool ReduceMotion { get; init; }
        public bool FullTrack { get; init; } = true;
        public double SnippetElapsed { get; init; } = 2;
        public bool Tall { get; init; }
        public bool TopTaskbar { get; init; }
        /// Draw the ↺ button in its hover state (a tooltip popup can't be captured offscreen).
        public bool RestartHover { get; init; }
        /// First press of the quit button: the red "Quit playlist?" capsule.
        public bool QuitArmed { get; init; }
        /// Track 5 ("Northbound") has two artists.
        public int TrackIndex { get; init; } = 6;
        /// Tests render their own state.
        public GameState? State { get; init; }
        /// The History tab instead of the game.
        public bool History { get; init; }
        /// The play history the view model holds (oldest first); SampleHistory by default.
        public IReadOnlyList<HistoryEntry>? Entries { get; init; }
        /// First press of "Clear history".
        public bool ClearArmed { get; init; }
        /// Album covers available (generated in code: no disk, no network).
        public bool Covers { get; init; }
    }

    private static readonly Verdict HalfRight = new(true, false);

    internal static IReadOnlyList<Scenario> Scenarios { get; } =
    [
        new("01-lip-idle", new GamePhase.Idle()) { Expanded = false },
        new("02-idle", new GamePhase.Idle()),
        new("03-idle-unsupported", new GamePhase.Idle()) { Url = "https://example.com/not-spotify", UrlMessage = "Unsupported link" },
        new("04-loading", new GamePhase.Loading()),
        new("05-collapsed-playing", new GamePhase.PlayingSnippet(0)) { Expanded = false, SnippetElapsed = 3 },
        new("06-playing-t0", new GamePhase.PlayingSnippet(0)) { Title = "Paper" },
        new("07-guessing-t0", new GamePhase.Guessing(0)) { Title = "Paper Lanterns", Artist = "Midnight" },
        new("08-wrong-t0", new GamePhase.Wrong(0, HalfRight)) { Title = "Paper Lanterns", Artist = "The Kites" },
        new("09-playing-t1", new GamePhase.PlayingSnippet(1)) { Title = "Paper Lanterns", Artist = "The Kites", SnippetElapsed = 6 },
        new("10-correct-confetti", new GamePhase.Correct(1)) { CelebrationAgo = 0.35, Tall = true },
        new("11-correct-preview-hint", new GamePhase.Correct(0)) { FullTrack = false },
        new("12-correct-reduce-motion", new GamePhase.Correct(2)) { CelebrationAgo = 0.7, ReduceMotion = true },
        new("13-revealed-gave-up", new GamePhase.Revealed(null)),
        new("14-revealed-out-of-tries", new GamePhase.Revealed(new Verdict(false, true))),
        new("15-set-complete", new GamePhase.SetComplete(20)) { State = IslandDemoGame.SetEndState(complete: true, newAvailable: 40) },
        new("16-set-failed", new GamePhase.SetFailed(13)) { State = IslandDemoGame.SetEndState(complete: false, newAvailable: 40) },
        new("17-exhausted", new GamePhase.Exhausted()),
        new("18-error", new GamePhase.Error("Spotify is not reachable. Check your connection, then press Skip.")),
        new("19-settings", new GamePhase.Guessing(0)) { Settings = true },
        new("20-settings-connect", new GamePhase.Guessing(0)) { Settings = true, PlayerMode = PlayerMode.SpotifyConnect },
        new("21-collapsed-guessing", new GamePhase.Guessing(0)) { Expanded = false },
        new("22-guessing-two-artists", new GamePhase.Guessing(0)) { Title = "Northbound", TrackIndex = 5 },
        new("23-wrong-two-artists", new GamePhase.Wrong(1, HalfRight)) { Title = "Northbound", Artist = "Tove Ahlberg", TrackIndex = 5 },
        new("24-collapsed-wrong", new GamePhase.Wrong(0, HalfRight)) { Expanded = false },
        new("25-collapsed-correct-flash", new GamePhase.Correct(0)) { Expanded = false, CelebrationAgo = 0.35, Tall = true },
        new("26-collapsed-song-plays-on", new GamePhase.Revealed(null)) { Expanded = false },
        new("27-collapsed-set-result", new GamePhase.SetComplete(20)) { Expanded = false, SetEndAgo = 1 },
        new("28-collapsed-error", new GamePhase.Error("x")) { Expanded = false },
        new("29-below-top-taskbar", new GamePhase.Guessing(1)) { TopTaskbar = true, Title = "Glass" },
        new("30-guessing-replay-hover", new GamePhase.Guessing(1)) { Title = "Paper Lanterns", Artist = "Kites", RestartHover = true },
        new("31-correct-restart-hover", new GamePhase.Correct(0)) { RestartHover = true },
        new("32-revealed-restart-hover", new GamePhase.Revealed(null)) { RestartHover = true, FullTrack = false },
        new("33-guessing-t0-skip", new GamePhase.Guessing(0)) { Title = "Paper" },
        new("34-guessing-t2-no-skip", new GamePhase.Guessing(2)) { Title = "Paper Lanterns", Artist = "The Kites" },
        new("35-quit-armed", new GamePhase.Guessing(0)) { Title = "Paper", QuitArmed = true },
        new("36-correct-quit-armed", new GamePhase.Correct(0)) { QuitArmed = true },
        new("37-set-failed-7-new", new GamePhase.SetFailed(13)) { State = IslandDemoGame.SetEndState(complete: false, newAvailable: 7) },
        new("38-set-failed-no-new", new GamePhase.SetFailed(13)) { State = IslandDemoGame.SetEndState(complete: false, newAvailable: 0) },
        new("39-set-complete-no-new", new GamePhase.SetComplete(20)) { State = IslandDemoGame.SetEndState(complete: true, newAvailable: 0) },
        new("44-history", new GamePhase.Guessing(0)) { History = true, Covers = true },
        new("45-history-empty", new GamePhase.Idle()) { History = true, Entries = [] },
        new("46-history-clear-armed", new GamePhase.SetFailed(14)) { History = true, ClearArmed = true, Covers = true },
        new("47-correct-cover", new GamePhase.Correct(0)) { Covers = true },
        new("48-revealed-cover", new GamePhase.Revealed(null)) { Covers = true, FullTrack = false },
        new("49-correct-cover-offline", new GamePhase.Correct(1)),
        new("50-history-no-covers", new GamePhase.Idle()) { History = true },
    ];

    internal static readonly Uri SampleCoverUrl = new("https://image-cdn-fa.spotifycdn.com/image/snapshot");

    /// A play history for the snapshots, oldest first, over three days, from another playlist.
    /// The last entries are demo tracks of the scenarios' current set, so a scenario in the middle
    /// of that set ("37-history") shows the spoiler rule hiding them; after the set they show.
    internal static IReadOnlyList<HistoryEntry> SampleHistory { get; } = BuildSampleHistory();

    private static IReadOnlyList<HistoryEntry> BuildSampleHistory()
    {
        (string Id, string Title, string[] Artists, string Listing, double HoursAgo, int? Tier, int Wrong, int Skips, bool Cover)[] plays =
        [
            ("h1", "Harbour Lights", ["Clara Wynn"], "Late Night Drive", 50, 0, 0, 0, true),
            ("h2", "Tangerine Motel", ["The Low Suns", "Ada Frost"], "Late Night Drive", 49.5, 2, 1, 1, true),
            ("h3", "Paper Planes Home", ["Milo Grey"], "Late Night Drive", 49, null, 1, 2, false),
            ("h4", "Cold Brew Morning", ["Sofia Lark"], "Sunday Coffee", 26, 1, 1, 0, true),
            ("h5", "Static Hearts", ["Glass Arcade"], "Sunday Coffee", 25.8, 0, 0, 0, true),
            ("h6", "Blue Hour", ["Nina Cole"], "Sunday Coffee", 3, null, 3, 0, true),
            ("h7", "Tidal", ["Oren Vale"], "Sunday Coffee", 2, 0, 0, 0, false),
            ("h8", "Sundown Parade", ["The Kettles"], "Sunday Coffee", 0.5, 1, 0, 1, true),
        ];
        var entries = plays.Select((p, i) => new HistoryEntry(
            new Guid(i + 1, 0, 0, new byte[8]), SnapshotNow.AddHours(-p.HoursAgo), p.Id, p.Title, p.Artists,
            p.Listing, new SourceRef(SourceKind.Playlist, "history"), p.Tier is not null, p.Tier, p.Wrong, p.Skips,
            p.Cover ? SampleCoverUrl : null)).ToList();
        // Played in the scenarios' current set (demo0, demo1): hidden until the set ends.
        foreach (var (track, i) in IslandDemoGame.Tracks.Take(2).Select((t, i) => (t, i)))
            entries.Add(new HistoryEntry(new Guid(100 + i, 0, 0, new byte[8]), SnapshotNow.AddMinutes(-4 + i), track.Id,
                track.Title, track.Artists, "Notchle Demo Mix", IslandDemoGame.DemoSource, i == 0, i == 0 ? 0 : null, i, 0,
                SampleCoverUrl));
        return entries;
    }

    /// A made-up cover per track (gradient with a note), frozen; no image files needed.
    internal static ImageSource SampleCover(string trackId)
    {
        var hue = (uint)trackId.Sum(c => c * 37) % 360; // stable across runs (string hashes are not)
        Color FromHue(double h, double l)
        {
            var c = (1 - Math.Abs(2 * l - 1)) * 0.65;
            var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
            var m = l - c / 2;
            var (r, g, b) = h switch { < 60 => (c, x, 0.0), < 120 => (x, c, 0.0), < 180 => (0.0, c, x), < 240 => (0.0, x, c), < 300 => (x, 0.0, c), _ => (c, 0.0, x) };
            return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
        }
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new LinearGradientBrush(FromHue(hue, 0.55), FromHue((hue + 70) % 360, 0.3), 45), null, new Rect(0, 0, 128, 128));
            dc.DrawEllipse(IslandTheme.Frozen(Colors.White, 0.18), null, new Point(84, 50), 34, 34);
        }
        var bitmap = new RenderTargetBitmap(128, 128, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    /// Writes one PNG per scenario into <paramref name="directory"/>. Runs on an STA thread of
    /// its own when called from a non-STA thread.
    public static void RenderAll(string directory)
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
        {
            Exception? failure = null;
            var thread = new Thread(() =>
            {
                try { RenderAll(directory); }
                catch (Exception e) { failure = e; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            if (failure is not null) throw new InvalidOperationException("Rendering the island snapshots failed.", failure);
            return;
        }
        Directory.CreateDirectory(directory);
        foreach (var scenario in Scenarios)
        {
            var path = System.IO.Path.Combine(directory, scenario.Name + ".png");
            Save(Render(scenario), path);
            Console.Error.WriteLine($"wrote {path}");
        }
    }

    internal static void Save(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    /// Fixed clock so every snapshot is reproducible.
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    internal static readonly Color SnapshotAccent = Color.FromRgb(0x00, 0x78, 0xD4);
    internal static readonly DateTimeOffset SnapshotNow = new(2026, 9, 24, 14, 5, 0, TimeSpan.Zero);

    /// Builds the scenario's island (for tests: the view tree without rendering).
    internal static (FrameworkElement Root, IslandView View, IslandSession Session) Compose(Scenario sc)
    {
        var state = sc.State ?? IslandDemoGame.SampleState(sc.Phase, sc.TrackIndex);
        var vm = new NotchViewModel
        {
            State = state,
            History = sc.Entries ?? SampleHistory,
            CurrentArtworkUrl = sc.Covers ? SampleCoverUrl : null,
            Settings = new AppSettings { PlayerMode = sc.PlayerMode },
            PlayerName = sc.PlayerMode == PlayerMode.Preview ? "30-second previews" : "Spotify Connect",
            PlayerPlaysFullTrack = sc.FullTrack,
        };
        var now = SnapshotNow;
        var session = new IslandSession(new FixedClock(now));
        session.StateDidChange(null, state);
        session.TitleText = sc.Title;
        session.ArtistText = sc.Artist;
        session.UrlText = sc.Url;
        session.UrlMessage = sc.UrlMessage;
        session.SetClocks(
            now.AddSeconds(-sc.SnippetElapsed),
            sc.CelebrationAgo is { } c ? now.AddSeconds(-c) : null,
            sc.SetEndAgo is { } e ? now.AddSeconds(-e) : null);
        if (sc.Expanded) session.Behavior.PointerMoved(true);
        session.ShowingSettings = sc.Settings;
        if (sc.QuitArmed) session.PressQuit();
        if (sc.History) session.ShowTab(IslandTab.History);
        if (sc.ClearArmed) session.PressClearHistory();

        // Fixed accent (Windows' default blue) so the PNGs do not depend on the CI machine.
        var covers = new Dictionary<string, ImageSource>();
        var artwork = sc.Covers
            ? ArtworkImages.FromMemory(id => covers.TryGetValue(id, out var c) ? c : covers[id] = SampleCover(id))
            : ArtworkImages.None;
        var view = new IslandView(session, vm, SnapshotAccent, artwork)
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
        };
        var frame = IslandFrame.For(session.Mode);

        var width = 760.0;
        var height = sc.Tall ? 520.0 : 300.0;
        var taskbarTop = sc.TopTaskbar ? 48.0 : 0;
        var root = new Grid { Width = width, Height = height, ClipToBounds = true };
        root.Children.Add(MockDesktop(width, height, taskbarTop));
        view.Margin = new Thickness(0, taskbarTop, 0, 0);
        root.Children.Add(view);
        if (!sc.ReduceMotion && sc.CelebrationAgo is { } ago)
        {
            var confetti = new ConfettiLayer { Width = width, Height = height };
            confetti.Start(session.CelebrationSeed, new Point(width / 2, taskbarTop + frame.Height));
            confetti.Time = ago;
            root.Children.Add(confetti);
        }

        // Two passes: some parts (the progress bar) size from the first layout.
        for (var pass = 0; pass < 2; pass++)
        {
            view.Update(frame, now, sc.ReduceMotion);
            if (sc.RestartHover)
                foreach (var button in Descendants<IslandIconButton>(view)) button.Highlighted = true;
            root.Measure(new Size(width, height));
            root.Arrange(new Rect(0, 0, width, height));
            root.UpdateLayout();
        }
        return (root, view, session);
    }

    /// Every element of type T under <paramref name="root"/> (visual and logical children).
    internal static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        var seen = new HashSet<DependencyObject>();
        var stack = new Stack<DependencyObject>([root]);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            if (!seen.Add(d)) continue;
            if (d is T match) yield return match;
            if (d is Visual or System.Windows.Media.Media3D.Visual3D)
                for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++) stack.Push(VisualTreeHelper.GetChild(d, i));
            foreach (var child in LogicalTreeHelper.GetChildren(d).OfType<DependencyObject>()) stack.Push(child);
        }
    }

    internal static BitmapSource Render(Scenario sc)
    {
        var (root, _, _) = Compose(sc);
        var bitmap = new RenderTargetBitmap((int)(root.Width * 2), (int)(root.Height * 2), 192, 192, PixelFormats.Pbgra32);
        bitmap.Render(root);
        bitmap.Freeze();
        return bitmap;
    }

    /// Wallpaper, a blurred-looking app window and (optionally) a taskbar docked at the top, so
    /// the black island reads as it would on a real desktop.
    private static FrameworkElement MockDesktop(double width, double height, double taskbarTop)
    {
        var canvas = new Canvas { Width = width, Height = height };
        canvas.Background = new LinearGradientBrush(Color.FromRgb(0x2B, 0x4C, 0x7E), Color.FromRgb(0x9A, 0x6F, 0xB0), 35);
        var app = new Border
        {
            Width = 520, Height = height, CornerRadius = new CornerRadius(8),
            Background = IslandTheme.Frozen(Color.FromRgb(0xF3, 0xF3, 0xF3), 0.92),
        };
        var titleBar = new Border { Height = 32, Background = IslandTheme.Frozen(Colors.White), CornerRadius = new CornerRadius(8, 8, 0, 0) };
        titleBar.Child = new TextBlock
        {
            Text = "Document - Notepad", FontFamily = IslandTheme.Font, FontSize = 12, Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center, Foreground = IslandTheme.Frozen(Color.FromRgb(0x20, 0x20, 0x20)),
        };
        app.Child = new StackPanel { Children = { titleBar } };
        Canvas.SetLeft(app, -120);
        Canvas.SetTop(app, taskbarTop + 70);
        canvas.Children.Add(app);
        if (taskbarTop > 0)
        {
            var bar = new Rectangle { Width = width, Height = taskbarTop, Fill = IslandTheme.Frozen(Color.FromRgb(0x20, 0x20, 0x20), 0.92) };
            canvas.Children.Add(bar);
            var start = new TextBlock
            {
                Text = "⊞  Search", FontFamily = IslandTheme.Font, FontSize = 13, Foreground = IslandTheme.Primary,
            };
            Canvas.SetLeft(start, 16);
            Canvas.SetTop(start, (taskbarTop - 18) / 2);
            canvas.Children.Add(start);
        }
        return canvas;
    }
}
