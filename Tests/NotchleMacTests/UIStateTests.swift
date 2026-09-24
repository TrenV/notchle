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

    @Test func expandsForInputAndCollapsesOnHoverEndOnlyWhenIdleOfInput() {
        let (model, ui) = make(.loading)
        var next = model.state
        next.phase = .guessing(tierIndex: 0)
        ui.stateDidChange(from: model.state, to: next)
        model.state = next
        #expect(ui.isExpanded)
        ui.hoverChanged(true)
        ui.hoverChanged(false)
        #expect(ui.isExpanded)          // guessing needs input

        let (m2, ui2) = make(.playingSnippet(tierIndex: 0))
        _ = m2
        ui2.hoverChanged(true)
        #expect(ui2.isExpanded)
        ui2.hoverChanged(false)
        #expect(!ui2.isExpanded)
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
