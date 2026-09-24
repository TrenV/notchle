import Foundation
import Testing
@testable import NotchleCore

/// Judge stub: a guess is right when its title equals the track id and its artist is "a".
/// Lets engine tests say `right(track)` / `wrong` without depending on the fuzzy judge.
private struct IDJudge: AnswerJudging {
    func judge(_ guess: Guess, against track: Track) -> Verdict {
        Verdict(titleCorrect: guess.title == track.id, artistCorrect: guess.artist == "a")
    }
}

private let ref = SourceRef(kind: .playlist, id: "pl")

private func listing(_ count: Int) -> SourceListing {
    SourceListing(ref: ref, name: "Test", tracks: (0..<count).map { i in
        Track(id: "t\(i)", uri: "spotify:track:t\(i)", title: "Song \(i)", artists: ["a"], durationMs: 200_000, previewURL: nil)
    })
}

private func right(_ track: Track?) -> GameAction { .submit(Guess(title: track?.id ?? "", artist: "a")) }
private let wrongGuess = GameAction.submit(Guess(title: "nope", artist: "a"))
private let wrongVerdict = Verdict(titleCorrect: false, artistCorrect: true)

private func engine(tracks: Int = 25, seed: UInt64 = 42, config: GameConfig = GameConfig(),
                    cleared: Set<String> = []) -> GameEngine {
    var e = GameEngine(config: config, clearedTrackIDs: cleared, judge: IDJudge(), seed: seed)
    _ = e.send(.load(ref))
    _ = e.send(.loaded(listing(tracks)))
    return e
}

/// Plays the current set to the end, answering right except at the given indices.
private func playSet(_ e: inout GameEngine, missing: Set<Int> = []) -> [GameEffect] {
    var last: [GameEffect] = []
    for i in 0..<e.state.currentSet.count {
        #expect(e.state.index == i)
        _ = e.send(missing.contains(i) ? .giveUp : right(e.state.currentTrack))
        last = e.send(.next)
    }
    return last
}

@Suite struct EngineTests {
    @Test func loadStopsAndFetches() {
        var e = GameEngine(judge: IDJudge(), seed: 1)
        #expect(e.send(.load(ref)) == [.stop, .fetch(ref)])
        #expect(e.state.phase == .loading)
    }

    @Test func loadedStartsSetOneAtTierZero() throws {
        var e = GameEngine(config: GameConfig(tiers: [5, 10, 15], setSize: 20, snippetStart: 7),
                           judge: IDJudge(), seed: 1)
        _ = e.send(.load(ref))
        let effects = e.send(.loaded(listing(30)))
        let first = try #require(e.state.currentTrack)
        #expect(effects == [.playSnippet(first, start: 7, seconds: 5)])
        #expect(e.state.phase == .playingSnippet(tierIndex: 0))
        #expect(e.state.setNumber == 1)
        #expect(e.state.currentSet.count == 20)
        #expect(Set(e.state.currentSet.map(\.id)).count == 20)
    }

    @Test func loadFailedGoesToErrorAndNextIsIgnored() {
        var e = GameEngine(judge: IDJudge(), seed: 1)
        _ = e.send(.load(ref))
        #expect(e.send(.loadFailed(message: "boom")) == [])
        #expect(e.state.phase == .error(message: "boom"))
        #expect(e.send(.next) == [])
        #expect(e.state.phase == .error(message: "boom"))
        #expect(e.send(.reset) == [.stop])
        #expect(e.state.phase == .idle)
    }

    @Test func fullTwentyOutOfTwentyCompletesAndPersists() {
        var e = engine(tracks: 25)
        let set = e.state.currentSet
        let celebrations = e.state.celebrationCount
        var lastEffects: [GameEffect] = []
        for i in 0..<20 {
            let track = e.state.currentTrack
            #expect(e.send(right(track)) == [.continuePlaying])
            #expect(e.state.phase == .correct(tierIndex: 0))
            lastEffects = e.send(.next)
            if i < 19 {
                #expect(lastEffects == [.playSnippet(set[i + 1], start: 0, seconds: 5)])
            }
        }
        #expect(lastEffects == [.stop, .persistProgress])
        #expect(e.state.phase == .setComplete(correctCount: 20))
        #expect(e.state.results == Array(repeating: .correct(tierIndex: 0), count: 20))
        #expect(e.state.celebrationCount == celebrations + 20)
        #expect(e.state.clearedTrackIDs == Set(set.map(\.id)))
        #expect(e.state.currentTrack == nil)
    }

