import Foundation
import Testing
@testable import NotchleCore

private struct IDJudge: AnswerJudging {
    func judge(_ guess: Guess, against track: Track) -> Verdict {
        Verdict(titleCorrect: guess.title == track.id, artistCorrect: guess.artist == "a")
    }
}

private let ref = SourceRef(kind: .playlist, id: "pl")
private let wrongGuess = GameAction.submit(Guess(title: "nope", artist: "a"))

private func engine(tracks: Int = 3, setSize: Int = 3) -> GameEngine {
    var e = GameEngine(config: GameConfig(tiers: [5, 10, 15], setSize: setSize), judge: IDJudge(), seed: 7)
    _ = e.send(.load(ref))
    _ = e.send(.loaded(SourceListing(ref: ref, name: "Test", tracks: (0..<tracks).map {
        Track(id: "t\($0)", uri: "spotify:track:t\($0)", title: "Song \($0)", artists: ["a"], durationMs: 1, previewURL: nil)
    })))
    return e
}

private func right(_ e: GameEngine) -> GameAction { .submit(Guess(title: e.state.currentTrack?.id ?? "", artist: "a")) }

private struct Record: Equatable {
    let trackID: String, outcome: TrackOutcome, wrong: Int, skips: Int
}

private func records(_ effects: [GameEffect]) -> [Record] {
    effects.compactMap {
        if case let .recordOutcome(t, o, w, s) = $0 { return Record(trackID: t.id, outcome: o, wrong: w, skips: s) }
        return nil
    }
}

/// Sends every action and collects all `.recordOutcome`s.
private func run(_ e: inout GameEngine, _ actions: [GameAction]) -> [Record] {
    actions.flatMap { records(e.send($0)) }
}

@Suite struct EngineRecordOutcomeTests {
    @Test(arguments: [0, 1, 2])
    func correctAtEachTierRecordsOnce(_ tier: Int) {
        var e = engine()
        let id = e.state.currentTrack!.id
        var got = run(&e, Array(repeating: [wrongGuess, .retry], count: tier).flatMap { $0 })
        #expect(got.isEmpty)   // nothing before the outcome
        let effects = e.send(right(e))
        #expect(effects.first.map { if case .recordOutcome = $0 { true } else { false } } == true)
        got += records(effects)
        got += run(&e, [.restart, .next])   // later actions on the track: no second record
        #expect(got == [Record(trackID: id, outcome: .correct(tierIndex: tier), wrong: tier, skips: 0)])
    }

    @Test func threeWrongGuessesRecordAMissWithThree() {
        var e = engine()
        let id = e.state.currentTrack!.id
        let got = run(&e, [wrongGuess, .retry, wrongGuess, .retry, wrongGuess, .giveUp, .next])
        #expect(got == [Record(trackID: id, outcome: .missed, wrong: 3, skips: 0)])
    }

    @Test func giveUpFromEachGuessPhase() {
        for setup: [GameAction] in [[], [.snippetFinished], [wrongGuess]] {
            var e = engine()
            let id = e.state.currentTrack!.id
            let got = run(&e, setup + [.giveUp, .giveUp, .next])
            #expect(got == [Record(trackID: id, outcome: .missed, wrong: setup == [wrongGuess] ? 1 : 0, skips: 0)])
        }
    }

    @Test func skipsCountAndTheLastTierSkipIsAGiveUp() {
        var e = engine()
        let id = e.state.currentTrack!.id
        #expect(run(&e, [.skip, .skip]).isEmpty)
        #expect(e.state.phase == .playingSnippet(tierIndex: 2))
        let got = run(&e, [.skip, .next])
        #expect(got == [Record(trackID: id, outcome: .missed, wrong: 0, skips: 2)])
    }

    @Test func skipThenCorrect() {
        var e = engine()
        let id = e.state.currentTrack!.id
        let got = run(&e, [.skip, .snippetFinished, wrongGuess, .retry, right(e)])
        #expect(got == [Record(trackID: id, outcome: .correct(tierIndex: 2), wrong: 1, skips: 1)])
    }

