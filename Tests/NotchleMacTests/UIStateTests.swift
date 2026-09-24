import Foundation
import Testing
import NotchleCore
@testable import NotchleMac

@MainActor
@Suite struct UIStateTests {
    let track = Track(id: "a", uri: "spotify:track:a", title: "T", artists: ["A"], durationMs: 1, previewURL: nil)

    func make(_ phase: GamePhase) -> (NotchViewModel, NotchUIState) {
        var s = GameState()
        s.currentSet = [track]
        s.phase = phase
        let model = NotchViewModel(state: s)
        return (model, NotchUIState(model: model))
    }

    @Test func noConfettiOnLaunchEvenWithACount() {
        let (model, ui) = make(.correct(tierIndex: 0))
        model.state.celebrationCount = 7
        ui.stateDidChange(from: nil, to: model.state)
        #expect(ui.celebrationStart == nil)
    }

    @Test func confettiOnlyWhenTheCountIncreases() {
        let (model, ui) = make(.guessing(tierIndex: 0))
        let before = model.state
        var after = before
        after.phase = .correct(tierIndex: 0)
        after.celebrationCount += 1
        ui.stateDidChange(from: before, to: after)
        #expect(ui.celebrationStart != nil)

        ui.celebrationStart = nil
        var next = after
        next.phase = .playingSnippet(tierIndex: 0)
        ui.stateDidChange(from: after, to: next)
        #expect(ui.celebrationStart == nil)

        var reset = next
        reset.celebrationCount = 0
        ui.stateDidChange(from: next, to: reset)
        #expect(ui.celebrationStart == nil)
    }

    @Test func newTrackClearsFieldsAndFocusesTitle() {
        let (model, ui) = make(.correct(tierIndex: 0))
        ui.titleText = "old"
        ui.artistText = "old"
        var next = model.state
        next.phase = .playingSnippet(tierIndex: 0)
        ui.stateDidChange(from: model.state, to: next)
        #expect(ui.titleText.isEmpty && ui.artistText.isEmpty)
        #expect(ui.requestedFocus == .title)
    }

    @Test func retryKeepsFieldsAndFocusesTheWrongHalf() {
        let (model, ui) = make(.wrong(tierIndex: 0, verdict: Verdict(titleCorrect: true, artistCorrect: false)))
        ui.titleText = "T"
        ui.artistText = "B"
        var next = model.state
        next.phase = .playingSnippet(tierIndex: 1)
        ui.stateDidChange(from: model.state, to: next)
        #expect(ui.titleText == "T" && ui.artistText == "B")
        #expect(ui.requestedFocus == .artist)
    }

    @Test func replayWhileGuessingKeepsTheGuessAndTheFocus() {
        let (model, ui) = make(.guessing(tierIndex: 0))
        var sent: [GameAction] = []
        model.send = { action in
            sent.append(action)
            if action == .restart { model.state.phase = .playingSnippet(tierIndex: 0) }
        }
        ui.requestFocus(.title)               // track start focused Title...
        ui.focusedField = .artist             // ...then the player clicked into Artist
        ui.titleText = "Paper"
        ui.artistText = "Kit"
        var old = model.state
        old.phase = .guessing(tierIndex: 0)

        ui.perform(.send(.restart))
        #expect(sent == [.restart])
        ui.stateDidChange(from: old, to: model.state, now: t0)
        #expect(ui.titleText == "Paper" && ui.artistText == "Kit")
        #expect(ui.requestedFocus == .artist)
        #expect(ui.snippetStart == t0)
    }

    @Test func replayMidSnippetRestartsTheProgress() {
        let (model, ui) = make(.playingSnippet(tierIndex: 1))
        var sent: [GameAction] = []
        model.send = { sent.append($0) }      // the engine leaves the phase as it is
        ui.snippetStart = t0
        ui.titleText = "Pap"
        ui.restart(now: t0.addingTimeInterval(3))
        #expect(sent == [.restart])
        #expect(ui.snippetStart == t0.addingTimeInterval(3))
        #expect(ui.titleText == "Pap")
    }

    @Test func restartAfterAnAnswerSendsRestartAndLeavesFieldsAlone() {
        let (model, ui) = make(.correct(tierIndex: 0))
        var sent: [GameAction] = []
        model.send = { sent.append($0) }
        ui.snippetStart = t0
        ui.restart(now: t0.addingTimeInterval(9))
        #expect(sent == [.restart])
        #expect(ui.snippetStart == t0)        // no snippet: nothing to animate
    }

