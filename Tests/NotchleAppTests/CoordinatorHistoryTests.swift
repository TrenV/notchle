import Foundation
import Testing
import NotchleCore
@testable import Notchle

actor OEmbedFake: HTTPClient {
    private(set) var requests: [URL] = []
    let fail: Bool
    init(fail: Bool = false) { self.fail = fail }
    func get(_ url: URL, headers: [String: String]) async throws -> (status: Int, body: Data) {
        requests.append(url)
        if fail { throw URLError(.notConnectedToInternet) }
        return (200, Data(#"{"thumbnail_url":"https://image-cdn-fa.spotifycdn.com/image/cover1"}"#.utf8))
    }
}

@MainActor
private func makeCoordinator(http: OEmbedFake) -> (AppCoordinator, RecordingPlayer, URL) {
    let player = RecordingPlayer()
    let dir = FileManager.default.temporaryDirectory.appendingPathComponent("notchle-coord-\(UUID().uuidString)")
    let c = AppCoordinator(source: OneTrackSource(), store: ProgressStore(directory: dir),
                           artwork: ArtworkResolver(http: http), makePlayer: { _ in player })
    return (c, player, dir)
}

@MainActor
private func waitForPlaying(_ c: AppCoordinator) async throws {
    let deadline = ContinuousClock.now.advanced(by: .seconds(5))
    while c.model.state.phase != .playingSnippet(tierIndex: 0), ContinuousClock.now < deadline {
        try await Task.sleep(for: .milliseconds(5))
    }
    #expect(c.model.state.phase == .playingSnippet(tierIndex: 0))
}

@MainActor
@Test func outcomeIsRecordedWithListingPersistedAndCovered() async throws {
    let http = OEmbedFake()
    let (c, _, dir) = makeCoordinator(http: http)
    defer { try? FileManager.default.removeItem(at: dir) }
    let ref = SourceRef(kind: .playlist, id: "p")
    c.send(.load(ref))
    try await waitForPlaying(c)
    #expect(await http.requests.isEmpty, "no cover lookup while the track is being guessed")
    #expect(c.model.currentArtworkURL == nil)

    let before = Date()
    c.send(.submit(Guess(title: "nope", artist: "y")))
    c.send(.retry)
    c.send(.submit(Guess(title: "x", artist: "y")))           // right, at tier 1
    #expect(c.model.history.count == 1)
    let e = try #require(c.model.history.first)
    #expect(e.trackID == "t1" && e.correct && e.tierIndex == 1 && e.wrongGuesses == 1 && e.skips == 0)
    #expect(e.listingName == "one" && e.listingRef == ref)
    #expect(e.date >= before && e.date <= Date())
    #expect(e.artworkURL == nil, "recording does not wait for the cover")

    await c.drainArtwork()
    let cover = URL(string: "https://image-cdn-fa.spotifycdn.com/image/cover1")
    #expect(c.model.currentArtworkURL == cover)
    #expect(c.model.history.first?.artworkURL == cover)
    #expect(HistoryStore(directory: dir).load().first?.artworkURL == cover)
    #expect(await http.requests.map(\.absoluteString) == ["https://open.spotify.com/oembed?url=https://open.spotify.com/track/t1"])

    c.send(.next)                                               // set over: cover leaves the screen
    #expect(c.model.currentArtworkURL == nil)
    c.send(.reset)
    await c.drainPlayback()
}

@MainActor
@Test func historySurvivesResetProgressAndRelaunchButNotClear() async throws {
    let http = OEmbedFake(fail: true)
    let (c, _, dir) = makeCoordinator(http: http)
    defer { try? FileManager.default.removeItem(at: dir) }
    c.send(.load(SourceRef(kind: .playlist, id: "p")))
    try await waitForPlaying(c)
    c.send(.giveUp)
    await c.drainArtwork()
    #expect(c.model.history.map(\.correct) == [false])
    #expect(c.model.history.first?.artworkURL == nil, "offline: no cover, entry still recorded")
    #expect(c.model.currentArtworkURL == nil)

    c.resetProgress()
    #expect(c.model.history.count == 1)
    let relaunched = AppCoordinator(source: OneTrackSource(), store: ProgressStore(directory: dir),
                                    artwork: ArtworkResolver(http: http), makePlayer: { _ in RecordingPlayer() })
    #expect(relaunched.model.history.count == 1)

    c.model.clearHistory()
    #expect(c.model.history.isEmpty)
    #expect(HistoryStore(directory: dir).load().isEmpty)
    await c.drainPlayback()
}
