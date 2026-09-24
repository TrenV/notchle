using System.ComponentModel;
using System.Runtime.CompilerServices;
using Notchle.Core;

namespace Notchle.Windows;

/// The one object the island UI binds to (frozen contract, owned by the integrator).
/// Views read State/Settings and call Send/UpdateSettings; the coordinator sets the delegates
/// and assigns new State on the UI thread after every engine step.
public sealed class NotchViewModel : INotifyPropertyChanged
{
    private GameState _state = new() { Config = GameConfig.Default };
    private AppSettings _settings = new();
    private string _playerName = "";
    private bool _playerPlaysFullTrack;

    public GameState State { get => _state; set => Set(ref _state, value); }
    public AppSettings Settings { get => _settings; set => Set(ref _settings, value); }
    public string PlayerName { get => _playerName; set => Set(ref _playerName, value); }
    public bool PlayerPlaysFullTrack { get => _playerPlaysFullTrack; set => Set(ref _playerPlaysFullTrack, value); }

    public Action<GameAction> Send { get; set; } = _ => { };
    public Action<AppSettings> UpdateSettings { get; set; } = _ => { };

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