    @Test func nextFromAnErrorRecordsAMissOnce() {
        var e = engine()
        let id = e.state.currentTrack!.id
        _ = e.send(wrongGuess)
        #expect(run(&e, [.playbackFailed(message: "x")]).isEmpty)
        let effects = e.send(.next)
        #expect(records(effects) == [Record(trackID: id, outcome: .missed, wrong: 1, skips: 0)])
        #expect(records(e.send(.next)).isEmpty)   // on the next track, playing: ignored
    }

    @Test func errorAfterTheOutcomeDoesNotRecordAgain() {
        var e = engine()
        #expect(run(&e, [right(e)]).count == 1)
        #expect(run(&e, [.playbackFailed(message: "x"), .next]).isEmpty)
    }

    @Test func lastTrackRecordsBeforeTheSetEnds() {
        var e = engine(tracks: 1, setSize: 1)
        _ = e.send(.playbackFailed(message: "x"))
        let effects = e.send(.next)
        #expect(records(effects).count == 1)
        #expect(e.state.phase == .setFailed(correctCount: 0))
    }

    @Test func countersResetPerTrackAndOnReplaySet() {
        var e = engine(tracks: 2, setSize: 2)
        var got = run(&e, [.skip, wrongGuess, .giveUp, .next])       // track 1: missed, 1 skip, 1 wrong
        got += run(&e, [right(e), .next])                             // track 2: clean
        #expect(got.map(\.wrong) == [1, 0])
        #expect(got.map(\.skips) == [1, 0])
        #expect(e.state.phase == .setFailed(correctCount: 1))
        _ = e.send(.replaySet)
        let replay = run(&e, [right(e)])
        #expect(replay.map(\.wrong) == [0])
        #expect(replay.map(\.skips) == [0])
    }

    @Test func everyTrackOfASetRecordsExactlyOnce() {
        var e = engine(tracks: 5, setSize: 5)
        var got: [Record] = []
        for i in 0..<5 {
            got += run(&e, i % 2 == 0 ? [right(e), .next] : [.giveUp, .next])
        }
        #expect(got.count == 5)
        #expect(Set(got.map(\.trackID)).count == 5)
    }
}

private func entry(_ minutes: Double, _ correct: Bool, tier: Int? = 0, id: String = "x",
                   base: Date = Date(timeIntervalSince1970: 1_800_000_000)) -> HistoryEntry {
    HistoryEntry(date: base.addingTimeInterval(minutes * 60), trackID: id, title: "T", artists: ["A"],
                 listingName: "L", listingRef: ref, correct: correct, tierIndex: tier, wrongGuesses: 0, skips: 0)
}

@Suite struct HistoryStatsTests {
    @Test func empty() {
        let s = HistoryStats([])
        #expect(s.total == 0 && s.correct == 0 && s.accuracy == 0)
        #expect(s.averageTries == nil)
        #expect(s.perTier == [] && s.bestStreak == 0 && s.currentStreak == 0)
    }

    @Test func mathOverAMixedHistoryInAnyOrder() {
        // Date order: ✓0 ✓1 ✗ ✓2 ✓0 ✓0 ✗ ✓1
        let ordered = [entry(0, true, tier: 0), entry(1, true, tier: 1), entry(2, false, tier: nil),
                       entry(3, true, tier: 2), entry(4, true, tier: 0), entry(5, true, tier: 0),
                       entry(6, false, tier: nil), entry(7, true, tier: 1)]
        let s = HistoryStats(ordered.reversed())
        #expect(s.total == 8)
        #expect(s.correct == 6)
        #expect(s.accuracy == 0.75)
        #expect(s.averageTries == Double(1 + 2 + 3 + 1 + 1 + 2) / 6)
        #expect(s.perTier == [3, 2, 1])
        #expect(s.bestStreak == 3)
        #expect(s.currentStreak == 1)
    }