    @Test func restartIsIgnoredInWrong() {
        let (model, ui) = make(.wrong(tierIndex: 0, verdict: Verdict(titleCorrect: false, artistCorrect: true)))
        var sent: [GameAction] = []
        model.send = { sent.append($0) }
        ui.restart()
        ui.perform(.send(.restart))
        #expect(sent.isEmpty)
    }

    @Test func skipKeepsTheGuessAndTheFocus() {
        let (model, ui) = make(.playingSnippet(tierIndex: 0))
        var sent: [GameAction] = []
        model.send = { action in
            sent.append(action)
            if action == .skip { model.state.phase = .playingSnippet(tierIndex: 1) }
        }
        ui.requestFocus(.title)
        ui.focusedField = .artist
        ui.titleText = "Paper"
        ui.artistText = "Kit"
        let old = model.state

        ui.perform(.send(.skip))
        #expect(sent == [.skip])
        ui.stateDidChange(from: old, to: model.state, now: t0)
        #expect(ui.titleText == "Paper" && ui.artistText == "Kit")
        #expect(ui.requestedFocus == .artist)
        #expect(ui.snippetStart == t0)                 // the longer snippet's progress starts now
    }

    @Test func skipDoesNothingAtTheLastTierOrOutsideGuessing() {
        for phase in [GamePhase.guessing(tierIndex: 2), .playingSnippet(tierIndex: 2),
                      .wrong(tierIndex: 0, verdict: Verdict(titleCorrect: false, artistCorrect: false)),
                      .correct(tierIndex: 0)] {
            let (model, ui) = make(phase)
            var sent: [GameAction] = []
            model.send = { sent.append($0) }
            ui.skip()
            ui.perform(.send(.skip))
            #expect(sent.isEmpty, "\(phase)")
        }
    }

    // MARK: Auto-close

    let t0 = Date(timeIntervalSinceReferenceDate: 2_000_000)

    @Test func phaseChangesNeverExpand() {
        for phase in UIRulesTests.allPhases {
            let (model, ui) = make(.loading)
            var next = model.state
            next.phase = phase
            ui.stateDidChange(from: model.state, to: next, now: t0)
            #expect(!ui.isExpanded, "\(phase)")
        }
    }

    @Test func hoverExpandsAndLeavingCollapsesAfterTheGrace() {
        let (_, ui) = make(.guessing(tierIndex: 0))
        ui.hoverChanged(true, now: t0)
        #expect(ui.isExpanded)
        ui.hoverChanged(false, now: t0)
        #expect(ui.autoCollapsePending)
        #expect(!ui.evaluateAutoCollapse(now: t0.addingTimeInterval(0.3)))
        #expect(ui.isExpanded)
        #expect(ui.evaluateAutoCollapse(now: t0.addingTimeInterval(0.45)))
        #expect(!ui.isExpanded)
        #expect(!ui.autoCollapsePending)
    }

    @Test func reenteringDuringTheGraceCancels() {
        let (_, ui) = make(.correct(tierIndex: 0))
        ui.hoverChanged(true, now: t0)
        ui.hoverChanged(false, now: t0)
        ui.hoverChanged(true, now: t0.addingTimeInterval(0.2))
        #expect(!ui.evaluateAutoCollapse(now: t0.addingTimeInterval(5)))
        #expect(ui.isExpanded)
    }

    @Test func typedTextKeepsItOpenUntilCleared() {
        let (_, ui) = make(.guessing(tierIndex: 0))
        ui.hoverChanged(true, now: t0)
        ui.focusedField = .title
        ui.titleText = "Pap"
        ui.hoverChanged(false, now: t0)
        #expect(!ui.evaluateAutoCollapse(now: t0.addingTimeInterval(30)))
        #expect(ui.isExpanded)
        ui.titleText = ""                    // cleared, and no key for > 2s
        #expect(ui.evaluateAutoCollapse(now: t0.addingTimeInterval(31)))
    }

    @Test func aRecentKeyKeepsAnEmptyFieldOpenForTwoSeconds() {
        let (_, ui) = make(.idle)
        ui.clicked()
        ui.focusedField = .url
        ui.hoverChanged(true, now: t0)
        ui.hoverChanged(false, now: t0)
        ui.noteKeyActivity(now: t0.addingTimeInterval(0.1))
        #expect(!ui.evaluateAutoCollapse(now: t0.addingTimeInterval(1.5)))
        #expect(ui.evaluateAutoCollapse(now: t0.addingTimeInterval(2.2)))
    }

    @Test func focusWithoutTextOrKeysDoesNotCountAsTyping() {
        let (_, ui) = make(.guessing(tierIndex: 0))
        ui.hoverChanged(true, now: t0)
        ui.focusedField = .title
        ui.hoverChanged(false, now: t0)
        #expect(ui.evaluateAutoCollapse(now: t0.addingTimeInterval(0.5)))
    }

