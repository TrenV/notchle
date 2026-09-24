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
        #expect(texts[0] == "2 artists, any order")
        #expect(texts[2] == "Replay snippet")
    }

    // MARK: Rendered proof: draw the real views, OCR the pixels.

    @MainActor
    static func renderedText(_ phase: GamePhase, expanded: Bool = true) throws -> String {
        let scenario = UISnapshots.Scenario(name: "test", phase: phase, expanded: expanded, state: state(phase))
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

    /// Positive control: the same pipeline does see the answer where it is allowed.
    @MainActor
    @Test func renderedCorrectViewShowsTheAnswer() throws {
        let text = try Self.renderedText(.correct(tierIndex: 0))
        #expect(text.contains("Zanzibar Nights"), "OCR read: \(text)")
        #expect(text.contains("Quill Ostrander"), "OCR read: \(text)")
    }
}
