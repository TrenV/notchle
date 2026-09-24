import Foundation

// STUB — Wave 2, agent A replaces this file.
/// Reads tracks from Spotify's public embed page (`__NEXT_DATA__` JSON). No login, no API key.
public struct EmbedTrackSource: TrackSource {
    private let http: HTTPClient

    public init(http: HTTPClient = URLSessionHTTPClient()) {
        self.http = http
    }

    public func listing(for ref: SourceRef) async throws -> SourceListing {
        throw SourceError.parseFailed("not implemented")
    }
}
