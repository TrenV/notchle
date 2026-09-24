using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Notchle.Core;

/// The play history as <c>history.json</c> next to progress.json (%APPDATA%\Notchle on
/// Windows). Mirror of Sources/NotchleCore/Engine/HistoryStore.swift: a top-level JSON array of
/// entries, oldest first, with the Swift Codable keys (<c>id</c>, <c>date</c> ISO-8601,
/// <c>trackID</c>, <c>title</c>, <c>artists</c>, <c>listingName</c>, <c>listingRef</c> {kind, id},
/// <c>correct</c>, <c>tierIndex</c>, <c>wrongGuesses</c>, <c>skips</c>, <c>artworkURL</c>), sorted keys, so the file
/// moves between the two apps. Absent optionals are omitted, as Swift's encoder does.
public sealed class HistoryStore(string directory)
{
    /// Oldest entries are dropped beyond this many.
    public const int Capacity = 10_000;

    public string FilePath { get; } = Path.Combine(directory, "history.json");

    private static readonly JsonSerializerOptions ReadOptions = new() { RespectNullableAnnotations = true };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// Oldest first. A missing or unreadable file is an empty history, never a throw.
    public IReadOnlyList<HistoryEntry> Load()
    {
        try
        {
            var stored = JsonSerializer.Deserialize<List<StoredEntry>>(File.ReadAllBytes(FilePath), ReadOptions);
            if (stored is null || stored.Contains(null!)) return [];
            return Capped(stored.Select(e => e.ToEntry()).ToList());
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException
                                          or NotSupportedException or ArgumentException or InvalidOperationException
                                          or FormatException)
        {
            return [];
        }
    }

    /// Writes all of <paramref name="entries"/> (capped) atomically: temp file in the same
    /// directory, then renamed over the old one. Throws IOException / UnauthorizedAccessException
    /// when the file cannot be written.
    public void Save(IReadOnlyList<HistoryEntry> entries)
    {
        var folder = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(folder);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(Capped(entries).Select(StoredEntry.From).ToList(), WriteOptions);
        var temp = Path.Combine(folder, $".history-{Guid.NewGuid():N}.tmp");
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

    /// Appends one entry to what is on disk and returns the new (capped) history.
    public IReadOnlyList<HistoryEntry> Append(HistoryEntry entry)
    {
        var updated = Capped([.. Load(), entry]);
        Save(updated);
        return updated;
    }

    /// The newest <see cref="Capacity"/> entries.
    public static IReadOnlyList<HistoryEntry> Capped(IReadOnlyList<HistoryEntry> entries) =>
        entries.Count <= Capacity ? entries : entries.Skip(entries.Count - Capacity).ToList();

    /// Swift's JSONEncoder .iso8601: UTC, whole seconds, "Z" (it rejects fractional seconds).
    internal static string FormatDate(DateTimeOffset date) =>
        date.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    internal static DateTimeOffset ParseDate(string text) =>
        DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    // On-disk shape, properties in alphabetical order (sorted keys, like .sortedKeys).
    private sealed class StoredEntry
    {
        [JsonPropertyName("artists"), JsonRequired] public List<string> Artists { get; set; } = [];
        [JsonPropertyName("artworkURL")] public string? ArtworkUrl { get; set; }
        [JsonPropertyName("correct"), JsonRequired] public bool Correct { get; set; }
        [JsonPropertyName("date"), JsonRequired] public string Date { get; set; } = "";
        [JsonPropertyName("id"), JsonRequired] public string Id { get; set; } = "";
        [JsonPropertyName("listingName"), JsonRequired] public string ListingName { get; set; } = "";
        [JsonPropertyName("listingRef")] public StoredRef? ListingRef { get; set; }
        [JsonPropertyName("skips"), JsonRequired] public int Skips { get; set; }
        [JsonPropertyName("tierIndex")] public int? TierIndex { get; set; }
        [JsonPropertyName("title"), JsonRequired] public string Title { get; set; } = "";
        [JsonPropertyName("trackID"), JsonRequired] public string TrackId { get; set; } = "";
        [JsonPropertyName("wrongGuesses"), JsonRequired] public int WrongGuesses { get; set; }

        public static StoredEntry From(HistoryEntry e) => new()
        {
            Artists = e.Artists.ToList(),
            ArtworkUrl = e.ArtworkUrl?.AbsoluteUri,
            Correct = e.Correct,
            Date = FormatDate(e.Date),
            // Swift's UUID encodes upper case.
            Id = e.Id.ToString("D").ToUpperInvariant(),
            ListingName = e.ListingName,
            ListingRef = e.ListingRef is { } r ? StoredRef.From(r) : null,
            Skips = e.Skips,
            TierIndex = e.TierIndex,
            Title = e.Title,
            TrackId = e.TrackId,
            WrongGuesses = e.WrongGuesses,
        };

        /// A bad id, date, kind or a null artist fails the whole file, as it does in Swift.
        public HistoryEntry ToEntry() => new(
            Guid.Parse(Id),
            ParseDate(Date),
            TrackId,
            Title,
            Artists.Contains(null!) ? throw new JsonException("null artist") : Artists.ToArray(),
            ListingName,
            ListingRef?.ToRef(),
            Correct,
            TierIndex,
            WrongGuesses,
            Skips,
            ArtworkUrl is null ? null
                : Uri.TryCreate(ArtworkUrl, UriKind.Absolute, out var art) ? art
                : throw new JsonException("bad artworkURL"));
    }

    private sealed class StoredRef
    {
        [JsonPropertyName("id"), JsonRequired] public string Id { get; set; } = "";
        [JsonPropertyName("kind"), JsonRequired] public string Kind { get; set; } = "";

        public static StoredRef From(SourceRef r) => new() { Id = r.Id, Kind = r.Kind.ToString().ToLowerInvariant() };

        public SourceRef ToRef() =>
            new(Enum.GetValues<SourceKind>().Cast<SourceKind?>().FirstOrDefault(k => k!.Value.ToString().ToLowerInvariant() == Kind)
                ?? throw new JsonException($"unknown source kind \"{Kind}\""), Id);
    }
}
