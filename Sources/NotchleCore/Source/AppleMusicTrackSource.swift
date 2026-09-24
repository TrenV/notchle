import Foundation

/// Reads the tracks behind a music.apple.com album or playlist link. No developer token, no
/// Apple ID.
///
/// - Albums: the documented iTunes Lookup API,
///   `https://itunes.apple.com/lookup?id=<album>&entity=song&country=<storefront>`, whose
///   `results` are one `collection` followed by `track` rows (`trackId`, `trackName`,
///   `artistName`, `trackTimeMillis`, `previewUrl`).
/// - Playlists: the Lookup API has no playlists, so the public page is read like Spotify's embed
///   page. Observed shape (2026-09-24): `<script type="application/json"
///   id="serialized-server-data">` holds `{ data: [ { data: { sections: [ … ] } } ] }`; the
///   section with `itemKind == "containerDetailHeaderLockup"` carries the playlist `title`, the
///   one with `itemKind == "trackLockup"` the songs: `{ title, duration (ms), subtitleLinks:
///   [{ title: <artist> }], artistName, contentDescriptor: { kind: "song", identifiers:
///   { storeAdamID } } }`. The server-rendered page lists the first 100 songs at most (Today's
///   Hits: 50 of 50). Undocumented; the fixture tests make a format change obvious.
///
/// Track ids are `am.<storeAdamID>` so they never collide with Spotify ids in progress.json;
/// the uri is `applemusic:song:<storeAdamID>`.
public struct AppleMusicTrackSource: TrackSource {
    private let http: HTTPClient

    public init(http: HTTPClient = URLSessionHTTPClient()) {
        self.http = http
    }

    static let requestHeaders: [String: String] = [
        "User-Agent": EmbedTrackSource.requestHeaders["User-Agent"]!,
        "Accept": "text/html,application/json",
    ]

    public static func trackID(adamID: String) -> String { "am.\(adamID)" }
    public static func uri(adamID: String) -> String { "applemusic:song:\(adamID)" }

    /// The URL fetched for `ref`; nil for a non-Apple ref.
    public static func requestURL(for ref: SourceRef) -> URL? {
        let storefront = ref.storefront ?? "us"
        switch ref.kind {
        case .appleMusicAlbum:
            var c = URLComponents(string: "https://itunes.apple.com/lookup")
            c?.queryItems = [
                URLQueryItem(name: "id", value: ref.id),
                URLQueryItem(name: "entity", value: "song"),
                URLQueryItem(name: "country", value: storefront),
            ]
            return c?.url
        case .appleMusicPlaylist:
            return URL(string: "https://music.apple.com/\(storefront)/playlist/\(ref.id)")
        default:
            return nil
        }
    }

    public func listing(for ref: SourceRef) async throws -> SourceListing {
        guard let url = Self.requestURL(for: ref) else { throw SourceError.invalidURL }
        let response: (status: Int, body: Data)
        do {
            response = try await http.get(url, headers: Self.requestHeaders)
        } catch let error as SourceError {
            throw error
        } catch is CancellationError {
            throw CancellationError()
        } catch {
            throw SourceError.network(String(describing: error))
        }
        switch response.status {
        case 200: break
        case 404: throw SourceError.notFound
        default: throw SourceError.network("HTTP \(response.status) from \(url.absoluteString)")
        }
        return ref.kind == .appleMusicAlbum
            ? try Self.parseLookup(response.body, ref: ref)
            : try Self.parsePlaylistPage(response.body, ref: ref)
    }

    // MARK: - Albums (iTunes Lookup)

    static func parseLookup(_ data: Data, ref: SourceRef) throws -> SourceListing {
        guard let root = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let results = root["results"] as? [[String: Any]]
        else { throw SourceError.parseFailed("lookup answer has no results array") }
        guard let collection = results.first(where: { $0["wrapperType"] as? String == "collection" }) else {
            throw SourceError.notFound
        }
        let name = string(collection["collectionName"]) ?? "Apple Music album"
        var tracks: [Track] = []
        var seen = Set<String>()
        for row in results where row["wrapperType"] as? String == "track" && row["kind"] as? String == "song" {
            guard let adam = intValue(row["trackId"]).map(String.init),
                  let title = string(row["trackName"]),
                  let artist = string(row["artistName"])
            else { continue }
            let track = Track(id: trackID(adamID: adam), uri: uri(adamID: adam), title: title,
                              artists: [artist], durationMs: intValue(row["trackTimeMillis"]) ?? 0,
                              previewURL: string(row["previewUrl"]).flatMap(URL.init(string:)))
            if seen.insert(track.id).inserted { tracks.append(track) }
        }
        guard !tracks.isEmpty else { throw SourceError.empty }
        return SourceListing(ref: ref, name: name, tracks: tracks)
    }

