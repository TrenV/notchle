import AppKit
import Foundation
import Testing
import NotchleCore
@testable import NotchleMac

private let track = Track(id: "a", uri: "spotify:track:a", title: "T", artists: ["A"], durationMs: 1, previewURL: nil)

private func entry(_ date: Date, correct: Bool = true, tier: Int? = 0, id: String = "x") -> HistoryEntry {
    HistoryEntry(date: date, trackID: id, title: "T", artists: ["A"], listingName: "L", listingRef: nil,
                 correct: correct, tierIndex: tier, wrongGuesses: 0, skips: 0)
}

@Suite struct UIHistoryRulesTests {
    typealias R = NotchUIRules

    @Test func tabKeysWorkEverywhere() {
        for phase in [GamePhase.idle, .playingSnippet(tierIndex: 0), .guessing(tierIndex: 1), .correct(tierIndex: 0),
                      .setFailed(correctCount: 3)] {
            for (settings, history, quit) in [(false, false, false), (true, false, false), (false, true, false), (false, true, true)] {
                #expect(R.command(for: .command1, phase: phase, focused: nil, settingsOpen: settings,
                                  quitArmed: quit, historyOpen: history) == .showTab(.play))
                #expect(R.command(for: .command2, phase: phase, focused: .title, settingsOpen: settings,
                                  quitArmed: quit, historyOpen: history) == .showTab(.history))
            }
            #expect(R.collapsedKeyBehavior(.command1, phase: phase) == .perform)
            #expect(R.collapsedKeyBehavior(.command2, phase: phase) == .perform)
        }
    }

    /// Behind the History tab, game keys are inert: Esc goes back to Play (never gives up),
    /// Return never submits blind.
    @Test func gameKeysAreInertOnTheHistoryTab() {
        for phase in [GamePhase.playingSnippet(tierIndex: 0), .guessing(tierIndex: 0),
                      .wrong(tierIndex: 0, verdict: Verdict(titleCorrect: false, artistCorrect: false)),
                      .correct(tierIndex: 0), .setFailed(correctCount: 1)] {
            #expect(R.command(for: .escape, phase: phase, focused: nil, historyOpen: true) == .showTab(.play))
            for key in [UIKeyInput.returnKey, .tab, .backTab, .commandR, .commandShiftR, .commandShiftS] {
                #expect(R.command(for: key, phase: phase, focused: .title, historyOpen: true) == nil, "\(key) \(phase)")
            }
            #expect(R.command(for: .commandN, phase: phase, focused: nil, historyOpen: true) == .quit)
            #expect(R.command(for: .escape, phase: phase, focused: nil, quitArmed: true, historyOpen: true) == .disarmQuit)
        }
        // Off the History tab nothing changed.
        #expect(R.command(for: .escape, phase: .guessing(tierIndex: 0), focused: nil) == .send(.giveUp))
    }

    @Test func keyMapping() {
        typealias C = NotchPanelController
        #expect(C.keyInput(keyCode: 18, characters: "1", modifiers: .command) == .command1)
        #expect(C.keyInput(keyCode: 19, characters: "2", modifiers: .command) == .command2)
        #expect(C.keyInput(keyCode: 18, characters: "1", modifiers: []) == nil)
        #expect(C.keyInput(keyCode: 19, characters: "2", modifiers: [.command, .shift]) == nil)
    }

    @Test func badges() {
        let now = Date()
        #expect(NotchHistoryRules.badge(entry(now, tier: 0)) == "1st try")
        #expect(NotchHistoryRules.badge(entry(now, tier: 1)) == "2nd try")
        #expect(NotchHistoryRules.badge(entry(now, tier: 2)) == "3rd try")
        #expect(NotchHistoryRules.badge(entry(now, tier: 3)) == "4th try")
        #expect(NotchHistoryRules.badge(entry(now, correct: false, tier: nil)) == "missed")
        #expect(NotchHistoryRules.ordinal(11) == "11th" && NotchHistoryRules.ordinal(22) == "22nd")
    }

    @Test func relativeDates() {
        let now = Date(timeIntervalSince1970: 1_800_000_000)
        var cal = Calendar(identifier: .gregorian)
        cal.timeZone = TimeZone(identifier: "UTC")!
        let r = { NotchHistoryRules.relativeDate(now.addingTimeInterval(-$0), now: now, calendar: cal) }
        #expect(r(10) == "just now")
        #expect(r(5 * 60) == "5m ago")
        #expect(r(2 * 3600 + 100) == "2h ago")
        #expect(r(3 * 86_400) == "3d ago")
        #expect(r(30 * 86_400) == "16 Dec")
        #expect(r(-30) == "just now")   // clock skew
    }

    @Test func dayGroupsNewestFirst() {
        var cal = Calendar(identifier: .gregorian)
        cal.timeZone = TimeZone(identifier: "UTC")!
        let now = Date(timeIntervalSince1970: 1_800_000_000)   // 2027-01-15 08:00 UTC
        let entries = [entry(now.addingTimeInterval(-3 * 86_400), id: "old"), entry(now.addingTimeInterval(-60), id: "a"),
                       entry(now.addingTimeInterval(-86_400), id: "y"), entry(now.addingTimeInterval(-120), id: "b")]
        let groups = NotchHistoryRules.dayGroups(entries, now: now, calendar: cal)
        #expect(groups.map(\.title) == ["Today", "Yesterday", "Tue 12 Jan"])
        #expect(groups.map { $0.entries.map(\.trackID) } == [["a", "b"], ["y"], ["old"]])
    }

    @Test func statsTexts() {
        let now = Date()
        let s = HistoryStats([entry(now, tier: 0), entry(now.addingTimeInterval(1), tier: 1),
                              entry(now.addingTimeInterval(2), correct: false, tier: nil)])
        let t = NotchHistoryRules.statsTexts(s)
        #expect(t.score == "2/3" && t.accuracy == "67%" && t.avgTries == "1.5" && t.bestStreak == "2")
        #expect(NotchHistoryRules.statsTexts(HistoryStats([])).avgTries == "–")
    }

    @Test func historyShapeIsTallerOnlyWhenExpanded() {
        let g = NotchGeometry(hardwareNotch: CGRect(x: 0, y: 0, width: 185, height: 32),
                              notchSize: CGSize(width: 185, height: 32), centerX: 500, anchorTop: 900)
        #expect(NotchMetrics.shapeSize(g, expanded: true, tall: true) == NotchMetrics.historySize)
        #expect(NotchMetrics.shapeSize(g, expanded: true) == NotchMetrics.expandedSize)
        #expect(NotchMetrics.shapeSize(g, expanded: false, tall: true) == NotchMetrics.collapsedSize(g))
        #expect(NotchMetrics.hoverFrame(g, expanded: true, tall: true).height > NotchMetrics.hoverFrame(g, expanded: true).height)
    }
}

