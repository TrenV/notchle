import Foundation

/// Finds a track's album cover through Spotify's public oEmbed endpoint (no auth):
/// `GET https://open.spotify.com/oembed?url=https://open.spotify.com/track/<id>` returns JSON
/// whose `thumbnail_url` is a 300×300 cover.
///
/// Spoiler rule: callers resolve a track only once its answer is on screen (correct/revealed,
/// i.e. when `.recordOutcome` fires); never prefetch a track that is still being guessed.
public actor ArtworkResolver {
    private let http: HTTPClient
    /// Successful lookups only; a failure is retried next time.
    private var cache: [String: URL] = [:]

    public init(http: HTTPClient = URLSessionHTTPClient()) {
        self.http = http
    }

    /// The cover URL, or nil on any failure (network, status, bad JSON). Never throws.
    public func artworkURL(forTrackID trackID: String) async -> URL? {
        if let hit = cache[trackID] { return hit }
        guard let request = Self.oEmbedURL(trackID: trackID),
              let (status, body) = try? await http.get(request, headers: ["Accept": "application/json"]),
              (200..<300).contains(status),
              let url = Self.parseOEmbed(body)
        else { return nil }
        cache[trackID] = url
        return url
    }

    /// The oEmbed request for a track id (base62; anything else is rejected).
    public static func oEmbedURL(trackID: String) -> URL? {
        guard !trackID.isEmpty, trackID.allSatisfy({ $0.isASCII && ($0.isLetter || $0.isNumber) }) else { return nil }
        var c = URLComponents(string: "https://open.spotify.com/oembed")
        c?.queryItems = [URLQueryItem(name: "url", value: "https://open.spotify.com/track/\(trackID)")]
        return c?.url
    }

    /// `thumbnail_url` from an oEmbed JSON body, if it is an http(s) URL.
    public static func parseOEmbed(_ data: Data) -> URL? {
        guard let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let string = object["thumbnail_url"] as? String,
              let url = URL(string: string),
              let scheme = url.scheme?.lowercased(), scheme == "https" || scheme == "http"
        else { return nil }
        return url
    }
}
