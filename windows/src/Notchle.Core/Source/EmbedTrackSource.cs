namespace Notchle.Core;

// STUB: agent W1 replaces this. Public API frozen.
/// Reads tracks from Spotify's public embed page (__NEXT_DATA__ JSON). No login, no API key.
public sealed class EmbedTrackSource(HttpClient http) : ITrackSource
{
    private readonly HttpClient _http = http;

    public Task<SourceListing> GetListingAsync(SourceRef source, CancellationToken cancellationToken = default)
        => throw new SourceException(SourceErrorKind.ParseFailed, "not implemented");
}
