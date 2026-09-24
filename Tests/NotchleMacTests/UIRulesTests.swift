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

    @Test func autoExpandPhases() {
        let expanding = Self.allPhases.filter(NotchUIRules.needsInput)
        #expect(expanding.count == 9)
        #expect(!NotchUIRules.needsInput(.loading))
        #expect(!NotchUIRules.needsInput(.playingSnippet(tierIndex: 2)))
        for p in [GamePhase.idle, .guessing(tierIndex: 0), .correct(tierIndex: 1), .revealed(verdict: nil),
                  .setComplete(correctCount: 20), .setFailed(correctCount: 1), .exhausted, .error(message: "e")] {
            #expect(NotchUIRules.needsInput(p), "\(p)")
        }
    }

    @Test func hoverEndCollapsesOnlyWithoutInput() {
        #expect(NotchUIRules.collapsesOnHoverEnd(.loading, hasTypedGuess: false))
        #expect(NotchUIRules.collapsesOnHoverEnd(.playingSnippet(tierIndex: 0), hasTypedGuess: false))
        #expect(!NotchUIRules.collapsesOnHoverEnd(.playingSnippet(tierIndex: 0), hasTypedGuess: true))
        #expect(!NotchUIRules.collapsesOnHoverEnd(.guessing(tierIndex: 0), hasTypedGuess: false))
        #expect(!NotchUIRules.collapsesOnHoverEnd(.idle, hasTypedGuess: false))
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
    }

    @Test func labels() {
        let c = GameConfig()
        #expect(NotchUIRules.retrySeconds(after: 0, c) == 10)
        #expect(NotchUIRules.retrySeconds(after: 1, c) == 15)
        #expect(NotchUIRules.secondsLabel(5) == "5s")
        #expect(NotchUIRules.secondsLabel(2.5) == "2.5s")
        #expect(NotchUIRules.artistPlaceholder(artistCount: 1) == "Artist(s)")
        #expect(NotchUIRules.artistPlaceholder(artistCount: 3) == "3 artists, any order")
        #expect(NotchUIRules.artistHint(Verdict(titleCorrect: true, artistCorrect: false), artistCount: 3) == "need all 3")
        #expect(NotchUIRules.artistHint(Verdict(titleCorrect: true, artistCorrect: false), artistCount: 1) == nil)
        #expect(NotchUIRules.artistHint(Verdict(titleCorrect: false, artistCorrect: true), artistCount: 3) == nil)
    }
}
