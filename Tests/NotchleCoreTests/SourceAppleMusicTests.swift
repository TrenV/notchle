import Foundation
import Testing
@testable import NotchleCore

@Suite struct SourceAppleMusicTests {
    static func fixture(_ name: String, _ ext: String) throws -> Data {
        let url = try #require(Bundle.module.url(forResource: name, withExtension: ext, subdirectory: "Fixtures"))
        return try Data(contentsOf: url)
    }

    static let playlist = SourceRef(kind: .appleMusicPlaylist, id: "pl.f4d106fed2bd41149aaacabb233eb5eb", storefront: "us")
    static let album = SourceRef(kind: .appleMusicAlbum, id: "1440857781", storefront: "nl")

    @Test(arguments: [
        ("https://music.apple.com/us/playlist/todays-hits/pl.f4d106fed2bd41149aaacabb233eb5eb", playlist),
        ("music.apple.com/us/playlist/pl.f4d106fed2bd41149aaacabb233eb5eb?l=nl", playlist),
        ("  https://music.apple.com/nl/album/in-between-dreams/1440857781  ", album),
        ("https://music.apple.com/nl/album/better-together/1440857781?i=1440857786&uo=4", album),
        ("https://MUSIC.apple.com/NL/album/x/1440857781#top", album),
    ])
    func parsesAppleMusicLinks(input: String, expected: SourceRef) {
        #expect(SourceRef(string: input) == expected)
    }

    @Test(arguments: [
        "https://music.apple.com/us/song/better-together/1440857786",
        "https://music.apple.com/us/artist/jack-johnson/909253",
        "https://music.apple.com/us/album/x/12ab",
        "https://music.apple.com/us/playlist/x/pl.",
        "https://music.apple.com/usa/album/x/1440857781",
        "https://music.apple.com.evil.com/us/album/x/1440857781",
        "https://evil.com/music.apple.com/us/album/x/1440857781",
        "https://music.apple.com/us/playlist/x/pl.ab\"cd",
    ])
    func rejectsOtherAppleLinks(input: String) {
        #expect(SourceRef(string: input) == nil)
    }

    @Test func spotifyRefsEncodeWithoutStorefrontAndOldJSONDecodes() throws {
        let spotify = SourceRef(kind: .playlist, id: "37i9dQZF1DXcBWIGoYBM5M")
        let json = String(decoding: try JSONEncoder().encode(spotify), as: UTF8.self)
        #expect(!json.contains("storefront"))
        let old = Data(#"{"kind":"album","id":"0ETFjACtuP2ADo6LFhL6HN"}"#.utf8)
        #expect(try JSONDecoder().decode(SourceRef.self, from: old) == SourceRef(kind: .album, id: "0ETFjACtuP2ADo6LFhL6HN"))
        let apple = try JSONDecoder().decode(SourceRef.self, from: JSONEncoder().encode(Self.playlist))
        #expect(apple == Self.playlist)
        // Raw values are persisted: pin them.
        #expect(SourceKind.appleMusicAlbum.rawValue == "appleMusicAlbum")
        #expect(SourceKind.appleMusicPlaylist.rawValue == "appleMusicPlaylist")
        #expect(PlayerMode.appleMusic.rawValue == "appleMusic")
    }

    @Test func albumUsesTheLookupAPI() async throws {
        let http = SourceFakeHTTPClient(.ok(try Self.fixture("AppleMusic-lookup-album-in-between-dreams", "json")))
        let listing = try await AppleMusicTrackSource(http: http).listing(for: Self.album)
        let url = try #require(await http.requests.first?.url)
        #expect(url.absoluteString == "https://itunes.apple.com/lookup?id=1440857781&entity=song&country=nl")
        #expect(listing.name == "In Between Dreams (Bonus Track Version)")
        #expect(listing.tracks.count == 15)
        let first = try #require(listing.tracks.first)
        #expect(first == Track(id: "am.1440857786", uri: "applemusic:song:1440857786", title: "Better Together",
                               artists: ["Jack Johnson"], durationMs: first.durationMs, previewURL: first.previewURL))
        #expect(first.durationMs > 150_000 && first.previewURL != nil)
    }

    @Test func playlistReadsThePublicPage() async throws {
        let http = SourceFakeHTTPClient(.ok(try Self.fixture("AppleMusic-playlist-todays-hits", "html")))
        let listing = try await AppleMusicTrackSource(http: http).listing(for: Self.playlist)
        #expect(await http.requests.first?.url.absoluteString == "https://music.apple.com/us/playlist/pl.f4d106fed2bd41149aaacabb233eb5eb")
        #expect(listing.name == "Today’s Hits")
        #expect(listing.tracks.count == 8)
        #expect(listing.tracks[0].title == "So Good (feat. Kendrick Lamar)")
        #expect(listing.tracks[0].artists == ["Jhené Aiko"])
        #expect(listing.tracks[0].durationMs == 237_251)
        #expect(listing.tracks[0].id == "am.6810716062")
        #expect(listing.tracks[2].artists == ["KAROL G", "Judeline", "rusowsky"])
    }

    @Test func pageWithoutDataFailsToParse() async {
        let http = SourceFakeHTTPClient(.ok(Data("<html></html>".utf8)))
        await #expect(throws: SourceError.parseFailed("no serialized-server-data block in the Apple Music page")) {
            try await AppleMusicTrackSource(http: http).listing(for: Self.playlist)
        }
    }

    @Test func statusCodesMap() async {
        await #expect(throws: SourceError.notFound) {
            try await AppleMusicTrackSource(http: SourceFakeHTTPClient(.status(404, Data()))).listing(for: Self.album)
        }
        await #expect(throws: SourceError.notFound) {
            try await AppleMusicTrackSource(http: SourceFakeHTTPClient(.ok(Data(#"{"resultCount":0,"results":[]}"#.utf8)))).listing(for: Self.album)
        }
    }

    @Test func routingSendsEachKindToItsSource() async throws {
        let apple = SourceFakeHTTPClient(.ok(try Self.fixture("AppleMusic-lookup-album-in-between-dreams", "json")))
        let spotify = SourceFakeHTTPClient(.status(500, Data()))
        let router = RoutingTrackSource(spotify: EmbedTrackSource(http: spotify), appleMusic: AppleMusicTrackSource(http: apple))
        _ = try await router.listing(for: Self.album)
        await #expect(throws: SourceError.self) { try await router.listing(for: SourceRef(kind: .playlist, id: "37i9dQZF1DXcBWIGoYBM5M")) }
        #expect(await apple.requests.count == 1)
        #expect(await spotify.requests.count == 1)
    }

    @Test func fixturesCarryNoTokens() throws {
        for (name, ext) in [("AppleMusic-playlist-todays-hits", "html"), ("AppleMusic-lookup-album-in-between-dreams", "json")] {
            let text = String(decoding: try Self.fixture(name, ext), as: UTF8.self)
            #expect(!text.contains("userTokenHash") && !text.contains("eyJ") && !text.lowercased().contains("token"), "\(name)")
        }
    }
}
