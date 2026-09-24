import Foundation

extension SourceRef {
    /// Parses open.spotify.com links (with or without locale prefix like /intl-nl/, query
    /// strings, trailing slashes) and `spotify:<kind>:<id>` URIs. nil when not a supported link.
    ///
    /// Accepted shapes (surrounding whitespace ignored):
    /// - `https://open.spotify.com/<kind>/<id>`, also `http://` or no scheme at all
    /// - an optional `/intl-xx/` locale segment and/or `/embed/` segment before `<kind>`
    /// - an optional trailing slash, `?query` and `#fragment`
    /// - `spotify:<kind>:<id>`
    ///
    /// `<kind>` is playlist, album or artist; `<id>` is 22 base62 characters. Tracks, shows,
    /// episodes, users, other hosts and malformed ids give nil.
    public init?(string: String) {
        let text = string.trimmingCharacters(in: .whitespacesAndNewlines)
        if let apple = Self.appleMusic(text) {
            self = apple
            return
        }
        let parts: (kind: Substring, id: Substring)?
        if text.lowercased().hasPrefix("spotify:") {
            parts = Self.uriParts(text)
        } else {
            parts = Self.webParts(text)
        }
        guard let parts,
              let kind = SourceKind(rawValue: parts.kind.lowercased()),
              Self.isSpotifyID(parts.id)
        else { return nil }
        self.init(kind: kind, id: String(parts.id))
    }

    /// The public embed page for this ref.
    public var embedURL: URL {
        URL(string: "https://open.spotify.com/embed/\(kind.rawValue)/\(id)")!
    }

    /// `[https://]music.apple.com/<storefront>/(album|playlist)[/<slug>]/<id>[?i=…]`. Album ids
    /// are digits, playlist ids `pl.` + letters/digits. A song link (`album/…?i=<song>`) gives
    /// its album. Songs, artists, other hosts and malformed ids give nil.
    static func appleMusic(_ text: String) -> SourceRef? {
        var rest = Substring(text)
        for scheme in ["https://", "http://"] where rest.lowercased().hasPrefix(scheme) {
            rest = rest.dropFirst(scheme.count)
            break
        }
        if let cut = rest.firstIndex(where: { $0 == "?" || $0 == "#" }) { rest = rest[..<cut] }
        let segments = rest.split(separator: "/")
        guard segments.count == 4 || segments.count == 5,
              segments[0].lowercased() == "music.apple.com"
        else { return nil }
        let storefront = segments[1].lowercased()
        guard storefront.count == 2, storefront.allSatisfy({ $0.isASCII && $0.isLetter }) else { return nil }
        let id = String(segments[segments.count - 1])
        switch segments[2].lowercased() {
        case "album":
            guard !id.isEmpty, id.count <= 20, id.allSatisfy({ $0.isASCII && $0.isNumber }) else { return nil }
            return SourceRef(kind: .appleMusicAlbum, id: id, storefront: storefront)
        case "playlist":
            let body = id.dropFirst(3)
            guard id.hasPrefix("pl."), !body.isEmpty, body.count <= 64,
                  body.allSatisfy({ $0.isASCII && ($0.isLetter || $0.isNumber) })
            else { return nil }
            return SourceRef(kind: .appleMusicPlaylist, id: id, storefront: storefront)
        default:
            return nil
        }
    }

    /// Spotify ids are 22 base62 characters.
    static func isSpotifyID<S: StringProtocol>(_ id: S) -> Bool {
        id.utf8.count == 22 && id.utf8.allSatisfy { byte in
            (UInt8(ascii: "0")...UInt8(ascii: "9")).contains(byte)
                || (UInt8(ascii: "a")...UInt8(ascii: "z")).contains(byte)
                || (UInt8(ascii: "A")...UInt8(ascii: "Z")).contains(byte)
        }
    }

    /// `spotify:<kind>:<id>`. Legacy `spotify:user:<name>:playlist:<id>` is rejected.
    private static func uriParts(_ text: String) -> (kind: Substring, id: Substring)? {
        let fields = text.split(separator: ":", omittingEmptySubsequences: false)
        guard fields.count == 3 else { return nil }
        return (fields[1], fields[2])
    }

    /// `[scheme://]open.spotify.com[/intl-xx][/embed]/<kind>/<id>[/][?query][#fragment]`
    private static func webParts(_ text: String) -> (kind: Substring, id: Substring)? {
        var rest = Substring(text)
        for scheme in ["https://", "http://"] where rest.lowercased().hasPrefix(scheme) {
            rest = rest.dropFirst(scheme.count)
            break
        }
        if let cut = rest.firstIndex(where: { $0 == "?" || $0 == "#" }) {
            rest = rest[..<cut]
        }
        guard let slash = rest.firstIndex(of: "/") else { return nil }
        // Exact host match: rejects ports, userinfo, subdomains and look-alike hosts.
        guard rest[..<slash].lowercased() == "open.spotify.com" else { return nil }

        var segments = rest[slash...].split(separator: "/")
        var sawLocale = false
        var sawEmbed = false
        while let first = segments.first {
            if !sawLocale, first.lowercased().hasPrefix("intl-") {
                sawLocale = true
            } else if !sawEmbed, first.lowercased() == "embed" {
                sawEmbed = true
            } else {
                break
            }
            segments.removeFirst()
        }
        guard segments.count == 2 else { return nil }
        return (segments[0], segments[1])
    }
}
