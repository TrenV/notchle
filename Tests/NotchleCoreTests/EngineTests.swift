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

/// `effects` without `.recordOutcome` (the history tests check that one on its own).
private func playback(_ effects: [GameEffect]) -> [GameEffect] {
    effects.filter { if case .recordOutcome = $0 { return false } else { return true } }
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
            #expect(playback(e.send(right(track))) == [.continuePlaying])
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
        #expect(playSet(&e, missing: [7]) == [.stop, .persistProgress])
        #expect(e.state.phase == .setFailed(correctCount: 19))
        #expect(e.state.clearedTrackIDs == Set(set.map(\.id)).subtracting([set[7].id]))

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
        #expect(playback(e.send(wrongGuess)) == [.continuePlaying])
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
        #expect(playback(e.send(right(e.state.currentTrack))) == [.continuePlaying])
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
        #expect(playback(e.send(.giveUp)) == [.continuePlaying])
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
        #expect(playback(effects) == [.playSnippet(try #require(e.state.currentTrack), start: 0, seconds: 5)])
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
                                   .next, .nextSet, .replaySet, .startSet(.replay), .startSet(.keepMisses),
                                   .startSet(.allNew), .loadFailed(message: "x"),
                                   .playbackFailed(message: "x")] {
            let before = idle.state
            #expect(idle.send(action) == [])
            #expect(idle.state == before)
        }

        var playing = engine()
        for action: GameAction in [.retry, .next, .nextSet, .replaySet, .startSet(.allNew),
                                   .startSet(.replay), .startSet(.keepMisses), .loaded(listing(3)),
                                   .loadFailed(message: "x")] {
            let before = playing.state
            #expect(playing.send(action) == [])
            #expect(playing.state == before)
        }

        var correct = engine()
        _ = correct.send(right(correct.state.currentTrack))
        for action: GameAction in [right(correct.state.currentTrack), .retry, .giveUp, .nextSet, .replaySet,
                                   .startSet(.replay), .startSet(.keepMisses), .startSet(.allNew)] {
            let before = correct.state
            #expect(correct.send(action) == [])
            #expect(correct.state == before)
        }

        var complete = engine(tracks: 20)
        _ = playSet(&complete)
        for action: GameAction in [.next, .giveUp, .playbackFailed(message: "x")] {
            let before = complete.state
            #expect(complete.send(action) == [])
            #expect(complete.state == before)
        }
    }

    // MARK: Restart

    @Test func restartWhilePlayingReplaysTheSameTierFromItsStart() throws {
        var e = engine(config: GameConfig(tiers: [5, 10, 15], setSize: 20, snippetStart: 7))
        let track = try #require(e.state.currentTrack)
        let before = e.state
        #expect(e.send(.restart) == [.playSnippet(track, start: 7, seconds: 5)])
        #expect(e.state == before)                     // same phase, tier, results, index
        #expect(e.state.phase == .playingSnippet(tierIndex: 0))
    }

    @Test func restartWhileGuessingReplaysWithoutUsingAnAttempt() throws {
        var e = engine()
        let track = try #require(e.state.currentTrack)
        _ = e.send(wrongGuess)
        _ = e.send(.retry)
        _ = e.send(.snippetFinished)
        #expect(e.state.phase == .guessing(tierIndex: 1))
        let results = e.state.results

        #expect(e.send(.restart) == [.playSnippet(track, start: 0, seconds: 10)])
        #expect(e.state.phase == .playingSnippet(tierIndex: 1))
        #expect(e.state.results == results)
        #expect(e.state.index == 0)

        // The replayed snippet finishing is the one real snippetFinished...
        #expect(e.send(.snippetFinished) == [])
        #expect(e.state.phase == .guessing(tierIndex: 1))
        // ...and a stale one (e.g. from the snippet the restart cancelled) changes nothing.
        let guessing = e.state
        #expect(e.send(.snippetFinished) == [])
        #expect(e.state == guessing)

        // Still on the tier the restart replayed: a wrong guess offers 15s next, not a reveal.
        _ = e.send(wrongGuess)
        #expect(e.state.phase == .wrong(tierIndex: 1, verdict: wrongVerdict))
        #expect(e.send(.retry) == [.playSnippet(track, start: 0, seconds: 15)])
    }

    @Test func restartAtTheLastTierDoesNotRevealOrRecordAMiss() {
        var e = engine()
        _ = e.send(wrongGuess); _ = e.send(.retry)
        _ = e.send(wrongGuess); _ = e.send(.retry)
        _ = e.send(.snippetFinished)
        #expect(e.state.phase == .guessing(tierIndex: 2))
        for _ in 0..<3 { _ = e.send(.restart); _ = e.send(.snippetFinished) }
        #expect(e.state.phase == .guessing(tierIndex: 2))
        #expect(e.state.results.isEmpty)
        // The last wrong verdict survives the replays.
        _ = e.send(.giveUp)
        #expect(e.state.phase == .revealed(verdict: wrongVerdict))
    }

    @Test func restartAfterCorrectOrRevealedRestartsTheWholeSong() throws {
        var e = engine()
        let first = try #require(e.state.currentTrack)
        _ = e.send(right(first))
        let correct = e.state
        #expect(e.send(.restart) == [.restartTrack(first)])
        #expect(e.state == correct)
        #expect(e.send(.snippetFinished) == [])        // stale: nothing moves
        #expect(e.state == correct)

        _ = e.send(.next)
        let second = try #require(e.state.currentTrack)
        _ = e.send(.giveUp)
        let revealed = e.state
        #expect(e.send(.restart) == [.restartTrack(second)])
        #expect(e.state == revealed)
        #expect(e.state.results == [.correct(tierIndex: 0), .missed])
    }

    @Test func restartInWrongReplaysTheSnippetButStaysWrong() throws {
        var wrong = engine()
        _ = wrong.send(wrongGuess)
        let wrongState = wrong.state
        let track = try #require(wrong.state.currentTrack)
        #expect(wrong.send(.restart) == [.playSnippet(track, start: 0, seconds: 5)])
        #expect(wrong.state == wrongState)          // still wrong: Retry or Give up decide
        #expect(wrong.send(.snippetFinished) == [])  // the replay's finish changes nothing
        #expect(wrong.state == wrongState)
    }

    @Test func restartIsIgnoredInEveryOtherPhase() {

        var idle = GameEngine(judge: IDJudge(), seed: 1)
        let idleState = idle.state
        #expect(idle.send(.restart) == [])
        #expect(idle.state == idleState)

        var loading = GameEngine(judge: IDJudge(), seed: 1)
        _ = loading.send(.load(ref))
        let loadingState = loading.state
        #expect(loading.send(.restart) == [])
        #expect(loading.state == loadingState)

        var failed = engine()
        _ = failed.send(.playbackFailed(message: "x"))
        let errorState = failed.state
        #expect(failed.send(.restart) == [])
        #expect(failed.state == errorState)

        var complete = engine(tracks: 20)
        _ = playSet(&complete)
        let completeState = complete.state
        #expect(complete.send(.restart) == [])
        #expect(complete.state == completeState)

        var setFailed = engine(tracks: 20)
        _ = playSet(&setFailed, missing: [0])
        let setFailedState = setFailed.state
        #expect(setFailed.send(.restart) == [])
        #expect(setFailed.state == setFailedState)

        let exhaustedAll = Set(listing(3).tracks.map(\.id))
        var exhausted = engine(tracks: 3, cleared: exhaustedAll)
        let exhaustedState = exhausted.state
        #expect(exhausted.send(.restart) == [])
        #expect(exhausted.state == exhaustedState)
    }

    // MARK: Skip

    @Test func skipWhilePlayingForfeitsTheTierForTheNextOne() throws {
        var e = engine(config: GameConfig(tiers: [5, 10, 15], setSize: 20, snippetStart: 3))
        let track = try #require(e.state.currentTrack)
        var expected = e.state
        #expect(e.send(.skip) == [.playSnippet(track, start: 3, seconds: 10)])
        expected.phase = .playingSnippet(tierIndex: 1)
        #expect(e.state == expected)                   // only the tier moved: no result, same track
    }

    @Test func skipWhileGuessingPlaysTheNextTier() throws {
        var e = engine()
        let track = try #require(e.state.currentTrack)
        _ = e.send(.skip)
        _ = e.send(.snippetFinished)
        #expect(e.state.phase == .guessing(tierIndex: 1))
        #expect(e.send(.skip) == [.playSnippet(track, start: 0, seconds: 15)])
        #expect(e.state.phase == .playingSnippet(tierIndex: 2))
        #expect(e.state.results.isEmpty)
    }

    @Test func skipAtTheLastTierIsExactlyGiveUp() {
        for finished in [false, true] {
            for wrongFirst in [false, true] {
                var e = engine()
                if wrongFirst { _ = e.send(wrongGuess); _ = e.send(.retry) } else { _ = e.send(.skip) }
                _ = e.send(.skip)
                if finished { _ = e.send(.snippetFinished) }
                #expect(e.state.phase == (finished ? .guessing(tierIndex: 2) : .playingSnippet(tierIndex: 2)))

                var skipped = e, gaveUp = e
                let skipEffects = skipped.send(.skip)
                #expect(skipEffects == gaveUp.send(.giveUp))
                #expect(skipped.state == gaveUp.state)
                #expect(playback(skipEffects) == [.continuePlaying])
                #expect(skipped.state.phase == .revealed(verdict: wrongFirst ? wrongVerdict : nil))
                #expect(skipped.state.results == [.missed])
            }
        }
    }

    @Test func skipSkipThenCorrectAtFifteenSeconds() throws {
        var e = engine()
        let track = try #require(e.state.currentTrack)
        #expect(e.send(.skip) == [.playSnippet(track, start: 0, seconds: 10)])
        _ = e.send(.snippetFinished)
        #expect(e.send(.skip) == [.playSnippet(track, start: 0, seconds: 15)])
        #expect(playback(e.send(right(track))) == [.continuePlaying])
        #expect(e.state.phase == .correct(tierIndex: 2))
        #expect(e.state.results == [.correct(tierIndex: 2)])
    }

    @Test func staleSnippetFinishedAfterASkipIsIgnored() {
        var e = engine()
        _ = e.send(.snippetFinished)                   // tier 0 done
        _ = e.send(.skip)                              // tier 1 playing
        _ = e.send(.snippetFinished)                   // tier 1 done
        let guessing = e.state
        #expect(guessing.phase == .guessing(tierIndex: 1))
        #expect(e.send(.snippetFinished) == [])        // a late one (the skipped tier's) moves nothing
        #expect(e.state == guessing)
    }

    @Test func skipIsIgnoredOutsideTheGuessPhases() {
        var wrong = engine()
        _ = wrong.send(wrongGuess)
        let wrongState = wrong.state
        #expect(wrong.send(.skip) == [])
        #expect(wrong.state == wrongState)

        var correct = engine()
        _ = correct.send(right(correct.state.currentTrack))
        let correctState = correct.state
        #expect(correct.send(.skip) == [])
        #expect(correct.state == correctState)

        var revealed = engine()
        _ = revealed.send(.giveUp)
        let revealedState = revealed.state
        #expect(revealed.send(.skip) == [])
        #expect(revealed.state == revealedState)

        var idle = GameEngine(judge: IDJudge(), seed: 1)
        #expect(idle.send(.skip) == [])
        #expect(idle.state.phase == .idle)

        var loading = GameEngine(judge: IDJudge(), seed: 1)
        _ = loading.send(.load(ref))
        #expect(loading.send(.skip) == [])
        #expect(loading.state.phase == .loading)

        var failed = engine()
        _ = failed.send(.playbackFailed(message: "x"))
        let errorState = failed.state
        #expect(failed.send(.skip) == [])
        #expect(failed.state == errorState)

        var complete = engine(tracks: 20)
        _ = playSet(&complete)
        let completeState = complete.state
        #expect(complete.send(.skip) == [])
        #expect(complete.state == completeState)
    }

    // MARK: End-of-set choices

    @Test func replayFromBothEndsKeepsTracksAndSetNumber() {
        for missing: Set<Int> in [[], [2, 9]] {
            var e = engine(tracks: 50)
            let ids = Set(e.state.currentSet.map(\.id)), order = e.state.currentSet.map(\.id)
            _ = playSet(&e, missing: missing)
            let effects = e.send(.startSet(.replay))
            #expect(e.state.phase == .playingSnippet(tierIndex: 0))
            #expect(Set(e.state.currentSet.map(\.id)) == ids)
            #expect(e.state.currentSet.map(\.id) != order)   // reshuffled (seed 42)
            #expect(e.state.results.isEmpty && e.state.index == 0)
            #expect(e.state.setNumber == 1)
            #expect(effects == [.playSnippet(e.state.currentSet[0], start: 0, seconds: 5)])
        }
    }

    @Test func keepMissesCarriesTheMissesAndFillsWithNewTracks() {
        var e = engine(tracks: 50)
        let first = e.state.currentSet
        _ = playSet(&e, missing: [0, 5, 13])
        let misses = Set([0, 5, 13].map { first[$0].id })
        _ = e.send(.startSet(.keepMisses))
        let ids = e.state.currentSet.map(\.id)
        #expect(ids.count == 20)
        #expect(Set(ids).count == 20)                                // no duplicates
        #expect(misses.isSubset(of: Set(ids)))                        // the misses are carried
        #expect(Set(ids).subtracting(misses).isDisjoint(with: Set(first.map(\.id))))  // the rest is new
        #expect(Set(ids).isDisjoint(with: e.state.clearedTrackIDs))
        #expect(e.state.setNumber == 2)
        #expect(e.state.results.isEmpty && e.state.phase == .playingSnippet(tierIndex: 0))
        // Shuffled together, not misses-first.
        #expect(Set(ids.prefix(3)) != misses)
    }

    @Test func keepMissesWithFewNewTracksLeftGivesASmallerSet() {
        var e = engine(tracks: 24)   // 4 new tracks after the first set
        _ = playSet(&e, missing: [1, 2])
        #expect(GameEngine.availableNewCount(e.state) == 4)
        _ = e.send(.startSet(.keepMisses))
        #expect(e.state.currentSet.count == 6)
        #expect(Set(e.state.currentSet.map(\.id)).count == 6)
    }

    @Test func keepMissesWithNoNewTracksPlaysOnlyTheMisses() {
        var e = engine(tracks: 20)
        let first = e.state.currentSet
        _ = playSet(&e, missing: [4])
        _ = e.send(.startSet(.keepMisses))
        #expect(e.state.currentSet.map(\.id) == [first[4].id])
        #expect(e.state.phase == .playingSnippet(tierIndex: 0))
    }

    @Test func keepMissesAfterAPerfectSetIsAllNew() {
        var a = engine(tracks: 50), b = engine(tracks: 50)
        _ = playSet(&a); _ = playSet(&b)
        let ea = a.send(.startSet(.keepMisses)), eb = b.send(.startSet(.allNew))
        #expect(ea == eb)
        #expect(a.state == b.state)
        #expect(a.state.setNumber == 2)
    }

    @Test func allNewFromAFailedSetSkipsTheWholeJustPlayedSet() {
        var e = engine(tracks: 50)
        let first = Set(e.state.currentSet.map(\.id))
        _ = playSet(&e, missing: [3, 4])
        #expect(GameEngine.availableNewCount(e.state) == 20)   // 30 new, capped at setSize
        _ = e.send(.startSet(.allNew))
        #expect(e.state.currentSet.count == 20)
        #expect(Set(e.state.currentSet.map(\.id)).count == 20)
        #expect(Set(e.state.currentSet.map(\.id)).isDisjoint(with: first))
        #expect(e.state.setNumber == 2)
    }

    @Test func allNewWithFewLeftIsSmallerAndWithNoneIsExhausted() {
        var e = engine(tracks: 27)
        _ = playSet(&e, missing: [0])
        _ = e.send(.startSet(.allNew))
        #expect(e.state.currentSet.count == 7)
        _ = playSet(&e, missing: [0])
        // Misses are never cleared: set 1's miss is outside the set just played, so it is new again.
        #expect(GameEngine.availableNewCount(e.state) == 1)
        _ = e.send(.startSet(.allNew))
        #expect(e.state.currentSet.count == 1)
        _ = playSet(&e)
        #expect(GameEngine.availableNewCount(e.state) == 1)   // set 2's miss
        _ = e.send(.startSet(.allNew)); _ = playSet(&e)
        #expect(GameEngine.availableNewCount(e.state) == 0)
        #expect(e.send(.startSet(.allNew)) == [])
        #expect(e.state.phase == .exhausted)
        #expect(e.state.clearedTrackIDs.count == 27)
    }

    @Test func failedSetsGrowTheClearedSetAndPersist() {
        var e = engine(tracks: 50)
        let first = e.state.currentSet
        #expect(playSet(&e, missing: Set(0..<10)) == [.stop, .persistProgress])
        #expect(e.state.clearedTrackIDs == Set(first[10...].map(\.id)))
        _ = e.send(.startSet(.replay))
        let replayed = e.state.currentSet
        _ = playSet(&e, missing: [0])
        #expect(e.state.clearedTrackIDs == Set(first.map(\.id)).subtracting([replayed[0].id]))
    }

    @Test func setChoicesAreDeterministicBySeed() {
        for choice in SetChoice.allCases {
            var a = engine(tracks: 60, seed: 7), b = engine(tracks: 60, seed: 7), c = engine(tracks: 60, seed: 8)
            _ = playSet(&a, missing: [1, 2]); _ = playSet(&b, missing: [1, 2]); _ = playSet(&c, missing: [1, 2])
            _ = a.send(.startSet(choice)); _ = b.send(.startSet(choice)); _ = c.send(.startSet(choice))
            #expect(a.state.currentSet == b.state.currentSet, "\(choice)")
            #expect(a.state.currentSet != c.state.currentSet, "\(choice)")
        }
    }

    @Test func aliasesMatchStartSet() {
        for missing: Set<Int> in [[], [3]] {
            var a = engine(tracks: 50), b = engine(tracks: 50)
            _ = playSet(&a, missing: missing); _ = playSet(&b, missing: missing)
            var a2 = a, b2 = b
            #expect(a.send(.nextSet) == b.send(.startSet(.allNew)))
            #expect(a.state == b.state)
            #expect(a2.send(.replaySet) == b2.send(.startSet(.replay)))
            #expect(a2.state == b2.state)
        }
    }

    @Test func availableNewCountIgnoresClearedAndCurrentAndDuplicates() {
        var tracks = listing(30).tracks
        tracks.append(tracks[25])   // duplicate listing entry
        var e = GameEngine(clearedTrackIDs: ["t29"], judge: IDJudge(), seed: 1)
        _ = e.send(.load(ref))
        _ = e.send(.loaded(SourceListing(ref: ref, name: "D", tracks: tracks)))
        // 30 unique, 1 cleared, 20 in the set → 9.
        #expect(GameEngine.availableNewCount(e.state) == 9)
    }
}