    @Test func missedEntriesNeverCarryATier() {
        #expect(entry(0, false, tier: 2).tierIndex == nil)
    }
}

@Suite struct HistorySpoilerFilterTests {
    func state(_ phase: GamePhase, set: [String]) -> GameState {
        var s = GameState()
        s.currentSet = set.map { Track(id: $0, uri: "", title: $0, artists: ["a"], durationMs: 1, previewURL: nil) }
        s.phase = phase
        return s
    }

    let history = [entry(0, true, id: "old"), entry(1, true, id: "cur1"), entry(2, false, tier: nil, id: "cur2"),
                   entry(3, true, id: "cur1")]

    @Test(arguments: [GamePhase.playingSnippet(tierIndex: 0), .guessing(tierIndex: 1),
                      .wrong(tierIndex: 0, verdict: Verdict(titleCorrect: false, artistCorrect: false)),
                      .correct(tierIndex: 0), .revealed(verdict: nil), .error(message: "e")])
    func currentSetIsHiddenWhileInProgress(_ phase: GamePhase) {
        let visible = HistorySpoilerFilter.visible(history, in: state(phase, set: ["cur1", "cur2"]))
        #expect(visible.map(\.trackID) == ["old"])
    }

    @Test(arguments: [GamePhase.setComplete(correctCount: 2), .setFailed(correctCount: 1), .idle, .loading, .exhausted])
    func everythingShowsOnceTheSetEndsOrThePlaylistIsQuit(_ phase: GamePhase) {
        let visible = HistorySpoilerFilter.visible(history, in: state(phase, set: ["cur1", "cur2"]))
        #expect(visible.map(\.trackID) == ["cur1", "cur2", "cur1", "old"])   // newest first
    }
}

private func tempDir() -> URL {
    FileManager.default.temporaryDirectory.appendingPathComponent("notchle-history-\(UUID().uuidString)", isDirectory: true)
}

@Suite struct HistoryStoreTests {
    @Test func missingFileIsEmptyAndRoundTripKeepsEveryField() throws {
        let dir = tempDir()
        defer { try? FileManager.default.removeItem(at: dir) }
        let store = HistoryStore(directory: dir.appendingPathComponent("a/Notchle"))
        #expect(store.load() == [])
        let a = HistoryEntry(date: Date(timeIntervalSince1970: 1_800_000_000), trackID: "t1", title: "Paper",
                             artists: ["X", "Y"], listingName: "Mix", listingRef: ref, correct: true, tierIndex: 1,
                             wrongGuesses: 1, skips: 0, artworkURL: URL(string: "https://i.scdn.co/image/abc"))
        let b = HistoryEntry(date: Date(timeIntervalSince1970: 1_800_000_100), trackID: "t2", title: "Glass",
                             artists: ["Z"], listingName: "Mix", listingRef: nil, correct: false, tierIndex: nil,
                             wrongGuesses: 3, skips: 2)
        try store.append(a)
        #expect(try store.append(b) == [a, b])
        #expect(HistoryStore(directory: store.fileURL.deletingLastPathComponent()).load() == [a, b])
        #expect(store.fileURL.lastPathComponent == "history.json")
        let text = try String(contentsOf: store.fileURL, encoding: .utf8)
        #expect(text.contains("\"artworkURL\""))
        #expect(text.contains("\"trackID\""))
    }

    @Test func corruptFileLoadsEmptyAndIsReplacedOnAppend() throws {
        let dir = tempDir()
        defer { try? FileManager.default.removeItem(at: dir) }
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        let store = HistoryStore(directory: dir)
        try Data("{not json".utf8).write(to: store.fileURL)
        #expect(store.load() == [])
        let e = entry(0, true)
        #expect(try store.append(e) == [e])
    }

    @Test func capDropsTheOldest() throws {
        let dir = tempDir()
        defer { try? FileManager.default.removeItem(at: dir) }
        let store = HistoryStore(directory: dir, cap: 3)
        let entries = (0..<5).map { entry(Double($0), true, id: "t\($0)") }
        for e in entries { try store.append(e) }
        #expect(store.load().map(\.trackID) == ["t2", "t3", "t4"])
        #expect(HistoryStore.defaultCap == 10_000)
        #expect(HistoryStore(directory: dir).cap == 10_000)
    }

