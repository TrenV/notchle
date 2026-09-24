import Foundation
import Testing
@testable import NotchleCore

private let playlistID = "37i9dQZF1DXcBWIGoYBM5M"
private let albumID = "0ETFjACtuP2ADo6LFhL6HN"
private let artistID = "06HL4z0CvFAxyc27GXpf02"

struct SourceRefParsingTests {
    static let accepted: [(String, SourceKind, String)] = [
        ("https://open.spotify.com/playlist/\(playlistID)", .playlist, playlistID),
        ("https://open.spotify.com/album/\(albumID)", .album, albumID),
        ("https://open.spotify.com/artist/\(artistID)", .artist, artistID),
        ("http://open.spotify.com/playlist/\(playlistID)", .playlist, playlistID),
        ("open.spotify.com/playlist/\(playlistID)", .playlist, playlistID),
        ("HTTPS://Open.Spotify.com/playlist/\(playlistID)", .playlist, playlistID),
        ("https://open.spotify.com/playlist/\(playlistID)?si=a1b2c3d4e5f6", .playlist, playlistID),
        ("https://open.spotify.com/playlist/\(playlistID)?si=abc&pi=u-xyz", .playlist, playlistID),
        ("https://open.spotify.com/playlist/\(playlistID)#frag", .playlist, playlistID),
        ("https://open.spotify.com/playlist/\(playlistID)/", .playlist, playlistID),
        ("https://open.spotify.com/playlist/\(playlistID)/?si=abc", .playlist, playlistID),
        ("https://open.spotify.com/intl-nl/playlist/\(playlistID)", .playlist, playlistID),
        ("https://open.spotify.com/intl-pt-BR/album/\(albumID)?si=x", .album, albumID),
        ("https://open.spotify.com/embed/playlist/\(playlistID)", .playlist, playlistID),
        ("https://open.spotify.com/embed/artist/\(artistID)?utm_source=generator", .artist, artistID),
        ("https://open.spotify.com/intl-de/embed/album/\(albumID)", .album, albumID),
        ("  https://open.spotify.com/album/\(albumID)\n", .album, albumID),
        ("\thttps://open.spotify.com/artist/\(artistID) ", .artist, artistID),
        ("spotify:playlist:\(playlistID)", .playlist, playlistID),
        ("spotify:album:\(albumID)", .album, albumID),
        ("spotify:artist:\(artistID)", .artist, artistID),
        (" spotify:album:\(albumID) ", .album, albumID),
    ]

    @Test(arguments: accepted)
    func parsesSupportedLinks(input: String, kind: SourceKind, id: String) {
        #expect(SourceRef(string: input) == SourceRef(kind: kind, id: id))
    }

    static let rejected: [String] = [
        "",
        "   ",
        "not a url",
        // Unsupported kinds.
        "https://open.spotify.com/track/2FZcjBYK4dTt48q94pJbJD",
        "spotify:track:2FZcjBYK4dTt48q94pJbJD",
        "https://open.spotify.com/show/4rOoJ6Egrf8K2IrywzwOMk",
        "https://open.spotify.com/episode/512ojhOuo1ktJprKbVcKyQ",
        "spotify:episode:512ojhOuo1ktJprKbVcKyQ",
        "https://open.spotify.com/user/spotify",
        "https://open.spotify.com/user/spotify/playlist/\(playlistID)",
        "spotify:user:spotify:playlist:\(playlistID)",
        // Other hosts and look-alikes.
        "https://spotify.com/playlist/\(playlistID)",
        "https://play.spotify.com/playlist/\(playlistID)",
        "https://open.spotify.com.evil.example/playlist/\(playlistID)",
        "https://evil.example/open.spotify.com/playlist/\(playlistID)",
        "https://open.spotify.com:8443/playlist/\(playlistID)",
        "https://user@open.spotify.com/playlist/\(playlistID)",
        "ftp://open.spotify.com/playlist/\(playlistID)",
        // Bad ids.
        "https://open.spotify.com/playlist/37i9dQZF1DXcBWIGoYBM5",    // 21 chars
        "https://open.spotify.com/playlist/37i9dQZF1DXcBWIGoYBM5Mx",  // 23 chars
        "https://open.spotify.com/playlist/37i9dQZF1DXcBWIGoYB-5M",   // non-base62
        "https://open.spotify.com/playlist/37i9dQZF1DXcBWIGoYBM5É",   // non-ASCII
        "https://open.spotify.com/playlist/",
        "https://open.spotify.com/playlist",
        "https://open.spotify.com/",
        "spotify:playlist:",
        "spotify:playlist:short",
        "spotify:playlist:\(playlistID):extra",
        // Wrong shapes.
        "https://open.spotify.com/playlist/\(playlistID)/tracks",
        "https://open.spotify.com/embed/embed/playlist/\(playlistID)",
        "https://open.spotify.com/intl-nl/intl-de/playlist/\(playlistID)",
        "https://open.spotify.com/\(playlistID)",
    ]

    @Test(arguments: rejected)
    func rejectsUnsupportedLinks(input: String) {
        #expect(SourceRef(string: input) == nil)
    }

    @Test func embedURLRoundTripsThroughParser() throws {
        for kind in SourceKind.allCases where !kind.isAppleMusic {
            let ref = SourceRef(kind: kind, id: playlistID)
            #expect(ref.embedURL.absoluteString == "https://open.spotify.com/embed/\(kind.rawValue)/\(playlistID)")
            #expect(SourceRef(string: ref.embedURL.absoluteString) == ref)
        }
    }
}
