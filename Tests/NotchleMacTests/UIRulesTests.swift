import AppKit
import Testing
import NotchleCore
@testable import NotchleMac

@Suite struct UIRulesTests {
    static let allPhases: [GamePhase] = [
        .idle, .loading, .playingSnippet(tierIndex: 0), .guessing(tierIndex: 1),
        .wrong(tierIndex: 0, verdict: Verdict(titleCorrect: true, artistCorrect: false)),
        .correct(tierIndex: 0), .revealed(verdict: nil), .setComplete(correctCount: 20),
        .setFailed(correctCount: 3), .exhausted, .error(message: "x"),
    ]

    @Test func phasesAwaitingInput() {
        #expect(Self.allPhases.filter(NotchUIRules.needsInput).count == 9)
        #expect(!NotchUIRules.needsInput(.loading))
        #expect(!NotchUIRules.needsInput(.playingSnippet(tierIndex: 2)))
    }

    // MARK: Auto-close

    let t0 = Date(timeIntervalSinceReferenceDate: 1_000_000)

    @Test func typingNeedsFocusAndTextOrARecentKey() {
        func typing(_ f: UIField?, _ text: String, keyAgo: Double?) -> Bool {
            NotchUIRules.isTyping(focused: f, focusedText: text, lastKeyAt: keyAgo.map { t0.addingTimeInterval(-$0) }, now: t0)
        }
        #expect(!typing(nil, "Paper", keyAgo: 0.1))          // no focus: never typing
        #expect(typing(.title, "Paper", keyAgo: nil))          // text in the focused field
        #expect(typing(.url, "https://", keyAgo: 60))
        #expect(typing(.artist, "", keyAgo: 1.9))              // empty but a key just now
        #expect(!typing(.artist, "", keyAgo: 2.0))             // empty and idle for 2s
        #expect(!typing(.title, "   ", keyAgo: nil))           // whitespace is empty
        #expect(!typing(.title, "", keyAgo: nil))
    }

    @Test func collapseAfterGraceUnlessTyping() {
        func collapse(expanded: Bool = true, leftAgo: Double?, typing: Bool = false) -> Bool {
            NotchUIRules.shouldAutoCollapse(expanded: expanded, mouseLeftAt: leftAgo.map { t0.addingTimeInterval(-$0) },
                                            typing: typing, now: t0)
        }
        #expect(!collapse(leftAgo: nil))                       // mouse inside
        #expect(!collapse(leftAgo: 0.39))                      // within the grace period
        #expect(collapse(leftAgo: 0.4))
        #expect(collapse(leftAgo: 30))
        #expect(!collapse(leftAgo: 30, typing: true))          // typing keeps it open
        #expect(!collapse(expanded: false, leftAgo: 30))
        #expect(NotchUIRules.collapseGrace == 0.4)
        #expect(NotchUIRules.typingWindow == 2)
    }

    @Test func keysOnACollapsedPanel() {
        typealias B = NotchUIRules.CollapsedKeyBehavior
        func k(_ key: UIKeyInput?, _ p: GamePhase) -> B { NotchUIRules.collapsedKeyBehavior(key, phase: p) }
        let guessing = GamePhase.guessing(tierIndex: 0)
        #expect(k(nil, guessing) == .expandAndReplay)
        #expect(k(nil, .idle) == .expandAndReplay)
        #expect(k(nil, .correct(tierIndex: 0)) == .ignore)
        #expect(k(.returnKey, guessing) == .expand)             // never submit blind
        #expect(k(.returnKey, .correct(tierIndex: 0)) == .perform)
        #expect(k(.returnKey, .setComplete(correctCount: 20)) == .perform)
        #expect(k(.escape, guessing) == .ignore)                // never give up blind
        #expect(k(.commandR, .wrong(tierIndex: 0, verdict: Verdict(titleCorrect: false, artistCorrect: false))) == .perform)
        #expect(k(.tab, .playingSnippet(tierIndex: 0)) == .expand)
        #expect(k(.commandShiftR, guessing) == .perform)          // replay without opening
        #expect(k(.commandShiftR, .correct(tierIndex: 0)) == .perform)
        #expect(k(.commandShiftS, guessing) == .perform)          // skip without opening
        #expect(k(.commandN, guessing) == .perform)               // arming opens the panel itself
        #expect(k(.commandN, .idle) == .ignore)
    }

