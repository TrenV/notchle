import AppKit
import Foundation
import Testing
import Vision
import NotchleCore
@testable import NotchleMac

/// Hard rule: during playingSnippet, guessing and wrong the title/artist are never rendered.
@Suite struct UIAnswerSecrecyTests {
    static let track = Track(id: "t1", uri: "spotify:track:t1", title: "Zanzibar Nights",
                             artists: ["Quill Ostrander", "Mabel Fitch"], durationMs: 1, previewURL: nil)

    static func state(_ phase: GamePhase) -> GameState {
        var s = GameState()
        s.listing = SourceListing(ref: SourceRef(kind: .playlist, id: "p"), name: "List", tracks: [track])
        s.currentSet = [track]
        s.phase = phase
        return s
    }

    @Test(arguments: [GamePhase.playingSnippet(tierIndex: 0), .guessing(tierIndex: 1),
                      .wrong(tierIndex: 0, verdict: Verdict(titleCorrect: false, artistCorrect: false)),
                      .idle, .loading, .setComplete(correctCount: 1), .setFailed(correctCount: 0),
                      .exhausted, .error(message: "e")])
    func noAnswerOutsideCorrectAndRevealed(_ phase: GamePhase) {
        #expect(NotchUIRules.revealedAnswer(Self.state(phase)) == nil)
    }

    @Test func answerInCorrectAndRevealed() {
        let a = NotchUIRules.revealedAnswer(Self.state(.correct(tierIndex: 0)))
        #expect(a?.title == "Zanzibar Nights")
        #expect(a?.artist == "Quill Ostrander, Mabel Fitch")
        #expect(NotchUIRules.revealedAnswer(Self.state(.revealed(verdict: nil)))?.title == "Zanzibar Nights")
    }

    @Test func guessPhaseTextNeverContainsNames() {
        let s = Self.state(.guessing(tierIndex: 0))
        let texts = [NotchUIRules.artistPlaceholder(artistCount: NotchUIRules.artistCount(s)),
                     NotchUIRules.progressText(s),
                     NotchUIRules.restartLabel(s.phase) ?? "",
                     NotchUIRules.secondsLabel(NotchUIRules.skipSeconds(s.phase, s.config) ?? 0),
                     NotchUIRules.artistHint(Verdict(titleCorrect: true, artistCorrect: false),
                                             artistCount: NotchUIRules.artistCount(s)) ?? ""]
        for t in texts {
            #expect(!t.contains("Zanzibar"))
            #expect(!t.contains("Quill"))
            #expect(!t.contains("Mabel"))
        }
        #expect(texts[0] == "Artist(s)")      // not even how many artists there are
        #expect(!texts.joined().contains("2"))
        #expect(texts[2] == "Replay snippet")
    }

    // MARK: Rendered proof: draw the real views, OCR the pixels.

    @MainActor
    static func renderedText(_ phase: GamePhase, expanded: Bool = true, quitArmed: Bool = false,
                             historyTab: Bool = false, cover: Bool = false) throws -> String {
        var scenario = UISnapshots.Scenario(name: "test", phase: phase, expanded: expanded, state: state(phase),
                                            quitArmed: quitArmed)
        scenario.historyTab = historyTab
        // A history that already holds the current track (e.g. a replayed set) plus another one.
        scenario.history = [
            HistoryEntry(date: Date().addingTimeInterval(-600), trackID: "other", title: "Harbour Lights",
                         artists: ["Cassia Moon"], listingName: "Older list", listingRef: nil, correct: true,
                         tierIndex: 0, wrongGuesses: 0, skips: 0),
            HistoryEntry(date: Date().addingTimeInterval(-60), track: track, outcome: .missed, wrongGuesses: 3,
                         skips: 0, listingName: "List", listingRef: nil,
                         artworkURL: URL(string: "https://example.invalid/t1.jpg")),
        ]
        // The model holds a cover URL in every phase here; only the views' gate keeps it off screen.
        // The image spells a word so OCR can tell whether it was drawn.
        if cover { scenario.cover = { _ in coverImage } }
        let data = try #require(UISnapshots.render(scenario))
        let image = try #require(NSBitmapImageRep(data: data)?.cgImage)
        let request = VNRecognizeTextRequest()
        request.recognitionLevel = .accurate
        request.usesLanguageCorrection = false
        try VNImageRequestHandler(cgImage: image).perform([request])
        return (request.results ?? []).compactMap { $0.topCandidates(1).first?.string }.joined(separator: " | ")
    }

    @MainActor
    @Test(arguments: [GamePhase.playingSnippet(tierIndex: 0), .guessing(tierIndex: 0),
                      .wrong(tierIndex: 1, verdict: Verdict(titleCorrect: true, artistCorrect: false))])
    func renderedGuessViewsDoNotLeak(_ phase: GamePhase) throws {
        for expanded in [true, false] {
            let text = try Self.renderedText(phase, expanded: expanded)
            // Control: OCR does read the screen (the mock menu bar says "Finder").
            #expect(text.contains("Finder"), "OCR read: \(text)")
            #expect(!text.localizedCaseInsensitiveContains("Zanzibar"), "OCR read: \(text)")
            #expect(!text.localizedCaseInsensitiveContains("Quill"), "OCR read: \(text)")
            #expect(!text.localizedCaseInsensitiveContains("Mabel"), "OCR read: \(text)")
        }
    }

