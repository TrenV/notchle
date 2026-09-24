import AppKit
import Foundation
import Testing
import Vision
import NotchleCore
@testable import NotchleMac

/// "Make everything fit": no text in the notch is cut off with "…" or clipped. Long strings
/// wrap, then shrink a little (never under 11 pt), and the expanded notch grows to fit them.
@MainActor
@Suite struct UILayoutTests {
    typealias Long = UISnapshots.LongText
    let notch = NotchGeometry(hardwareNotch: CGRect(x: 0, y: 0, width: 185, height: 32),
                              notchSize: CGSize(width: 185, height: 32), centerX: 500, anchorTop: 1000)
    let pill = NotchGeometry(hardwareNotch: nil, notchSize: NotchGeometry.fallbackNotchSize,
                             centerX: 500, anchorTop: 970)

    static func ui(_ state: GameState) -> NotchUIState {
        let ui = NotchUIState(model: NotchViewModel(state: state))
        ui.isExpanded = true
        return ui
    }

    // MARK: Pure layout

    @Test func shortStringsKeepTheBaseSize() {
        for hasNotch in [true, false] {
            for cover in [true, false] {
                let body = NotchLayout.answerBody(title: "Lemon Skies", artist: "Sunday Club", hasNotch: hasNotch,
                                                  hasCover: cover)
                let h = NotchLayout.shapeHeight(body: body, caption: 0, notchHeight: 32)
                #expect(h <= NotchMetrics.expandedSize.height, "cover \(cover) notch \(hasNotch): \(h)")
            }
        }
    }

