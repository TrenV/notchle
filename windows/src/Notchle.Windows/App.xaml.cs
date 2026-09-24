using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Notchle.Core;
using Notchle.Core.App;
using Notchle.Windows.Playback;
using Notchle.Windows.Shell;

namespace Notchle.Windows;

/// Entry point. Normal launch runs the game in the tray; debug flags (same as the macOS app):
///   Notchle.Windows.exe --ui-demo [...]          W2's island demo
///   Notchle.Windows.exe --ui-snapshots &lt;dir&gt;     render every island screen to PNG and exit
public partial class App : Application
{
    private const string InstanceName = "Notchle.7c1e3f52";

    private SingleInstance? _instance;
    private AppCoordinator? _coordinator;
    private NotchViewModel? _model;
    private Window? _island;
    private TrayIcon? _tray;
    private StartupRegistration? _startup;
    private bool _quitting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        LaunchOptions options;
        try
        {
            options = LaunchOptions.Parse(e.Args);
        }
        catch (ArgumentException error)
        {
            Console.Error.WriteLine(error.Message);
            Shutdown(2);
            return;
        }

        switch (options.Mode)
        {
            case LaunchMode.UiSnapshots:
                try
                {
                    UiHooks.RenderSnapshots(options.SnapshotDirectory!);
                    Shutdown(0);
                }
                catch (Exception error)
                {
                    Console.Error.WriteLine($"Notchle: snapshots failed: {error}");
                    Shutdown(1);
                }
                return;

            case LaunchMode.UiDemo:
                ShutdownMode = ShutdownMode.OnLastWindowClose;
                UiHooks.RunDemo(e.Args);
                return;

            default:
                StartGame();
                return;
        }
    }

    private void StartGame()
    {
        _instance = new SingleInstance(InstanceName, () => Dispatcher.BeginInvoke(NewLink));
        if (!_instance.IsFirst)
        {
            _instance.ActivateFirst();
            Shutdown(0);
            return;
        }

        var model = new NotchViewModel();
        var coordinator = new AppCoordinator(
            new EmbedTrackSource(Http.Shared),
            new ProgressStore(AppPaths.DataDirectory),
            PlayerFactory.Create,
            state => model.State = state,
            new DispatcherSynchronizationContext(Dispatcher),
            history: new HistoryStore(AppPaths.DataDirectory),
            artwork: new ArtworkResolver(Http.Shared));
        model.State = coordinator.State;
        model.Settings = coordinator.Settings;
        model.History = coordinator.History;
        coordinator.HistoryChanged += history => model.History = history;
        coordinator.CurrentArtworkChanged += url => model.CurrentArtworkUrl = url;
        model.ClearHistory = coordinator.ClearHistory;
        ShowPlayer(model, coordinator.Player);
        coordinator.SettingsChanged += settings => model.Settings = settings;
        coordinator.PlayerChanged += player => ShowPlayer(model, player);
        model.Send = coordinator.Send;
        model.UpdateSettings = coordinator.UpdateSettings;

        _model = model;
        _coordinator = coordinator;
        _startup = new StartupRegistration(AppPaths.ExecutablePath);
        _tray = new TrayIcon(
            newLink: NewLink,
            connectSpotify: ConnectSpotify,
            resetProgress: ResetProgress,
            isStartWithWindows: () => _startup.IsEnabled,
            setStartWithWindows: SetStartWithWindows,
            quit: Quit);

        _island = UiHooks.CreateIsland(model);
        // The island is permanent: closing it (Alt+F4) hides it; the tray brings it back.
        _island.Closing += (_, args) =>
        {
            if (_quitting) return;
            args.Cancel = true;
            _island.Hide();
        };
        _island.Show();
    }

    private static void ShowPlayer(NotchViewModel model, IPlayer player)
    {
        model.PlayerName = player.DisplayName;
        model.PlayerPlaysFullTrack = player.PlaysFullTrack;
    }

    // MARK: - Tray menu

    /// Same as the macOS "New link…": back to idle (the link field) and bring the island up.
    private void NewLink()
    {
        _coordinator?.Send(new GameAction.Reset());
        if (_island is null) return;
        _island.Show();
        _island.Activate();
    }

    private void ResetProgress()
    {
        var answer = MessageBox.Show(
            "Songs from sets you already cleared can come back. Your play history is kept.",
            "Reset progress?",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question,
            MessageBoxResult.Cancel);
        if (answer == MessageBoxResult.OK) _coordinator?.ResetProgress();
    }

    private async void ConnectSpotify()
    {
        if (_coordinator is null) return;
        var settings = _coordinator.Settings;
        var clientId = TextPrompt.Ask(
            "Connect Spotify",
            "Client id of your Spotify developer app (developer.spotify.com › Dashboard). " +
            "Its redirect URI must be http://127.0.0.1/callback. Full tracks need Spotify Premium " +
            "and the Spotify app running on this PC. Note: Spotify's own app and the Windows media " +
            "flyout show the song, which spoils the answer.",
            settings.SpotifyClientId ?? "");
        if (string.IsNullOrWhiteSpace(clientId)) return;

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            await SpotifyConnectPlayer.CreateAuthorizer().ConnectAsync(clientId, timeout.Token);
            _coordinator.UpdateSettings(_coordinator.Settings with
            {
                SpotifyClientId = clientId,
                PlayerMode = PlayerMode.SpotifyConnect,
            });
            _tray?.ShowBalloon("Notchle", "Connected to Spotify. Songs now play in full through the Spotify app.");
        }
        catch (Exception error)
        {
            var message = error is OperationCanceledException ? "Spotify sign-in timed out." : AppMessages.Describe(error);
            _tray?.ShowBalloon("Couldn't connect Spotify", message, error: true);
        }
    }

    private void SetStartWithWindows(bool enabled)
    {
        try
        {
            _startup?.SetEnabled(enabled);
        }
        catch (Exception error)
        {
            _tray?.ShowBalloon("Notchle", $"Couldn't change Start with Windows: {error.Message}", error: true);
        }
    }

    private async void Quit()
    {
        if (_quitting) return;
        _quitting = true;
        if (_coordinator is { } coordinator)
        {
            // Pause the song before going: Spotify Connect would otherwise play on without us.
            await Task.WhenAny(coordinator.ShutdownAsync(), Task.Delay(TimeSpan.FromSeconds(3)));
        }
        Shutdown(0);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        (_coordinator?.Player as IDisposable)?.Dispose();
        _instance?.Dispose();
        Trace.Flush();
        base.OnExit(e);
    }
}
