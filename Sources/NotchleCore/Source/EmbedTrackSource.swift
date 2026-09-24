import Foundation

/// Reads tracks from Spotify's public embed page (`__NEXT_DATA__` JSON). No login, no API key.
///
/// Observed page shape (2026-09-24, playlist/album/artist embeds alike):
/// `props.pageProps.state.data.entity` = `{ type, name, title, subtitle, uri, id, isPlayable,
/// playabilityReason, trackList: [item] }`, where each item is `{ uri, uid, title, subtitle,
/// duration, isPlayable, playabilityReason, isExplicit, isNineteenPlus, contentRatings,
/// audioPreview: { format, url }, entityType }`.
///
/// Limits seen live: playlist embeds list at most 100 items (All Out 80s has 150 songs, the
/// embed lists 100); artist embeds list the artist's top 10 tracks.
public struct EmbedTrackSource: TrackSource {
    private let http: HTTPClient

    public init(http: HTTPClient = URLSessionHTTPClient()) {
        self.http = http
    }

    /// Spotify serves the server-rendered page (with `__NEXT_DATA__`) to desktop browsers.
    static let requestHeaders: [String: String] = [
        "User-Agent": "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36",
        "Accept": "text/html,application/xhtml+xml",
    ]

    public func listing(for ref: SourceRef) async throws -> SourceListing {
        let response: (status: Int, body: Data)
        do {
            response = try await http.get(ref.embedURL, headers: Self.requestHeaders)
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
        default: throw SourceError.network("HTTP \(response.status) from \(ref.embedURL.absoluteString)")
        }
        return try Self.parse(html: response.body, ref: ref)
    }

    // MARK: - Parsing

    static func parse(html: Data, ref: SourceRef) throws -> SourceListing {
        let json = try nextData(in: String(decoding: html, as: UTF8.self))
        let root: Any
        do {
            root = try JSONSerialization.jsonObject(with: json)
        } catch {
            throw SourceError.parseFailed("__NEXT_DATA__ is not valid JSON")
        }
        guard let entity = findEntity(in: root) else {
            throw SourceError.parseFailed("no entity with a trackList array in __NEXT_DATA__")
        }
        guard let name = nonEmptyString(entity["name"]) ?? nonEmptyString(entity["title"]) else {
            throw SourceError.parseFailed("entity has no name or title")
        }
        let items = entity["trackList"] as? [Any] ?? []

        var tracks: [Track] = []
        var seen = Set<String>()
        var malformed = 0
        for item in items {
            switch decodeItem(item) {
            case .track(let track):
                if seen.insert(track.id).inserted { tracks.append(track) }
            case .skipped:
                continue
            case .malformed:
                malformed += 1
            }
        }
        if tracks.isEmpty {
            // Every item unreadable points at a changed page format, not an empty listing.
            if malformed > 0, malformed == items.count {
                throw SourceError.parseFailed("no trackList item had a readable uri, title and subtitle")
            }
            throw SourceError.empty
        }
        return SourceListing(ref: ref, name: name, tracks: tracks)
    }

    /// The JSON text inside `<script id="__NEXT_DATA__" …>…</script>`.
    static func nextData(in html: String) throws -> Data {
        guard let marker = html.range(of: "id=\"__NEXT_DATA__\""),
              let tagEnd = html[marker.upperBound...].firstIndex(of: ">"),
              let close = html.range(of: "</script>", range: html.index(after: tagEnd)..<html.endIndex)
        else {
            throw SourceError.parseFailed("no <script id=\"__NEXT_DATA__\"> block in the embed page")
        }
        return Data(html[html.index(after: tagEnd)..<close.lowerBound].utf8)
    }

    /// `props.pageProps.state.data.entity` when it has a trackList, else the first dictionary
    /// with a `trackList` array found breadth-first (in case Spotify moves it).
    static func findEntity(in root: Any) -> [String: Any]? {
        let known = ["props", "pageProps", "state", "data", "entity"].reduce(Optional(root)) { node, key in
            (node as? [String: Any])?[key]
        }
        if let entity = known as? [String: Any], entity["trackList"] is [Any] { return entity }

        var queue: [Any] = [root]
        var head = 0
        while head < queue.count {
            let node = queue[head]
            head += 1
            if let dict = node as? [String: Any] {
                if dict["trackList"] is [Any] { return dict }
                // Sorted keys keep the search deterministic across runs.
                for key in dict.keys.sorted() { queue.append(dict[key]!) }
            } else if let array = node as? [Any] {
                queue.append(contentsOf: array)
            }
        }
        return nil
    }

    enum ItemResult {
        case track(Track)
        /// A well-formed item Notchle can't play: not a track (episode, local file) or marked unplayable.
        case skipped
        /// Missing the fields we need.
        case malformed
    }

    static func decodeItem(_ item: Any) -> ItemResult {
        guard let dict = item as? [String: Any], let uri = nonEmptyString(dict["uri"]) else { return .malformed }
        let prefix = "spotify:track:"
        guard uri.hasPrefix(prefix) else { return .skipped }
        let id = String(uri.dropFirst(prefix.count))
        guard SourceRef.isSpotifyID(id) else { return .skipped }
        if let playable = dict["isPlayable"] as? Bool, !playable { return .skipped }
        guard let title = nonEmptyString(dict["title"]),
              let subtitle = dict["subtitle"] as? String
        else { return .malformed }
        let artists = splitArtists(subtitle)
        guard !artists.isEmpty else { return .malformed }

        let durationMs = intValue(dict["duration"]) ?? 0
        let preview = ((dict["audioPreview"] as? [String: Any]).flatMap { nonEmptyString($0["url"]) })
            .flatMap(URL.init(string:))
        return .track(Track(id: id, uri: uri, title: title, artists: artists, durationMs: durationMs, previewURL: preview))
    }

    /// Spotify joins credited artists with `,` + U+00A0 ("KAROL G,\u{A0}Judeline"). A comma
    /// followed by a plain space is part of a name ("Earth, Wind & Fire", "Tyler, The Creator"),
    /// so only the comma+NBSP pair separates artists.
    static func splitArtists(_ subtitle: String) -> [String] {
        let trim = CharacterSet.whitespacesAndNewlines.union(CharacterSet(charactersIn: "\u{00A0}"))
        return subtitle.components(separatedBy: ",\u{00A0}")
            .map { $0.trimmingCharacters(in: trim) }
            .filter { !$0.isEmpty }
    }

    /// JSON numbers arrive as Int, Double or NSNumber depending on the Foundation build.
    private static func intValue(_ value: Any?) -> Int? {
        if let int = value as? Int { return int }
        if let double = value as? Double { return Int(exactly: double.rounded()) }
        return nil
    }

    private static func nonEmptyString(_ value: Any?) -> String? {
        guard let string = value as? String, !string.isEmpty else { return nil }
        return string
    }
}
