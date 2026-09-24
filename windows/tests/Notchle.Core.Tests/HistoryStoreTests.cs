using System.Text.Json;
using Notchle.Core;

namespace Notchle.Core.Tests;

/// history.json: round trip, missing / corrupt files, the 10,000 cap and the exact on-disk shape
/// shared with the Swift HistoryStore.
public sealed class HistoryStoreTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("notchle-history-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static readonly HistoryEntry Right = new(
        Guid.Parse("0B9A3C2E-1F4D-4E5A-9C7B-2D8E6F1A3B4C"), new DateTimeOffset(2026, 9, 24, 14, 5, 7, TimeSpan.Zero),
        "4uLU6hMCjMI75M1A2tKUQC", "Bass Persuades", ["Rex Orange", "Mabel Fitch"], "Road trip",
        new SourceRef(SourceKind.Playlist, "37i9dQZF1DXcBWIGoYBM5M"), true, 1, 1, 0,
        new Uri("https://image-cdn-fa.spotifycdn.com/image/ab67616d00001e0240e583b55bdddcf70516fa6c"));

    private static readonly HistoryEntry Missed = new(
        Guid.Parse("7F000000-0000-4000-8000-000000000001"), new DateTimeOffset(2026, 9, 24, 16, 0, 0, TimeSpan.FromHours(2)),
        "t2", "Other", ["Solo"], "", null, false, null, 0, 2);

    [Fact]
    public void MissingFileIsEmpty() => Assert.Empty(new HistoryStore(Path.Combine(_root, "nope")).Load());

    [Fact]
    public void RoundTripCreatesTheDirectory()
    {
        var store = new HistoryStore(Path.Combine(_root, "a", "Notchle"));
        store.Save([Right, Missed]);
        Assert.Equal(Path.Combine(_root, "a", "Notchle", "history.json"), store.FilePath);
        var loaded = store.Load();
        Assert.Equal([Right, Missed with { Date = Missed.Date.ToUniversalTime() }], loaded);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(store.FilePath)!, "*.tmp"));
    }

    [Fact]
    public void AppendAddsToWhatIsOnDisk()
    {
        var store = new HistoryStore(_root);
        store.Append(Right);
        var all = store.Append(Missed);
        Assert.Equal(2, all.Count);
        Assert.Equal([Right.Id, Missed.Id], store.Load().Select(e => e.Id));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"entries\":[]}")]
    [InlineData("[{\"id\":\"x\"}]")]
    [InlineData("[null]")]
    [InlineData("[{\"artists\":[\"a\"],\"correct\":true,\"date\":\"yesterday\",\"id\":\"0B9A3C2E-1F4D-4E5A-9C7B-2D8E6F1A3B4C\",\"listingName\":\"\",\"skips\":0,\"title\":\"t\",\"trackID\":\"t\",\"wrongGuesses\":0}]")]
    [InlineData("[{\"artists\":[\"a\"],\"correct\":true,\"date\":\"2026-09-24T14:05:07Z\",\"id\":\"0B9A3C2E-1F4D-4E5A-9C7B-2D8E6F1A3B4C\",\"listingName\":\"\",\"listingRef\":{\"kind\":\"podcast\",\"id\":\"p\"},\"skips\":0,\"title\":\"t\",\"trackID\":\"t\",\"wrongGuesses\":0}]")]
    public void CorruptFileIsEmpty(string content)
    {
        var store = new HistoryStore(_root);
        File.WriteAllText(store.FilePath, content);
        Assert.Empty(store.Load());
    }

    [Fact]
    public void CapKeepsTheNewestTenThousand()
    {
        var store = new HistoryStore(_root);
        var many = Enumerable.Range(0, HistoryStore.Capacity + 5)
            .Select(i => Missed with { Id = Guid.NewGuid(), TrackId = $"t{i}" }).ToList();
        store.Save(many);
        var loaded = store.Load();
        Assert.Equal(HistoryStore.Capacity, loaded.Count);
        Assert.Equal("t5", loaded[0].TrackId);
        Assert.Equal($"t{HistoryStore.Capacity + 4}", loaded[^1].TrackId);
        var appended = store.Append(Right);
        Assert.Equal(HistoryStore.Capacity, appended.Count);
        Assert.Equal("t6", appended[0].TrackId);
        Assert.Equal(Right, appended[^1]);
    }

    [Fact]
    public void FileShapeMatchesSwiftCodable()
    {
        var store = new HistoryStore(_root);
        store.Save([Right, Missed]);
        using var doc = JsonDocument.Parse(File.ReadAllText(store.FilePath));
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        var first = doc.RootElement[0];
        Assert.Equal(
            ["artists", "artworkURL", "correct", "date", "id", "listingName", "listingRef", "skips", "tierIndex", "title", "trackID", "wrongGuesses"],
            first.EnumerateObject().Select(p => p.Name));
        Assert.Equal("0B9A3C2E-1F4D-4E5A-9C7B-2D8E6F1A3B4C", first.GetProperty("id").GetString());
        Assert.Equal("2026-09-24T14:05:07Z", first.GetProperty("date").GetString());
        Assert.Equal("playlist", first.GetProperty("listingRef").GetProperty("kind").GetString());
        Assert.Equal(["id", "kind"], first.GetProperty("listingRef").EnumerateObject().Select(p => p.Name));
        Assert.Equal(1, first.GetProperty("tierIndex").GetInt32());
        // Absent optionals are left out, like Swift's encodeIfPresent.
        var second = doc.RootElement[1];
        Assert.Equal(
            ["artists", "correct", "date", "id", "listingName", "skips", "title", "trackID", "wrongGuesses"],
            second.EnumerateObject().Select(p => p.Name));
        Assert.Equal("2026-09-24T14:00:00Z", second.GetProperty("date").GetString());
    }

    [Fact]
    public void ReadsAFileWrittenBySwift()
    {
        // JSONEncoder with .iso8601 and .sortedKeys escapes slashes; UUIDs upper case.
        var swift = """
        [
          {
            "artists" : [ "Rex Orange", "Mabel Fitch" ],
            "artworkURL" : "https:\/\/image-cdn-fa.spotifycdn.com\/image\/ab67616d00001e0240e583b55bdddcf70516fa6c",
            "correct" : true,
            "date" : "2026-09-24T14:05:07Z",
            "id" : "0B9A3C2E-1F4D-4E5A-9C7B-2D8E6F1A3B4C",
            "listingName" : "Road trip",
            "listingRef" : { "id" : "37i9dQZF1DXcBWIGoYBM5M", "kind" : "playlist" },
            "skips" : 0,
            "tierIndex" : 1,
            "title" : "Bass Persuades",
            "trackID" : "4uLU6hMCjMI75M1A2tKUQC",
            "wrongGuesses" : 1
          }
        ]
        """;
        var store = new HistoryStore(_root);
        File.WriteAllText(store.FilePath, swift);
        Assert.Equal([Right], store.Load());
    }
}