    @Test func clearAndSetArtwork() throws {
        let dir = tempDir()
        defer { try? FileManager.default.removeItem(at: dir) }
        let store = HistoryStore(directory: dir)
        let e = entry(0, true)
        try store.append(e)
        let url = URL(string: "https://i.scdn.co/image/x")!
        #expect(try store.setArtworkURL(url, for: e.id).first?.artworkURL == url)
        #expect(store.load().first?.artworkURL == url)
        try store.clear()
        #expect(store.load() == [])
    }

    @Test func entriesWithoutArtworkKeyStillDecode() throws {
        let json = """
        [{"id":"00000000-0000-0000-0000-000000000001","date":"2026-09-24T12:00:00Z","trackID":"t","title":"T",
          "artists":["A"],"listingName":"L","correct":true,"tierIndex":0,"wrongGuesses":0,"skips":0}]
        """
        let dir = tempDir()
        defer { try? FileManager.default.removeItem(at: dir) }
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        let store = HistoryStore(directory: dir)
        try Data(json.utf8).write(to: store.fileURL)
        let loaded = store.load()
        #expect(loaded.count == 1)
        #expect(loaded.first?.artworkURL == nil && loaded.first?.listingRef == nil)
    }
}

actor ArtworkFakeHTTP: HTTPClient {
    let status: Int
    let body: Data
    private(set) var requests: [URL] = []
    init(status: Int = 200, body: Data) { self.status = status; self.body = body }
    func get(_ url: URL, headers: [String: String]) async throws -> (status: Int, body: Data) {
        requests.append(url)
        return (status, body)
    }
}

@Suite struct ArtworkResolverTests {
    static func fixture() throws -> Data {
        let url = try #require(Bundle.module.url(forResource: "oembed-track", withExtension: "json", subdirectory: "Fixtures"))
        return try Data(contentsOf: url)
    }

    @Test func parsesThumbnailFromTheSavedOEmbed() throws {
        #expect(ArtworkResolver.parseOEmbed(try Self.fixture())
                == URL(string: "https://image-cdn-fa.spotifycdn.com/image/ab67616d00001e0240e583b55bdddcf70516fa6c"))
    }

    @Test func rejectsBadBodies() {
        for body in ["", "[]", "{}", #"{"thumbnail_url":42}"#, #"{"thumbnail_url":"javascript:alert(1)"}"#] {
            #expect(ArtworkResolver.parseOEmbed(Data(body.utf8)) == nil, "\(body)")
        }
    }

    @Test func requestURLAndCache() async throws {
        let http = ArtworkFakeHTTP(body: try Self.fixture())
        let resolver = ArtworkResolver(http: http)
        let first = await resolver.artworkURL(forTrackID: "4uLU6hMCjMI75M1A2tKUQC")
        let second = await resolver.artworkURL(forTrackID: "4uLU6hMCjMI75M1A2tKUQC")
        #expect(first != nil && first == second)
        let requests = await http.requests
        #expect(requests.count == 1)
        #expect(requests.first?.absoluteString
                == "https://open.spotify.com/oembed?url=https://open.spotify.com/track/4uLU6hMCjMI75M1A2tKUQC")
    }

    @Test func failuresGiveNilAndAreNotCached() async throws {
        let http = ArtworkFakeHTTP(status: 404, body: Data())
        let resolver = ArtworkResolver(http: http)
        #expect(await resolver.artworkURL(forTrackID: "abc") == nil)
        #expect(await resolver.artworkURL(forTrackID: "abc") == nil)
        #expect(await http.requests.count == 2)
        #expect(await resolver.artworkURL(forTrackID: "../etc") == nil)
        #expect(await http.requests.count == 2)   // invalid id: no request at all
    }
}