    @Test func collapsedIndicatorPerPhase() {
        var s = GameState()
        let track = Track(id: "x", uri: "spotify:track:x", title: "Secret Title", artists: ["Secret Artist"],
                          durationMs: 1, previewURL: nil)
        s.currentSet = Array(repeating: track, count: 20)
        s.index = 6
        func ind(_ p: GamePhase, _ secs: Double = 0.1) -> NotchUIRules.CollapsedIndicator {
            s.phase = p
            return NotchUIRules.collapsedIndicator(s, secondsInPhase: secs)
        }
        #expect(ind(.playingSnippet(tierIndex: 1)).glyph == .snippet(tierIndex: 1))
        #expect(ind(.playingSnippet(tierIndex: 1)).text == "7/20")
        #expect(ind(.guessing(tierIndex: 0)).glyph == .awaitingGuess)
        #expect(ind(.wrong(tierIndex: 0, verdict: Verdict(titleCorrect: true, artistCorrect: false))).glyph == .awaitingGuess)
        #expect(ind(.correct(tierIndex: 0), 0.5).glyph == .correctFlash)
        #expect(ind(.correct(tierIndex: 0), 0.5).emphasized)
        #expect(ind(.correct(tierIndex: 0), 2).glyph == .playing)
        #expect(!ind(.correct(tierIndex: 0), 2).emphasized)
        #expect(ind(.revealed(verdict: nil)).glyph == .playing)
        #expect(ind(.setComplete(correctCount: 20), 1) == .init(glyph: .setComplete, text: "20/20", emphasized: true))
        #expect(ind(.setFailed(correctCount: 14), 1) == .init(glyph: .setFailed, text: "14/20", emphasized: true))
        #expect(ind(.setFailed(correctCount: 14), 5).emphasized == false)
        #expect(ind(.loading).text == nil)                      // spinner
        #expect(ind(.error(message: "e")).glyph == .warning)
        #expect(ind(.idle).glyph == .note)
        for p in Self.allPhases {
            let text = ind(p).text ?? ""
            #expect(!text.contains("Secret"), "\(p)")
        }
    }

    @Test func returnKeyMeansThePrimaryButton() {
        func ret(_ p: GamePhase) -> UICommand? { NotchUIRules.command(for: .returnKey, phase: p, focused: nil) }
        #expect(ret(.idle) == .load)
        #expect(ret(.exhausted) == .load)
        #expect(ret(.loading) == nil)
        #expect(ret(.playingSnippet(tierIndex: 0)) == .submitGuess)
        #expect(ret(.guessing(tierIndex: 2)) == .submitGuess)
        #expect(ret(.wrong(tierIndex: 0, verdict: Verdict(titleCorrect: false, artistCorrect: false))) == .send(.retry))
        #expect(ret(.correct(tierIndex: 0)) == .send(.next))
        #expect(ret(.revealed(verdict: nil)) == .send(.next))
        #expect(ret(.error(message: "e")) == .send(.next))
        #expect(ret(.setComplete(correctCount: 20)) == .send(.nextSet))
        #expect(ret(.setFailed(correctCount: 19)) == .send(.replaySet))
    }