    // MARK: - Playlists (public page)

    static func parsePlaylistPage(_ html: Data, ref: SourceRef) throws -> SourceListing {
        let text = String(decoding: html, as: UTF8.self)
        guard let marker = text.range(of: "id=\"serialized-server-data\""),
              let tagEnd = text[marker.upperBound...].firstIndex(of: ">"),
              let close = text.range(of: "</script>", range: text.index(after: tagEnd)..<text.endIndex)
        else { throw SourceError.parseFailed("no serialized-server-data block in the Apple Music page") }
        let json = Data(text[text.index(after: tagEnd)..<close.lowerBound].utf8)
        guard let root = try? JSONSerialization.jsonObject(with: json) else {
            throw SourceError.parseFailed("serialized-server-data is not valid JSON")
        }
        let sections = allSections(in: root)
        let header = sections.first { $0["itemKind"] as? String == "containerDetailHeaderLockup" }
        let name = ((header?["items"] as? [[String: Any]])?.first).flatMap { string($0["title"]) } ?? "Apple Music playlist"
        let items = sections.filter { $0["itemKind"] as? String == "trackLockup" }
            .flatMap { ($0["items"] as? [Any]) ?? [] }
        guard !items.isEmpty else { throw SourceError.parseFailed("no trackLockup section in the Apple Music page") }

        var tracks: [Track] = []
        var seen = Set<String>()
        for case let item as [String: Any] in items {
            guard let descriptor = item["contentDescriptor"] as? [String: Any],
                  descriptor["kind"] as? String == "song",
                  let adam = string((descriptor["identifiers"] as? [String: Any])?["storeAdamID"]),
                  adam.allSatisfy(\.isNumber),
                  let title = string(item["title"])
            else { continue }
            if item["isDisabled"] as? Bool == true { continue }
            var artists = ((item["subtitleLinks"] as? [[String: Any]]) ?? []).compactMap { string($0["title"]) }
            if artists.isEmpty, let artist = string(item["artistName"]) { artists = [artist] }
            guard !artists.isEmpty else { continue }
            let track = Track(id: trackID(adamID: adam), uri: uri(adamID: adam), title: title,
                              artists: artists, durationMs: intValue(item["duration"]) ?? 0, previewURL: nil)
            if seen.insert(track.id).inserted { tracks.append(track) }
        }
        guard !tracks.isEmpty else { throw SourceError.empty }
        return SourceListing(ref: ref, name: name, tracks: tracks)
    }

    /// Every dictionary with a `sections` array's elements, breadth-first (in case Apple nests it
    /// differently).
    static func allSections(in root: Any) -> [[String: Any]] {
        var out: [[String: Any]] = []
        var queue: [Any] = [root]
        var head = 0
        while head < queue.count {
            let node = queue[head]
            head += 1
            if let dict = node as? [String: Any] {
                if let sections = dict["sections"] as? [[String: Any]] { out += sections; continue }
                for key in dict.keys.sorted() { queue.append(dict[key]!) }
            } else if let array = node as? [Any] {
                queue.append(contentsOf: array)
            }
        }
        return out
    }

    private static func intValue(_ value: Any?) -> Int? {
        if let int = value as? Int { return int }
        if let double = value as? Double { return Int(exactly: double.rounded()) }
        return nil
    }

    private static func string(_ value: Any?) -> String? {
        guard let s = value as? String, !s.isEmpty else { return nil }
        return s
    }
}

/// Sends Apple Music refs to `AppleMusicTrackSource` and the rest to `EmbedTrackSource`.
public struct RoutingTrackSource: TrackSource {
    private let spotify: TrackSource
    private let appleMusic: TrackSource

    public init(spotify: TrackSource = EmbedTrackSource(), appleMusic: TrackSource = AppleMusicTrackSource()) {
        self.spotify = spotify
        self.appleMusic = appleMusic
    }

    public func listing(for ref: SourceRef) async throws -> SourceListing {
        try await (ref.kind.isAppleMusic ? appleMusic : spotify).listing(for: ref)
    }
}