    @Test func oneMissFailsTheSetAndReplayReshufflesSameTracks() throws {
        var e = engine(tracks: 25)
        let set = e.state.currentSet
        #expect(playSet(&e, missing: [7]) == [.stop])
        #expect(e.state.phase == .setFailed(correctCount: 19))
        #expect(e.state.clearedTrackIDs.isEmpty)

        let effects = e.send(.replaySet)
        #expect(e.state.phase == .playingSnippet(tierIndex: 0))
        #expect(e.state.results.isEmpty)
        #expect(e.state.index == 0)
        #expect(e.state.setNumber == 1)
        #expect(Set(e.state.currentSet.map(\.id)) == Set(set.map(\.id)))
        #expect(e.state.currentSet.map(\.id) != set.map(\.id))  // reshuffled (seed 42)
        #expect(effects == [.playSnippet(try #require(e.state.currentTrack), start: 0, seconds: 5)])
    }

    @Test func tierEscalationFiveTenFifteenThenReveal() throws {
        var e = engine()
        let track = try #require(e.state.currentTrack)
        _ = e.send(.snippetFinished)
        #expect(e.send(wrongGuess) == [])  // snippet already finished: nothing to stop
        #expect(e.state.phase == .wrong(tierIndex: 0, verdict: wrongVerdict))
        #expect(e.send(.retry) == [.playSnippet(track, start: 0, seconds: 10)])
        #expect(e.state.phase == .playingSnippet(tierIndex: 1))
        _ = e.send(.snippetFinished)
        _ = e.send(wrongGuess)
        #expect(e.send(.retry) == [.playSnippet(track, start: 0, seconds: 15)])
        _ = e.send(.snippetFinished)
        #expect(e.state.phase == .guessing(tierIndex: 2))
        #expect(e.send(wrongGuess) == [.continuePlaying])
        #expect(e.state.phase == .revealed(verdict: wrongVerdict))
        #expect(e.state.results == [.missed])
    }

    @Test func correctAtLaterTierRecordsTier() {
        var e = engine()
        _ = e.send(wrongGuess)
        _ = e.send(.retry)
        _ = e.send(right(e.state.currentTrack))
        #expect(e.state.phase == .correct(tierIndex: 1))
        #expect(e.state.results == [.correct(tierIndex: 1)])
    }

    @Test func submitMidSnippetStopsOnWrongAndContinuesOnRight() {
        var e = engine()
        #expect(e.send(wrongGuess) == [.stop])
        #expect(e.state.phase == .wrong(tierIndex: 0, verdict: wrongVerdict))
        _ = e.send(.retry)
        #expect(e.send(right(e.state.currentTrack)) == [.continuePlaying])
    }

    @Test func blankGuessIsIgnoredButHalfBlankIsJudged() {
        var e = engine()
        let before = e.state
        #expect(e.send(.submit(Guess(title: "  ", artist: "\n\t"))) == [])
        #expect(e.state == before)
        #expect(e.send(.submit(Guess(title: "", artist: "a"))) == [.stop])
        #expect(e.state.phase == .wrong(tierIndex: 0, verdict: Verdict(titleCorrect: false, artistCorrect: true)))
    }

    @Test func giveUpRevealsWithLastWrongVerdictOrNil() {
        var e = engine()
        #expect(e.send(.giveUp) == [.continuePlaying])
        #expect(e.state.phase == .revealed(verdict: nil))
        #expect(e.state.results == [.missed])

        _ = e.send(.next)
        _ = e.send(wrongGuess)
        _ = e.send(.retry)            // playingSnippet(1): verdict survives the retry
        _ = e.send(.giveUp)
        #expect(e.state.phase == .revealed(verdict: wrongVerdict))

        _ = e.send(.next)             // new track: previous verdict must not leak
        _ = e.send(.snippetFinished)
        _ = e.send(.giveUp)
        #expect(e.state.phase == .revealed(verdict: nil))
    }

    @Test func staleSnippetFinishedIsIgnored() {
        var e = engine()
        _ = e.send(wrongGuess)
        let wrongState = e.state
        #expect(e.send(.snippetFinished) == [])
        #expect(e.state == wrongState)

        _ = e.send(.retry)
        _ = e.send(right(e.state.currentTrack))
        let correctState = e.state
        #expect(e.send(.snippetFinished) == [])
        #expect(e.state == correctState)
    }

    @Test func noRepeatsAcrossSetsThenSmallerFinalSetThenExhausted() {
        var e = engine(tracks: 45)
        var seen: [String] = []
        for expectedSize in [20, 20, 5] {
            #expect(e.state.currentSet.count == expectedSize)
            seen += e.state.currentSet.map(\.id)
            #expect(playSet(&e) == [.stop, .persistProgress])
            #expect(e.send(.nextSet) == [] || e.state.phase == .playingSnippet(tierIndex: 0))
        }
        #expect(seen.count == 45)
        #expect(Set(seen).count == 45)
        #expect(e.state.phase == .exhausted)
        #expect(e.state.setNumber == 4)
        #expect(e.state.clearedTrackIDs.count == 45)
    }