    @Test func escapeGivesUpInGuessPhasesElseCollapses() {
        func esc(_ p: GamePhase) -> UICommand? { NotchUIRules.command(for: .escape, phase: p, focused: nil) }
        #expect(esc(.playingSnippet(tierIndex: 0)) == .send(.giveUp))
        #expect(esc(.guessing(tierIndex: 1)) == .send(.giveUp))
        #expect(esc(.wrong(tierIndex: 1, verdict: Verdict(titleCorrect: true, artistCorrect: false))) == .send(.giveUp))
        for p in [GamePhase.idle, .loading, .correct(tierIndex: 0), .revealed(verdict: nil),
                  .setComplete(correctCount: 20), .setFailed(correctCount: 0), .exhausted, .error(message: "e")] {
            #expect(esc(p) == .collapse, "\(p)")
        }
        #expect(NotchUIRules.command(for: .escape, phase: .guessing(tierIndex: 0), focused: .title,
                                     settingsOpen: true) == .closeSettings)
        #expect(NotchUIRules.command(for: .returnKey, phase: .guessing(tierIndex: 0), focused: .title,
                                     settingsOpen: true) == nil)
    }

    @Test func commandROnlyRetriesFromWrong() {
        #expect(NotchUIRules.command(for: .commandR, phase: .wrong(tierIndex: 0, verdict: Verdict(titleCorrect: false, artistCorrect: true)),
                                     focused: nil) == .send(.retry))
        for p in Self.allPhases where { if case .wrong = p { return false } else { return true } }() {
            #expect(NotchUIRules.command(for: .commandR, phase: p, focused: nil) == nil, "\(p)")
        }
    }

    @Test func commandShiftRRestartsWhereTheButtonShows() {
        func r(_ p: GamePhase) -> UICommand? { NotchUIRules.command(for: .commandShiftR, phase: p, focused: .title) }
        #expect(r(.playingSnippet(tierIndex: 0)) == .send(.restart))
        #expect(r(.guessing(tierIndex: 2)) == .send(.restart))
        #expect(r(.correct(tierIndex: 1)) == .send(.restart))
        #expect(r(.revealed(verdict: nil)) == .send(.restart))
        #expect(r(.wrong(tierIndex: 0, verdict: Verdict(titleCorrect: false, artistCorrect: false))) == .send(.restart))
        for p in [GamePhase.idle, .loading,
                  .setComplete(correctCount: 20), .setFailed(correctCount: 3), .exhausted, .error(message: "e")] {
            #expect(r(p) == nil, "\(p)")
        }
        // Settings cover the phase: only Esc does anything.
        #expect(NotchUIRules.command(for: .commandShiftR, phase: .guessing(tierIndex: 0), focused: nil,
                                     settingsOpen: true) == nil)
        // ⌘R stays Retry, and only Retry.
        #expect(NotchUIRules.command(for: .commandR, phase: .guessing(tierIndex: 0), focused: nil) == nil)
        for p in Self.allPhases {
            #expect(NotchUIRules.canRestart(p) == (r(p) != nil), "\(p)")
        }
    }

    @Test func skipSecondsIsTheNextTierAndNilAtTheLast() {
        let c = GameConfig()
        #expect(NotchUIRules.skipSeconds(.playingSnippet(tierIndex: 0), c) == 10)
        #expect(NotchUIRules.skipSeconds(.guessing(tierIndex: 1), c) == 15)
        #expect(NotchUIRules.skipSeconds(.playingSnippet(tierIndex: 2), c) == nil)
        #expect(NotchUIRules.skipSeconds(.guessing(tierIndex: 2), c) == nil)
        #expect(NotchUIRules.skipSeconds(.guessing(tierIndex: 0), GameConfig(tiers: [2, 4])) == 4)
        #expect(NotchUIRules.skipSeconds(.guessing(tierIndex: 1), GameConfig(tiers: [2, 4])) == nil)
        #expect(NotchUIRules.skipSeconds(.guessing(tierIndex: 0), GameConfig(tiers: [])) == 10)  // engine fallback
        for p in Self.allPhases where !NotchUIRules.showsGuessFields(p) {
            #expect(NotchUIRules.skipSeconds(p, c) == nil, "\(p)")
        }
    }

    @Test func commandShiftSSkipsOnlyWhereALongerTierIsLeft() {
        func s(_ p: GamePhase, _ c: GameConfig = GameConfig()) -> UICommand? {
            NotchUIRules.command(for: .commandShiftS, phase: p, focused: .artist, config: c)
        }
        #expect(s(.playingSnippet(tierIndex: 0)) == .send(.skip))
        #expect(s(.guessing(tierIndex: 1)) == .send(.skip))
        #expect(s(.guessing(tierIndex: 2)) == nil)                          // never a hidden Give up
        #expect(s(.playingSnippet(tierIndex: 1), GameConfig(tiers: [5, 10])) == nil)
        for p in Self.allPhases where !NotchUIRules.showsGuessFields(p) {
            #expect(s(p) == nil, "\(p)")
        }
        #expect(NotchUIRules.command(for: .commandShiftS, phase: .guessing(tierIndex: 0), focused: nil,
                                     settingsOpen: true) == nil)
    }

    // MARK: Quit

    @Test func quitShowsWhereAListingIsLoaded() {
        #expect(!NotchUIRules.showsQuit(.idle))
        #expect(!NotchUIRules.showsQuit(.exhausted))
        #expect(Self.allPhases.filter(NotchUIRules.showsQuit).count == 9)
    }

    @Test func quitStaysArmedForThreeSeconds() {
        #expect(!NotchUIRules.isQuitArmed(armedAt: nil, now: t0))
        #expect(NotchUIRules.isQuitArmed(armedAt: t0, now: t0))
        #expect(NotchUIRules.isQuitArmed(armedAt: t0, now: t0.addingTimeInterval(2.99)))
        #expect(!NotchUIRules.isQuitArmed(armedAt: t0, now: t0.addingTimeInterval(3)))
        #expect(!NotchUIRules.isQuitArmed(armedAt: t0, now: t0.addingTimeInterval(-1)))   // clock went back
        #expect(NotchUIRules.quitConfirmWindow == 3)
    }

    @Test func commandNQuitsAndEscOnlyDisarms() {
        for p in Self.allPhases {
            let expected: UICommand? = NotchUIRules.showsQuit(p) ? .quit : nil
            #expect(NotchUIRules.command(for: .commandN, phase: p, focused: nil) == expected, "\(p)")
            #expect(NotchUIRules.command(for: .commandN, phase: p, focused: nil, quitArmed: true) == expected, "\(p)")
        }
        // Armed: Esc cancels the question and nothing else (no Give up, no collapse).
        for p in Self.allPhases where NotchUIRules.showsQuit(p) {
            #expect(NotchUIRules.command(for: .escape, phase: p, focused: .title, quitArmed: true) == .disarmQuit, "\(p)")
        }
        #expect(NotchUIRules.command(for: .escape, phase: .guessing(tierIndex: 0), focused: .title,
                                     settingsOpen: true, quitArmed: true) == .disarmQuit)
        #expect(NotchUIRules.command(for: .escape, phase: .guessing(tierIndex: 0), focused: .title) == .send(.giveUp))
        #expect(NotchUIRules.command(for: .commandN, phase: .guessing(tierIndex: 0), focused: nil,
                                     settingsOpen: true) == nil)
    }

    @Test func restartLabelPerPhase() {
        #expect(NotchUIRules.restartLabel(.playingSnippet(tierIndex: 1)) == "Replay snippet")
        #expect(NotchUIRules.restartLabel(.guessing(tierIndex: 0)) == "Replay snippet")
        #expect(NotchUIRules.restartLabel(.correct(tierIndex: 0)) == "Restart song")
        #expect(NotchUIRules.restartLabel(.revealed(verdict: Verdict(titleCorrect: true, artistCorrect: false))) == "Restart song")
        #expect(NotchUIRules.restartLabel(.wrong(tierIndex: 0, verdict: Verdict(titleCorrect: true, artistCorrect: false))) == "Replay snippet")
        #expect(Self.allPhases.filter(NotchUIRules.canRestart).count == 5)
    }

    @Test func tabMovesBetweenTitleAndArtist() {
        let p = GamePhase.guessing(tierIndex: 0)
        #expect(NotchUIRules.command(for: .tab, phase: p, focused: .title) == .focus(.artist))
        #expect(NotchUIRules.command(for: .tab, phase: p, focused: .artist) == .focus(.title))
        #expect(NotchUIRules.command(for: .backTab, phase: .playingSnippet(tierIndex: 0), focused: .artist) == .focus(.title))
        #expect(NotchUIRules.command(for: .tab, phase: .idle, focused: .url) == nil)
    }

    @Test func keyEventsMapToKeyInputs() {
        typealias C = NotchPanelController
        #expect(C.keyInput(keyCode: 36, characters: "\r", modifiers: []) == .returnKey)
        #expect(C.keyInput(keyCode: 76, characters: "\u{3}", modifiers: []) == .returnKey)
        #expect(C.keyInput(keyCode: 53, characters: "\u{1b}", modifiers: []) == .escape)
        #expect(C.keyInput(keyCode: 48, characters: "\t", modifiers: []) == .tab)
        #expect(C.keyInput(keyCode: 48, characters: "\t", modifiers: .shift) == .backTab)
        #expect(C.keyInput(keyCode: 15, characters: "r", modifiers: .command) == .commandR)
        #expect(C.keyInput(keyCode: 15, characters: "r", modifiers: []) == nil)       // typing "r"
        #expect(C.keyInput(keyCode: 15, characters: "R", modifiers: [.command, .shift]) == .commandShiftR)
        #expect(C.keyInput(keyCode: 15, characters: "r", modifiers: [.command, .shift, .capsLock]) == .commandShiftR)
        #expect(C.keyInput(keyCode: 15, characters: "R", modifiers: .shift) == nil)   // typing "R"
        #expect(C.keyInput(keyCode: 15, characters: "r", modifiers: [.command, .option]) == nil)
        #expect(C.keyInput(keyCode: 15, characters: "r", modifiers: [.command, .shift, .control]) == nil)
        #expect(C.keyInput(keyCode: 1, characters: "S", modifiers: [.command, .shift]) == .commandShiftS)
        #expect(C.keyInput(keyCode: 1, characters: "s", modifiers: .command) == nil)             // ⌘S: not ours
        #expect(C.keyInput(keyCode: 1, characters: "S", modifiers: .shift) == nil)               // typing "S"
        #expect(C.keyInput(keyCode: 45, characters: "n", modifiers: .command) == .commandN)
        #expect(C.keyInput(keyCode: 45, characters: "N", modifiers: [.command, .shift]) == nil)
        #expect(C.keyInput(keyCode: 45, characters: "n", modifiers: []) == nil)                  // typing "n"
        #expect(C.keyInput(keyCode: 36, characters: "\r", modifiers: .shift) == nil)
        #expect(C.keyInput(keyCode: 36, characters: "\r", modifiers: [.capsLock, .numericPad]) == .returnKey)
        #expect(C.editAction(keyCode: 9, characters: "v", modifiers: .command) == #selector(NSText.paste(_:)))
        #expect(C.editAction(keyCode: 0, characters: "a", modifiers: .command) == #selector(NSText.selectAll(_:)))
        #expect(C.editAction(keyCode: 9, characters: "v", modifiers: []) == nil)
    }

    @Test func fieldsResetForANewTrackAndKeepForARetry() {
        #expect(NotchUIRules.fieldTransition(from: .correct(tierIndex: 0), to: .playingSnippet(tierIndex: 0)) == .clearAndFocusTitle)
        #expect(NotchUIRules.fieldTransition(from: .loading, to: .playingSnippet(tierIndex: 0)) == .clearAndFocusTitle)
        #expect(NotchUIRules.fieldTransition(from: .error(message: "e"), to: .guessing(tierIndex: 0)) == .clearAndFocusTitle)
        #expect(NotchUIRules.fieldTransition(from: .playingSnippet(tierIndex: 0), to: .guessing(tierIndex: 0)) == .none)
        #expect(NotchUIRules.fieldTransition(from: .wrong(tierIndex: 0, verdict: Verdict(titleCorrect: true, artistCorrect: false)),
                                             to: .playingSnippet(tierIndex: 1)) == .keepAndFocus(.artist))
        #expect(NotchUIRules.fieldTransition(from: .wrong(tierIndex: 1, verdict: Verdict(titleCorrect: false, artistCorrect: true)),
                                             to: .playingSnippet(tierIndex: 2)) == .keepAndFocus(.title))
        #expect(NotchUIRules.fieldTransition(from: nil, to: .idle) == .focusURL)
        #expect(NotchUIRules.fieldTransition(from: .guessing(tierIndex: 0), to: .guessing(tierIndex: 0)) == .none)
        // Restart (replay) from guessing: same track, keep the guess and the focus.
        #expect(NotchUIRules.fieldTransition(from: .guessing(tierIndex: 0), to: .playingSnippet(tierIndex: 0)) == .none)
        #expect(NotchUIRules.fieldTransition(from: .guessing(tierIndex: 2), to: .playingSnippet(tierIndex: 2)) == .none)
        // Skip: next tier of the same track, keep the guess and the focus.
        #expect(NotchUIRules.fieldTransition(from: .playingSnippet(tierIndex: 0), to: .playingSnippet(tierIndex: 1)) == .none)
        #expect(NotchUIRules.fieldTransition(from: .guessing(tierIndex: 1), to: .playingSnippet(tierIndex: 2)) == .none)
        // A new track from the last-tier snippet still clears (tier 0 is never a skip target).
        #expect(NotchUIRules.fieldTransition(from: .playingSnippet(tierIndex: 2), to: .playingSnippet(tierIndex: 0)) == .clearAndFocusTitle)
    }

    @Test func labels() {
        let c = GameConfig()
        #expect(NotchUIRules.retrySeconds(after: 0, c) == 10)
        #expect(NotchUIRules.retrySeconds(after: 1, c) == 15)
        #expect(NotchUIRules.secondsLabel(5) == "5s")
        #expect(NotchUIRules.secondsLabel(2.5) == "2.5s")
        #expect(NotchUIRules.artistPlaceholder(artistCount: 1) == "Artist(s)")
        // The number of artists is never shown: knowing them is part of the game.
        #expect(NotchUIRules.artistPlaceholder(artistCount: 3) == "Artist(s)")
        #expect(NotchUIRules.artistHint(Verdict(titleCorrect: true, artistCorrect: false), artistCount: 3) == nil)
        #expect(NotchUIRules.artistHint(Verdict(titleCorrect: true, artistCorrect: false), artistCount: 1) == nil)
        #expect(NotchUIRules.artistHint(Verdict(titleCorrect: false, artistCorrect: true), artistCount: 3) == nil)
    }
}
