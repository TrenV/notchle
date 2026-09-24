import Foundation
import Testing
@testable import NotchleCore

/// Serves canned responses and records what was requested.
actor SourceFakeHTTPClient: HTTPClient {
    enum Reply: Sendable {
        case ok(Data)
        case status(Int, Data)
        case fail(any Error & Sendable)
    }

    private let reply: Reply
    private(set) var requests: [(url: URL, headers: [String: String])] = []

    init(_ reply: Reply) { self.reply = reply }

    func get(_ url: URL, headers: [String: String]) async throws -> (status: Int, body: Data) {
        requests.append((url, headers))
        switch reply {
        case .ok(let body): return (200, body)
        case .status(let code, let body): return (code, body)
        case .fail(let error): throw error
        }
    }
}

enum SourceFixtures {
    static func data(_ name: String) throws -> Data {
        let url = try #require(Bundle.module.url(forResource: name, withExtension: "html", subdirectory: "Fixtures"))
        return try Data(contentsOf: url)
    }

    /// A minimal embed page wrapping `trackList` items in the real entity shape.
    static func page(name: String? = "Synthetic", items: String) -> Data {
        let nameField = name.map { "\"name\":\"\($0)\"," } ?? ""
        let html = """
        <!DOCTYPE html><html><body><div id="__next"></div>
        <script id="__NEXT_DATA__" type="application/json">{"props":{"pageProps":{"state":{"data":{"entity":{\(nameField)"type":"playlist","trackList":[\(items)]}}}}}}</script>
        </body></html>
        """
        return Data(html.utf8)
    }

    static func item(
        id: String,
        title: String = "Song",
        subtitle: String = "Artist",
        uriKind: String = "track",
        isPlayable: Bool = true,
        duration: Int = 200_000,
        preview: String? = "https://p.scdn.co/mp3-preview/abc"
    ) -> String {
        let previewField = preview.map { ",\"audioPreview\":{\"format\":\"MP3_96\",\"url\":\"\($0)\"}" } ?? ""
        return """
        {"uri":"spotify:\(uriKind):\(id)","uid":"u","title":"\(title)","subtitle":"\(subtitle)","duration":\(duration),"isPlayable":\(isPlayable),"playabilityReason":"\(isPlayable ? "PLAYABLE" : "NOT_AVAILABLE")","entityType":"\(uriKind)"\(previewField)}
        """
    }
}

private let ref = SourceRef(kind: .playlist, id: "37i9dQZF1DXcBWIGoYBM5M")
private let idA = "2FZcjBYK4dTt48q94pJbJD"
private let idB = "0pNeVovbiZHkulpGeOx1Gj"
private let idC = "53iuhJlwXhSER5J2IYYv1W"

private func listing(_ body: Data, ref: SourceRef = ref) async throws -> SourceListing {
    try await EmbedTrackSource(http: SourceFakeHTTPClient(.ok(body))).listing(for: ref)
}

private func expectError(_ expected: SourceError, _ reply: SourceFakeHTTPClient.Reply) async {
    await #expect(throws: expected) {
        try await EmbedTrackSource(http: SourceFakeHTTPClient(reply)).listing(for: ref)
    }
}

struct SourceEmbedTrackSourceTests {
    // MARK: Real fixtures

    @Test func requestsEmbedURLWithBrowserUserAgent() async throws {
        let http = SourceFakeHTTPClient(.ok(try SourceFixtures.data("embed-playlist-todays-top-hits")))
        _ = try await EmbedTrackSource(http: http).listing(for: ref)
        let requests = await http.requests
        #expect(requests.count == 1)
        #expect(requests.first?.url == URL(string: "https://open.spotify.com/embed/playlist/37i9dQZF1DXcBWIGoYBM5M"))
        let agent = try #require(requests.first?.headers["User-Agent"])
        #expect(agent.hasPrefix("Mozilla/5.0 (Macintosh"))
    }