    /// The armed "Quit playlist?" header is on screen (control) and the answer is not.
    @MainActor
    @Test(arguments: [GamePhase.playingSnippet(tierIndex: 0), .guessing(tierIndex: 1),
                      .wrong(tierIndex: 0, verdict: Verdict(titleCorrect: false, artistCorrect: false))])
    func armedQuitDoesNotLeak(_ phase: GamePhase) throws {
        let text = try Self.renderedText(phase, quitArmed: true)
        #expect(text.contains("Quit playlist"), "OCR read: \(text)")
        #expect(!text.localizedCaseInsensitiveContains("Zanzibar"), "OCR read: \(text)")
        #expect(!text.localizedCaseInsensitiveContains("Quill"), "OCR read: \(text)")
        #expect(!text.localizedCaseInsensitiveContains("Mabel"), "OCR read: \(text)")
    }

    /// A cover image OCR can read ("COVERART").
    static let coverImage: NSImage = NSImage(size: NSSize(width: 256, height: 256), flipped: false) { rect in
        NSColor.white.setFill()
        rect.fill()
        let text = NSAttributedString(string: "COVERART", attributes: [
            .font: NSFont.systemFont(ofSize: 44, weight: .black), .foregroundColor: NSColor.black])
        text.draw(at: NSPoint(x: 12, y: 104))
        return true
    }

    /// History tab reachable mid-set: it shows the other list (control) but never the current
    /// set's track, whose entry is in the history.
    @MainActor
    @Test(arguments: [GamePhase.playingSnippet(tierIndex: 0), .guessing(tierIndex: 1),
                      .wrong(tierIndex: 0, verdict: Verdict(titleCorrect: false, artistCorrect: false))])
    func historyTabDoesNotLeakTheCurrentSet(_ phase: GamePhase) throws {
        let text = try Self.renderedText(phase, historyTab: true)
        #expect(text.contains("Harbour Lights"), "OCR read: \(text)")
        #expect(!text.localizedCaseInsensitiveContains("Zanzibar"), "OCR read: \(text)")
        #expect(!text.localizedCaseInsensitiveContains("Quill"), "OCR read: \(text)")
        #expect(!text.localizedCaseInsensitiveContains("Mabel"), "OCR read: \(text)")
    }

    /// Positive control for the filter: once the set is over the entry does show.
    @MainActor
    @Test func historyTabShowsTheSetOnceItEnds() throws {
        let text = try Self.renderedText(.setFailed(correctCount: 0), historyTab: true)
        #expect(text.contains("Zanzibar Nights"), "OCR read: \(text)")
    }

    /// The cover never renders while guessing, although the model holds its URL.
    @MainActor
    @Test(arguments: [GamePhase.playingSnippet(tierIndex: 0), .guessing(tierIndex: 0),
                      .wrong(tierIndex: 1, verdict: Verdict(titleCorrect: true, artistCorrect: false))])
    func coverDoesNotRenderWhileGuessing(_ phase: GamePhase) throws {
        let text = try Self.renderedText(phase, cover: true)
        #expect(text.contains("Finder"), "OCR read: \(text)")
        #expect(!text.localizedCaseInsensitiveContains("COVERART"), "OCR read: \(text)")
    }

    @MainActor
    @Test(arguments: [GamePhase.correct(tierIndex: 0), .revealed(verdict: nil)])
    func coverRendersOnTheAnswerScreens(_ phase: GamePhase) throws {
        let text = try Self.renderedText(phase, cover: true)
        #expect(text.contains("COVERART"), "OCR read: \(text)")
    }

    @Test func artworkGateOnlyOpensWithTheAnswer() {
        let url = URL(string: "https://example.invalid/c.jpg")
        for phase in [GamePhase.playingSnippet(tierIndex: 0), .guessing(tierIndex: 2),
                      .wrong(tierIndex: 0, verdict: Verdict(titleCorrect: false, artistCorrect: false)),
                      .idle, .loading, .setComplete(correctCount: 1), .setFailed(correctCount: 0), .exhausted,
                      .error(message: "e")] {
            #expect(NotchUIRules.revealedArtwork(Self.state(phase), url: url) == nil, "\(phase)")
        }
        #expect(NotchUIRules.revealedArtwork(Self.state(.correct(tierIndex: 0)), url: url)?.trackID == "t1")
        #expect(NotchUIRules.revealedArtwork(Self.state(.revealed(verdict: nil)), url: url)?.url == url)
        #expect(NotchUIRules.revealedArtwork(Self.state(.correct(tierIndex: 0)), url: nil) == nil)
    }

    /// Positive control: the same pipeline does see the answer where it is allowed.
    @MainActor
    @Test func renderedCorrectViewShowsTheAnswer() throws {
        let text = try Self.renderedText(.correct(tierIndex: 0))
        #expect(text.contains("Zanzibar Nights"), "OCR read: \(text)")
        #expect(text.contains("Quill Ostrander"), "OCR read: \(text)")
    }
}
