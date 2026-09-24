namespace Notchle.Core;

// Shared, frozen contracts: the C# mirror of Sources/NotchleCore/Contracts/*.swift.
// The Swift core is the reference; behaviour is kept identical through the shared test vectors
// in /spec. Changes go through the integrator, not through an individual agent.
// This project targets plain net10.0 (no Windows APIs) so its tests run on any OS.

/// One song as the game knows it. <see cref="Id"/> is the Spotify track id (base62).
/// <param name="Uri">"spotify:track:&lt;id&gt;".</param>
/// <param name="Artists">All credited artists in credit order; a correct guess names every one.</param>
/// <param name="PreviewUrl">~30s preview clip, used by the preview player. Not every track has one.</param>
public sealed record Track(
    string Id,
    string Uri,
    string Title,
    IReadOnlyList<string> Artists,
    int DurationMs,
    Uri? PreviewUrl)
{
    // Value equality over the artist list, like the Swift struct (records compare lists by reference).
    public bool Equals(Track? other) =>
        other is not null && Id == other.Id && Uri == other.Uri && Title == other.Title
        && DurationMs == other.DurationMs && Equals(PreviewUrl, other.PreviewUrl)
        && Artists.SequenceEqual(other.Artists);

    public override int GetHashCode() => HashCode.Combine(Id, Uri, Title, DurationMs, Artists.Count);
}

public enum SourceKind { Playlist, Album, Artist }

/// A parsed Spotify link. Parsing lives in Source/SourceRefParser.cs.
public sealed record SourceRef(SourceKind Kind, string Id)
{
    public Uri EmbedUrl => new($"https://open.spotify.com/embed/{Kind.ToString().ToLowerInvariant()}/{Id}");
}

public sealed record SourceListing(SourceRef Ref, string Name, IReadOnlyList<Track> Tracks);

public sealed record Guess(string Title, string Artist);

/// Both parts must be right for the guess to count.
public sealed record Verdict(bool TitleCorrect, bool ArtistCorrect)
{
    public bool IsCorrect => TitleCorrect && ArtistCorrect;
}

/// How one track in a set ended.
public abstract record TrackOutcome
{
    /// Guessed right at snippet tier <paramref name="TierIndex"/> (0 = 5s).
    public sealed record Correct(int TierIndex) : TrackOutcome;
    /// Missed at the last tier, gave up, or skipped.
    public sealed record Missed : TrackOutcome;
}

public sealed record GameConfig(IReadOnlyList<double> Tiers, int SetSize = 20, double SnippetStart = 0)
{
    // Value equality over the tiers, like the Swift struct: a config loaded from JSON must equal
    // the same config built in code.
    public bool Equals(GameConfig? other) =>
        other is not null && SetSize == other.SetSize && SnippetStart == other.SnippetStart
        && Tiers.SequenceEqual(other.Tiers);

    public override int GetHashCode() => HashCode.Combine(SetSize, SnippetStart, Tiers.Count);

    /// Agreed defaults: 5, 10, 15 seconds; sets of 20; snippets from the start of the song.
    public static GameConfig Default { get; } = new(new[] { 5.0, 10.0, 15.0 });
}