    @Test func submittingEndsTypingSoItCollapses() {
        let (model, ui) = make(.guessing(tierIndex: 0))
        var sent: [GameAction] = []
        model.send = { sent.append($0) }
        ui.hoverChanged(true, now: t0)
        ui.focusedField = .artist
        ui.titleText = "T"
        ui.artistText = "A"
        ui.hoverChanged(false, now: t0)
        ui.noteKeyActivity(now: t0)          // the Return press
        ui.submitGuess()
        #expect(sent == [.submit(Guess(title: "T", artist: "A"))])
        var next = model.state
        next.phase = .wrong(tierIndex: 0, verdict: Verdict(titleCorrect: false, artistCorrect: false))
        ui.stateDidChange(from: model.state, to: next, now: t0)
        #expect(ui.focusedField == nil)      // the fields went away with the phase
        #expect(ui.evaluateAutoCollapse(now: t0.addingTimeInterval(0.5)))
    }

    @Test func clickExpands() {
        let (_, ui) = make(.playingSnippet(tierIndex: 0))
        ui.clicked()
        #expect(ui.isExpanded)
        #expect(!ui.autoCollapsePending)
    }

    @Test func typingIntoACollapsedPanelOpensItWhileTyping() {
        let (_, ui) = make(.playingSnippet(tierIndex: 0))
        ui.expandForTyping(now: t0)
        #expect(ui.isExpanded)
        #expect(ui.requestedFocus == .title)
        ui.focusedField = .title
        #expect(!ui.evaluateAutoCollapse(now: t0.addingTimeInterval(1)))   // key 1s ago
        #expect(ui.evaluateAutoCollapse(now: t0.addingTimeInterval(2.5)))  // idle, empty, mouse outside
    }

    @Test func collapseClosesSettings() {
        let (_, ui) = make(.guessing(tierIndex: 0))
        ui.hoverChanged(true, now: t0)
        ui.showingSettings = true
        ui.hoverChanged(false, now: t0)
        #expect(ui.evaluateAutoCollapse(now: t0.addingTimeInterval(1)))
        #expect(!ui.showingSettings)
    }

    @Test func correctFlashesThenPlaysOn() {
        let (model, ui) = make(.guessing(tierIndex: 0))
        var next = model.state
        next.phase = .correct(tierIndex: 0)
        next.celebrationCount += 1
        ui.stateDidChange(from: model.state, to: next, now: t0)
        model.state = next
        #expect(!ui.isExpanded)                       // collapsed…
        #expect(ui.celebrationStart == t0)            // …but confetti still fires
        #expect(ui.collapsedIndicator(now: t0.addingTimeInterval(0.5)).glyph == .correctFlash)
        #expect(ui.collapsedIndicator(now: t0.addingTimeInterval(2)).glyph == .playing)
    }

    @Test func unsupportedLinkShowsAMessage() {
        let (model, ui) = make(.idle)
        var sent: [GameAction] = []
        model.send = { sent.append($0) }
        ui.urlText = "https://example.com/x"
        ui.parseSource = { _ in nil }
        ui.load()
        #expect(ui.urlMessage == "Unsupported link")
        #expect(sent.isEmpty)
        ui.parseSource = { _ in SourceRef(kind: .album, id: "x") }
        ui.load()
        #expect(sent == [.load(SourceRef(kind: .album, id: "x"))])
    }

    @Test func submitNeedsBothHalves() {
        let (model, ui) = make(.guessing(tierIndex: 0))
        var sent: [GameAction] = []
        model.send = { sent.append($0) }
        ui.titleText = "T"
        ui.submitGuess()
        #expect(sent.isEmpty)
        #expect(ui.requestedFocus == .artist)
        ui.artistText = " A "
        ui.submitGuess()
        #expect(sent == [.submit(Guess(title: "T", artist: "A"))])
    }

    @Test func confettiIsEightyParticlesAndDeterministic() {
        #expect(Confetti.particles(seed: 42).count == 80)
        #expect(Confetti.particles(seed: 42) == Confetti.particles(seed: 42))
        #expect(Confetti.particles(seed: 42) != Confetti.particles(seed: 43))
        #expect(Confetti.opacity(at: 0.1) == 1)
        #expect(Confetti.opacity(at: Confetti.duration) == 0)
        // Particles end up below where they start: they fall out of the notch.
        let p = Confetti.particles(seed: 1)
        let below = p.filter { Confetti.offset($0, at: 1.2).y > 0 }.count
        #expect(below == p.count)
    }
}
