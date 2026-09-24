using System.Text.Json;
using System.Text.Json.Serialization;

namespace Notchle.Core;

/// JSON file persistence; on Windows the directory is %APPDATA%\Notchle.
/// Port of Sources/NotchleCore/Engine/ProgressStore.swift. The file uses the same keys as the
/// Swift one (`settings`, `clearedTrackIDs`, `playerMode`, `lastSource`, `config`, `tiers`,
/// `setSize`, `snippetStart`), pretty-printed with sorted keys and sorted ids. The player modes
/// differ per platform: a mode this platform does not know loads as the default one.
public sealed class ProgressStore(string directory)
{
    public string FilePath { get; } = Path.Combine(directory, "progress.json");

    private static readonly JsonSerializerOptions ReadOptions = new() { RespectNullableAnnotations = true };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// Missing or unreadable file yields a default Progress, never a throw.
    public Progress Load()
    {
        try
        {
            var stored = JsonSerializer.Deserialize<StoredProgress>(File.ReadAllBytes(FilePath), ReadOptions);
            return stored?.ToProgress() ?? new Progress();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException
                                          or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            return new Progress();
        }
    }

    /// Writes atomically (temp file in the same directory, then rename over the old file) so a
    /// crash mid-write never leaves a truncated file. Creates the directory if it is missing.
    /// Throws IOException / UnauthorizedAccessException when the file cannot be written.
    public void Save(Progress progress)
    {
        var folder = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(folder);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(StoredProgress.From(progress), WriteOptions);
        var temp = Path.Combine(folder, $".progress-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, FilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    // On-disk shape. Properties are declared in alphabetical order so the output has sorted keys,
    // like the Swift encoder's .sortedKeys. Required members mirror Swift's Codable, which rejects
    // a file missing any non-optional key (and the whole file then loads as the default).

    private sealed class StoredProgress
    {
        [JsonPropertyName("clearedTrackIDs"), JsonRequired] public List<string> ClearedTrackIds { get; set; } = [];
        [JsonPropertyName("settings"), JsonRequired] public StoredSettings Settings { get; set; } = new();

        public static StoredProgress From(Progress progress) => new()
        {
            ClearedTrackIds = progress.ClearedTrackIds.Order(StringComparer.Ordinal).ToList(),
            Settings = StoredSettings.From(progress.Settings),
        };

        /// A null id fails the whole file, as it does in Swift (element nullability is not
        /// enforced by System.Text.Json).
        public Progress ToProgress() => new()
        {
            Settings = Settings.ToSettings(),
            ClearedTrackIds = ClearedTrackIds.Contains(null!)
                ? throw new JsonException("null track id")
                : new HashSet<string>(ClearedTrackIds),
        };
    }

    private sealed class StoredSettings
    {
        [JsonPropertyName("config"), JsonRequired] public StoredConfig Config { get; set; } = new();
        [JsonPropertyName("lastSource")] public StoredSource? LastSource { get; set; }
        [JsonPropertyName("playerMode"), JsonRequired] public string PlayerMode { get; set; } = "";
        [JsonPropertyName("spotifyClientId")] public string? SpotifyClientId { get; set; }

        public static StoredSettings From(AppSettings settings) => new()
        {
            Config = StoredConfig.From(settings.Config),
            LastSource = settings.LastSource is { } source ? StoredSource.From(source) : null,
            PlayerMode = Camel(settings.PlayerMode.ToString()),
            SpotifyClientId = settings.SpotifyClientId,
        };

        public AppSettings ToSettings() => new()
        {
            Config = Config.ToConfig(),
            LastSource = LastSource?.ToSource(),
            PlayerMode = Enum.GetValues<PlayerMode>().FirstOrDefault(
                mode => Camel(mode.ToString()) == PlayerMode, new AppSettings().PlayerMode),
            SpotifyClientId = SpotifyClientId,
        };
    }

    private sealed class StoredConfig
    {
        [JsonPropertyName("setSize"), JsonRequired] public int SetSize { get; set; }
        [JsonPropertyName("snippetStart"), JsonRequired] public double SnippetStart { get; set; }
        [JsonPropertyName("tiers"), JsonRequired] public List<double> Tiers { get; set; } = [];

        public static StoredConfig From(GameConfig config) => new()
        {
            SetSize = config.SetSize,
            SnippetStart = config.SnippetStart,
            Tiers = config.Tiers.ToList(),
        };

        public GameConfig ToConfig() => new(Tiers.ToArray(), SetSize, SnippetStart);
    }

    private sealed class StoredSource
    {
        [JsonPropertyName("id"), JsonRequired] public string Id { get; set; } = "";
        [JsonPropertyName("kind"), JsonRequired] public string Kind { get; set; } = "";

        public static StoredSource From(SourceRef source) => new() { Id = source.Id, Kind = Camel(source.Kind.ToString()) };

        /// An unknown kind fails the whole file, as it does in Swift.
        public SourceRef ToSource() =>
            new(Enum.GetValues<SourceKind>().Cast<SourceKind?>().FirstOrDefault(kind => Camel(kind!.Value.ToString()) == Kind)
                ?? throw new JsonException($"unknown source kind \"{Kind}\""), Id);
    }

    /// "SpotifyConnect" → "spotifyConnect", matching Swift's raw values ("playlist", "preview").
    private static string Camel(string name) => char.ToLowerInvariant(name[0]) + name[1..];
}