    @Test func parsesTodaysTopHits() async throws {
        let result = try await listing(try SourceFixtures.data("embed-playlist-todays-top-hits"))
        #expect(result.ref == ref)
        #expect(result.name == "Today\u{2019}s Top Hits")
        #expect(result.tracks.count == 50)
        #expect(Set(result.tracks.map(\.id)).count == 50)
        #expect(result.tracks.first == Track(
            id: "2FZcjBYK4dTt48q94pJbJD",
            uri: "spotify:track:2FZcjBYK4dTt48q94pJbJD",
            title: "Bass Persuades",
            artists: ["Miley Cyrus"],
            durationMs: 202_460,
            previewURL: URL(string: "https://p.scdn.co/mp3-preview/e57b7c5fcb52b8bb4c781cc1e2e5b25f8a6c6e79")
        ))
        // Subtitle is "KAROL G,\u{A0}Judeline,\u{A0}rusowsky" in the page.
        let multi = try #require(result.tracks.first { $0.artists.first == "KAROL G" })
        #expect(multi.artists == ["KAROL G", "Judeline", "rusowsky"])
        for track in result.tracks {
            #expect(track.uri == "spotify:track:\(track.id)")
            #expect(!track.title.isEmpty)
            #expect(!track.artists.isEmpty)
            #expect(track.artists.allSatisfy { !$0.contains("\u{00A0}") && $0 == $0.trimmingCharacters(in: .whitespaces) })
            #expect(track.durationMs > 0)
            #expect(track.previewURL != nil)
        }
    }

    @Test func parsesAlbumEmbed() async throws {
        let album = SourceRef(kind: .album, id: "0ETFjACtuP2ADo6LFhL6HN")
        let result = try await listing(try SourceFixtures.data("Source-embed-album-abbey-road"), ref: album)
        #expect(result.ref == album)
        #expect(result.name == "Abbey Road (Remastered)")
        #expect(result.tracks.count == 17)
        #expect(result.tracks.first?.title == "Come Together - Remastered 2009")
        #expect(result.tracks.allSatisfy { $0.artists == ["The Beatles"] })
    }

    @Test func parsesArtistEmbedTopTracks() async throws {
        let artist = SourceRef(kind: .artist, id: "06HL4z0CvFAxyc27GXpf02")
        let result = try await listing(try SourceFixtures.data("Source-embed-artist-taylor-swift"), ref: artist)
        #expect(result.name == "Taylor Swift")
        #expect(result.tracks.count == 10)
        #expect(result.tracks.map(\.title).prefix(2) == ["The Fate of Ophelia", "Blank Space"])
    }

    @Test func largePlaylistEmbedIsCappedAt100AndKeepsCommaNames() async throws {
        // All Out 80s had 150 songs on 2026-09-24; the embed lists the first 100.
        let result = try await listing(try SourceFixtures.data("Source-embed-playlist-all-out-80s"))
        #expect(result.name == "All Out 80s")
        #expect(result.tracks.count == 100)
        #expect(result.tracks.contains { $0.artists == ["Earth, Wind & Fire"] })
        #expect(result.tracks.contains { $0.artists == ["Eurythmics", "Annie Lennox", "Dave Stewart"] })
    }

    // MARK: Item mapping

    @Test func skipsNonTracksUnplayableAndDuplicatesKeepingOrder() async throws {
        let items = [
            SourceFixtures.item(id: idA, title: "First"),
            SourceFixtures.item(id: idB, uriKind: "episode"),
            SourceFixtures.item(id: idC, title: "Unplayable", isPlayable: false),
            SourceFixtures.item(id: idB, title: "Second"),
            SourceFixtures.item(id: idA, title: "First again"),
            #"{"uri":"spotify:local:::Some+Local+File:180","title":"Local","subtitle":"Me","duration":1,"isPlayable":true}"#,
            #"{"uri":"spotify:track:not-a-valid-id","title":"Bad","subtitle":"Me","duration":1}"#,
        ].joined(separator: ",")
        let result = try await listing(SourceFixtures.page(items: items))
        #expect(result.tracks.map(\.id) == [idA, idB])
        #expect(result.tracks.map(\.title) == ["First", "Second"])
    }

    @Test func splitsArtistsOnlyOnCommaNBSP() async throws {
        // Real subtitle from the All Out 2010s embed.
        let items = SourceFixtures.item(id: idA, subtitle: "Tyler, The Creator,\u{00A0}Kali Uchis")
        let result = try await listing(SourceFixtures.page(items: items))
        #expect(result.tracks.first?.artists == ["Tyler, The Creator", "Kali Uchis"])
        #expect(EmbedTrackSource.splitArtists(" A ,\u{00A0} B\u{00A0},\u{00A0}") == ["A", "B"])
    }

    @Test func mapsMissingPreviewToNilAndMissingPlayableFlagToPlayable() async throws {
        let items = [
            SourceFixtures.item(id: idA, preview: nil),
            #"{"uri":"spotify:track:\#(idB)","title":"No flag","subtitle":"X","duration":123}"#,
        ].joined(separator: ",")
        let result = try await listing(SourceFixtures.page(items: items))
        #expect(result.tracks.map(\.id) == [idA, idB])
        #expect(result.tracks[0].previewURL == nil)
        #expect(result.tracks[1].durationMs == 123)
    }

    @Test func fallsBackToTitleForListingName() async throws {
        let html = #"<script id="__NEXT_DATA__" type="application/json">{"props":{"pageProps":{"state":{"data":{"entity":{"title":"Titled","trackList":[\#(SourceFixtures.item(id: idA))]}}}}}}</script>"#
        #expect(try await listing(Data(html.utf8)).name == "Titled")
    }

    @Test func findsTrackListOutsideTheKnownPath() async throws {
        let html = #"<script type="application/json" id="__NEXT_DATA__">{"props":{"moved":[{"entity":{"name":"Moved","trackList":[\#(SourceFixtures.item(id: idA))]}}]}}</script>"#
        let result = try await listing(Data(html.utf8))
        #expect(result.name == "Moved")
        #expect(result.tracks.map(\.id) == [idA])
    }

    // MARK: Errors

    @Test func notFoundOn404() async {
        await expectError(.notFound, .status(404, Data()))
    }

    @Test(arguments: [301, 403, 429, 500, 503])
    func networkErrorOnOtherStatus(code: Int) async {
        await expectError(.network("HTTP \(code) from https://open.spotify.com/embed/playlist/37i9dQZF1DXcBWIGoYBM5M"), .status(code, Data()))
    }

    @Test func networkErrorWhenTransportThrows() async {
        struct Offline: Error, Sendable, CustomStringConvertible { var description: String { "offline" } }
        await expectError(.network("offline"), .fail(Offline()))
    }

    @Test func cancellationPassesThrough() async {
        await #expect(throws: CancellationError.self) {
            try await EmbedTrackSource(http: SourceFakeHTTPClient(.fail(CancellationError()))).listing(for: ref)
        }
    }

    @Test func parseFailedWithoutNextData() async {
        await expectError(.parseFailed("no <script id=\"__NEXT_DATA__\"> block in the embed page"),
                          .ok(Data("<html><body>captcha</body></html>".utf8)))
    }

    @Test func parseFailedOnInvalidJSON() async {
        await expectError(.parseFailed("__NEXT_DATA__ is not valid JSON"),
                          .ok(Data(#"<script id="__NEXT_DATA__" type="application/json">{not json</script>"#.utf8)))
    }

    @Test func parseFailedWithoutTrackList() async {
        await expectError(.parseFailed("no entity with a trackList array in __NEXT_DATA__"),
                          .ok(Data(#"<script id="__NEXT_DATA__" type="application/json">{"props":{"pageProps":{"state":{"data":{"entity":{"name":"X","trackList":"nope"}}}}}}</script>"#.utf8)))
    }

    @Test func parseFailedWithoutName() async {
        await expectError(.parseFailed("entity has no name or title"),
                          .ok(SourceFixtures.page(name: nil, items: SourceFixtures.item(id: idA))))
    }

    @Test func parseFailedWhenEveryItemIsUnreadable() async {
        await expectError(.parseFailed("no trackList item had a readable uri, title and subtitle"),
                          .ok(SourceFixtures.page(items: #"{"name":"x"},{"uri":"spotify:track:\#(idA)"},3"#)))
    }

    @Test func emptyWhenTrackListIsEmpty() async {
        await expectError(.empty, .ok(SourceFixtures.page(items: "")))
    }

    @Test func emptyWhenNothingIsPlayable() async {
        let items = [
            SourceFixtures.item(id: idA, isPlayable: false),
            SourceFixtures.item(id: idB, uriKind: "episode"),
        ].joined(separator: ",")
        await expectError(.empty, .ok(SourceFixtures.page(items: items)))
    }
}