    @Test func alreadyClearedListingIsExhaustedOnLoad() {
        let all = Set(listing(10).tracks.map(\.id))
        let e = engine(tracks: 10, cleared: all)
        #expect(e.state.phase == .exhausted)
        #expect(e.state.currentSet.isEmpty)
    }

    @Test func clearedTracksAreSkippedOnLoad() {
        let e = engine(tracks: 25, cleared: ["t0", "t1", "t2", "t3", "t4"])
        #expect(e.state.currentSet.count == 20)
        #expect(Set(e.state.currentSet.map(\.id)).isDisjoint(with: ["t0", "t1", "t2", "t3", "t4"]))
    }

    @Test func sameSeedSameOrderDifferentSeedDifferentOrder() {
        let a = engine(tracks: 50, seed: 7).state.currentSet.map(\.id)
        let b = engine(tracks: 50, seed: 7).state.currentSet.map(\.id)
        let c = engine(tracks: 50, seed: 8).state.currentSet.map(\.id)
        #expect(a == b)
        #expect(a != c)
        #expect(a != listing(50).tracks.prefix(20).map(\.id))  // actually shuffled
    }

    @Test func splitMix64MatchesReferenceOutput() {
        // Reference values for seed 0 from the published SplitMix64 algorithm.
        var rng = SplitMix64(seed: 0)
        #expect(rng.next() == 0xE220_A839_7B1D_CDAF)
        #expect(rng.next() == 0x6E78_9E6A_A1B9_65F4)
    }

    @Test func playbackFailedThenNextCountsAsMissed() throws {
        var e = engine()
        #expect(e.send(.playbackFailed(message: "no Spotify")) == [])
        #expect(e.state.phase == .error(message: "no Spotify"))
        let effects = e.send(.next)
        #expect(e.state.results == [.missed])
        #expect(e.state.index == 1)
        #expect(effects == [.playSnippet(try #require(e.state.currentTrack), start: 0, seconds: 5)])
    }

    @Test func playbackFailedAfterCorrectDoesNotDoubleCount() {
        var e = engine()
        _ = e.send(right(e.state.currentTrack))
        _ = e.send(.playbackFailed(message: "x"))
        _ = e.send(.next)
        #expect(e.state.results == [.correct(tierIndex: 0)])
    }

    @Test func configureTakesEffectAtNextSnippet() throws {
        var e = engine()
        #expect(e.send(.configure(GameConfig(tiers: [2, 4, 6], setSize: 20, snippetStart: 30))) == [])
        #expect(e.state.phase == .playingSnippet(tierIndex: 0))
        _ = e.send(wrongGuess)
        let track = try #require(e.state.currentTrack)
        #expect(e.send(.retry) == [.playSnippet(track, start: 30, seconds: 4)])
    }

    @Test func resetKeepsClearedTrackIDs() {
        var e = engine(tracks: 20)
        _ = playSet(&e)
        let cleared = e.state.clearedTrackIDs
        #expect(e.send(.reset) == [.stop])
        #expect(e.state.phase == .idle)
        #expect(e.state.listing == nil)
        #expect(e.state.currentSet.isEmpty)
        #expect(e.state.results.isEmpty)
        #expect(e.state.clearedTrackIDs == cleared)
        #expect(cleared.count == 20)
    }

    @Test func loadMidGameRestarts() {
        var e = engine()
        _ = e.send(right(e.state.currentTrack))
        #expect(e.send(.load(ref)) == [.stop, .fetch(ref)])
        #expect(e.state.phase == .loading)
        #expect(e.state.currentSet.isEmpty)
        #expect(e.state.results.isEmpty)
    }

    @Test func actionsOutOfPhaseAreNoOps() {
        var idle = GameEngine(judge: IDJudge(), seed: 1)
        for action: GameAction in [.loaded(listing(3)), .snippetFinished, right(nil), .retry, .giveUp,
                                   .next, .nextSet, .replaySet, .loadFailed(message: "x"),
                                   .playbackFailed(message: "x")] {
            let before = idle.state
            #expect(idle.send(action) == [])
            #expect(idle.state == before)
        }

        var playing = engine()
        for action: GameAction in [.retry, .next, .nextSet, .replaySet, .loaded(listing(3)),
                                   .loadFailed(message: "x")] {
            let before = playing.state
            #expect(playing.send(action) == [])
            #expect(playing.state == before)
        }

        var correct = engine()
        _ = correct.send(right(correct.state.currentTrack))
        for action: GameAction in [right(correct.state.currentTrack), .retry, .giveUp, .nextSet, .replaySet] {
            let before = correct.state
            #expect(correct.send(action) == [])
            #expect(correct.state == before)
        }

        var complete = engine(tracks: 20)
        _ = playSet(&complete)
        for action: GameAction in [.replaySet, .next, .giveUp, .playbackFailed(message: "x")] {
            let before = complete.state
            #expect(complete.send(action) == [])
            #expect(complete.state == before)
        }
    }
}