@MainActor
@Suite struct UIHistoryStateTests {
    func make(_ phase: GamePhase) -> (NotchViewModel, NotchUIState) {
        var s = GameState()
        s.currentSet = [track]
        s.phase = phase
        let model = NotchViewModel(state: s)
        return (model, NotchUIState(model: model))
    }

    @Test func switchingToHistoryAndBackLeavesTheGameAlone() {
        let (model, ui) = make(.playingSnippet(tierIndex: 1))
        var sent: [GameAction] = []
        model.send = { sent.append($0) }
        ui.isExpanded = true
        ui.titleText = "Paper"
        ui.focusedField = .artist
        ui.perform(.showTab(.history))
        #expect(ui.tab == .history && ui.usesTallLayout)
        ui.focusedField = nil                               // the fields are gone from the screen
        model.state.phase = .guessing(tierIndex: 1)         // the snippet ends meanwhile
        ui.stateDidChange(from: GameState(), to: model.state)
        ui.perform(.showTab(.play))
        #expect(ui.tab == .play && !ui.usesTallLayout)
        #expect(sent.isEmpty, "switching tabs never touches the game")
        #expect(ui.titleText == "Paper")
        #expect(ui.requestedFocus == .artist, "the cursor goes back where it was")
        #expect(model.state.phase == .guessing(tierIndex: 1))
    }

    @Test func tabKeyOpensACollapsedPanelAndClosesSettings() {
        let (_, ui) = make(.idle)
        ui.showingSettings = true
        ui.perform(.showTab(.history))
        #expect(ui.isExpanded && ui.tab == .history && !ui.showingSettings)
        #expect(ui.autoCollapsePending, "opened without hover: it still auto-collapses")
    }

    @Test func collapseReturnsToPlay() {
        let (_, ui) = make(.guessing(tierIndex: 0))
        ui.showTab(.history)
        ui.collapse()
        #expect(ui.tab == .play && !ui.isExpanded)
    }

    @Test func autoCollapseUnchangedOnTheHistoryTab() {
        let (_, ui) = make(.guessing(tierIndex: 0))
        let t0 = Date()
        ui.hoverChanged(true, now: t0)
        ui.showTab(.history, now: t0)
        ui.hoverChanged(false, now: t0)
        #expect(!ui.evaluateAutoCollapse(now: t0.addingTimeInterval(0.1)))
        #expect(ui.evaluateAutoCollapse(now: t0.addingTimeInterval(NotchUIRules.collapseGrace + 0.01)))
    }

    @Test func clearHistoryNeedsTwoPresses() {
        let (model, ui) = make(.idle)
        var cleared = 0
        model.clearHistory = { cleared += 1 }
        let t0 = Date()
        ui.clearHistoryPressed(now: t0)
        #expect(cleared == 0 && ui.isClearHistoryArmed(now: t0))
        ui.clearHistoryPressed(now: t0.addingTimeInterval(1))
        #expect(cleared == 1 && !ui.isClearHistoryArmed(now: t0.addingTimeInterval(1)))
        // Expired arming: the next press only arms again.
        ui.clearHistoryPressed(now: t0.addingTimeInterval(10))
        ui.clearHistoryPressed(now: t0.addingTimeInterval(10 + NotchUIRules.quitConfirmWindow + 0.1))
        #expect(cleared == 1)
    }

    @Test func visibleHistoryHidesTheCurrentSetMidSet() {
        let (model, ui) = make(.guessing(tierIndex: 0))
        model.history = [entry(Date(), id: "a"), entry(Date(), id: "other")]
        #expect(ui.visibleHistory.map(\.trackID) == ["other"])
        model.state.phase = .setFailed(correctCount: 0)
        #expect(Set(ui.visibleHistory.map(\.trackID)) == ["a", "other"])
    }
}