    @Test func longAnswerFitsItsLineBudgetAtAReadableSize() {
        for hasNotch in [true, false] {
            for cover in [true, false] {
                let w = NotchLayout.answerTextWidth(hasNotch: hasNotch, hasCover: cover)
                let title = NotchLayout.Fonts.answerTitle
                let artist = NotchLayout.Fonts.answerArtist
                // Control: the long title really is longer than one line.
                #expect(NotchLayout.lines(Long.title, title, width: w) > 1)
                #expect(NotchLayout.fits(Long.title, title, width: w, maxLines: NotchLayout.answerTitleLines,
                                         scale: NotchLayout.answerTitleMinScale))
                #expect(NotchLayout.fits(Long.artists.joined(separator: ", "), artist, width: w,
                                         maxLines: NotchLayout.answerArtistLines, scale: NotchLayout.answerArtistMinScale))
            }
        }
        #expect(19 * NotchLayout.answerTitleMinScale >= 11)
        #expect(13 * NotchLayout.answerArtistMinScale >= 11)
        #expect(12 * NotchLayout.historyTitleMinScale >= 11)
        #expect(12 * NotchLayout.chipGuessMinScale >= 11)
    }

    @Test func theShapeGrowsForALongAnswerAndStaysInsideThePanel() {
        let ui = Self.ui(Long.state(.correct(tierIndex: 0)))
        let short = Self.ui(UISnapshots.sampleState(.correct(tierIndex: 0)))
        for g in [notch, pill] {
            let h = NotchLayout.expandedHeight(ui, g)
            #expect(h > NotchLayout.expandedHeight(short, g) + 30, "\(h)")
            #expect(h <= NotchMetrics.panelSize.height)
            #expect(NotchLayout.shapeSize(ui, g) == CGSize(width: NotchMetrics.expandedSize.width, height: h))
        }
    }

    @Test func longErrorAndGuessesGrowTheShape() {
        let err = Self.ui(Long.state(.error(message: Long.error)))
        #expect(NotchLayout.expandedHeight(err, notch) > NotchMetrics.expandedSize.height)
        let wrong = Self.ui(Long.state(.wrong(tierIndex: 0, verdict: Verdict(titleCorrect: false, artistCorrect: false))))
        let before = NotchLayout.expandedHeight(wrong, notch)
        wrong.titleText = Long.titleGuess
        #expect(NotchLayout.chipHeight(guess: Long.titleGuess, hasNotch: true) > 40)
        #expect(NotchLayout.expandedHeight(wrong, notch) > before)
    }

    @Test func longPlaylistNameMovesOutOfTheHeaderWing() {
        #expect(!NotchLayout.headerTitleFits(Long.playlist, hasNotch: true, notchWidth: 185, progress: "7/20"))
        #expect(!NotchLayout.headerTitleFits(Long.playlist, hasNotch: false, notchWidth: 185, progress: "7/20"))
        #expect(NotchLayout.captionHeight(Long.playlist, hasNotch: true, notchWidth: 185, progress: "7/20") > 0)
        #expect(NotchLayout.headerTitleFits("History", hasNotch: true, notchWidth: 185, progress: nil))
        #expect(NotchLayout.headerTitleFits("Notchle", hasNotch: true, notchWidth: 185, progress: nil))
    }

    @Test func playerChoiceLabelsFitOnOneLine() {
        for hasNotch in [true, false] {
            let room = NotchLayout.choiceWidth(hasNotch: hasNotch) - 20
            for choice in NotchUIRules.playerChoices {
                #expect(NotchLayout.fits(choice.label, NSFont.systemFont(ofSize: 12, weight: .semibold),
                                         width: room, maxLines: 1, scale: 0.92), "\(choice.label)")
            }
        }
    }

    @Test func historyTitleFitsTwoLines() {
        let w = NotchLayout.contentWidth(hasNotch: true) - 28 - 8
        #expect(NotchLayout.fits(Long.title, NotchLayout.Fonts.historyTitle, width: w,
                                 maxLines: NotchLayout.historyTitleLines, scale: NotchLayout.historyTitleMinScale))
        #expect(NotchLayout.fits(Long.artists.joined(separator: ", "), NotchLayout.Fonts.historyArtist, width: w,
                                 maxLines: NotchLayout.historyArtistLines))
    }

    /// Secrecy: while guessing, the height must not depend on the hidden track (a longer title
    /// must not make the notch taller before it is revealed).
    @Test func guessHeightDoesNotDependOnTheHiddenTrack() {
        for phase in [GamePhase.playingSnippet(tierIndex: 0), .guessing(tierIndex: 1),
                      .wrong(tierIndex: 0, verdict: Verdict(titleCorrect: false, artistCorrect: false))] {
            var short = Long.state(phase)
            short.currentSet[6] = Track(id: "s", uri: "spotify:track:s", title: "Hi", artists: ["A"], durationMs: 1,
                                        previewURL: nil)
            for g in [notch, pill] {
                #expect(NotchLayout.expandedHeight(Self.ui(Long.state(phase)), g)
                        == NotchLayout.expandedHeight(Self.ui(short), g), "\(phase)")
            }
        }
    }

    /// The panel takes the mouse exactly over the drawn shape (+ slack), also when it grows.
    @Test func hitAreaFollowsTheGrownShape() {
        let ui = Self.ui(Long.state(.correct(tierIndex: 0)))
        for g in [notch, pill] {
            let shape = NotchLayout.shapeFrame(ui, g)
            #expect(shape.maxY == g.anchorTop)
            #expect(shape.height == NotchLayout.expandedHeight(ui, g))
            #expect(NotchLayout.hoverFrame(ui, g) == shape.insetBy(dx: -NotchMetrics.hoverSlack, dy: -NotchMetrics.hoverSlack))
            let justAboveBottom = CGPoint(x: g.centerX, y: shape.minY + 2)
            #expect(NotchLayout.hoverFrame(ui, g).contains(justAboveBottom))
            // Control: the old fixed shape would not have covered that point.
            #expect(!NotchMetrics.hoverFrame(g, expanded: true).contains(justAboveBottom))
        }
    }

    @Test func collapsedPillStaysCompact() {
        let ui = Self.ui(Long.state(.correct(tierIndex: 0)))
        ui.isExpanded = false
        #expect(NotchLayout.shapeSize(ui, notch) == NotchMetrics.collapsedSize(notch))
    }

    // MARK: Rendered proof: OCR the pixels, the whole long title must be there

    static func ocr(_ scenario: UISnapshots.Scenario) throws -> String {
        let data = try #require(UISnapshots.render(scenario))
        let image = try #require(NSBitmapImageRep(data: data)?.cgImage)
        let request = VNRecognizeTextRequest()
        request.recognitionLevel = .accurate
        request.usesLanguageCorrection = false
        try VNImageRequestHandler(cgImage: image).perform([request])
        return (request.results ?? []).compactMap { $0.topCandidates(1).first?.string }.joined(separator: " ")
    }

    /// Whitespace-collapsed, so text wrapped over lines reads as one run. Folds what OCR
    /// cannot tell apart (O/0, ë/e), on both sides of the comparison.
    static func flat(_ s: String) -> String {
        s.split(whereSeparator: \.isWhitespace).joined(separator: " ")
            .folding(options: .diacriticInsensitive, locale: nil)
            .replacingOccurrences(of: "0", with: "O")
    }

    static func expectFullyReadable(_ text: String, _ needles: [String]) {
        let t = flat(text)
        #expect(t.contains("Finder"), "OCR read: \(t)")          // control: OCR works
        // A cut-off string ends in an ellipsis right after a letter ("Extended V…"). Dots after a
        // glyph are OCR reading the animated equaliser, not truncation.
        #expect(t.range(of: #"[\p{L}\p{N}] ?(…|\.\.\.)"#, options: .regularExpression) == nil, "truncated: \(t)")
        for n in needles { #expect(t.contains(flat(n)), "missing \"\(n)\" in: \(t)") }
    }

    @Test(arguments: [false, true])
    func longAnswerRendersInFull(cover: Bool) throws {
        var sc = UISnapshots.Scenario(name: "t", phase: .correct(tierIndex: 0), state: Long.state(.correct(tierIndex: 0)))
        if cover { sc.cover = { DemoHistory.cover(for: $0) } }
        Self.expectFullyReadable(try Self.ocr(sc), [Long.title, Long.artists.joined(separator: ", "), Long.playlist, "Next"])
    }

    @Test func longRevealedRendersInFullInThePill() throws {
        let sc = UISnapshots.Scenario(name: "t", phase: .revealed(verdict: nil), hasNotch: false,
                                      state: Long.state(.revealed(verdict: nil)))
        Self.expectFullyReadable(try Self.ocr(sc), [Long.title, Long.artists.joined(separator: ", "), Long.playlist, "Next"])
    }

    @Test func longHistoryRowRendersInFull() throws {
        var sc = UISnapshots.Scenario(name: "t", phase: .idle)
        sc.historyTab = true
        sc.history = Long.history()
        Self.expectFullyReadable(try Self.ocr(sc), [Long.title, Long.artists.joined(separator: ", "), "Road Trip Playlist", "5m ago", "missed"])
    }

    @Test func longGuessesAndErrorRenderInFull() throws {
        let wrong = GamePhase.wrong(tierIndex: 0, verdict: Verdict(titleCorrect: false, artistCorrect: false))
        let sc = UISnapshots.Scenario(name: "t", phase: wrong, title: Long.titleGuess, artist: Long.artistGuess,
                                      state: Long.state(wrong))
        Self.expectFullyReadable(try Self.ocr(sc), ["Version London", "featuring Peso Pluma", "Retry"])
        let err = UISnapshots.Scenario(name: "t", phase: .error(message: Long.error),
                                       state: Long.state(.error(message: Long.error)))
        Self.expectFullyReadable(try Self.ocr(err), ["try the next song", "Skip"])
    }

    @Test func settingsRenderInFull() throws {
        var sc = UISnapshots.Scenario(name: "t", phase: .guessing(tierIndex: 0), settings: true)
        sc.connect = .failed(Long.error)
        sc.clientID = "0123456789abcdef0123456789abcdef"
        Self.expectFullyReadable(try Self.ocr(sc), ["Spotify (no window, Premium)", "Apple Music (experimental)",
                                                    "try the next song", "Done"])
    }
}
