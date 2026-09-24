import AppKit
import NotchleCore

/// How tall the expanded notch has to be so nothing in it is cut off. Pure text maths (AppKit
/// font metrics, no views), so it is unit-testable and the panel's hit area can use the same
/// number as the drawn shape.
///
/// The rule: text wraps first (answer title up to 3 lines, artists 2, History titles 2, the
/// rest as many as needed), then scales down a little (never under 11 pt), and the shape grows
/// downwards to fit. `NotchMetrics.expandedSize` stays the minimum, so short strings look as
/// before. The estimates round up: a few spare points only widen the gap above the buttons.
///
/// Answer secrecy: only the answer screens read the track. Every other screen's height depends
/// on what the player typed or on fixed copy, never on the hidden title or artists.
public enum NotchLayout {
    // MARK: Text maths

    /// Height of one line of `font`, as a text view lays it out.
    public static func lineHeight(_ font: NSFont) -> CGFloat {
        ceil(font.ascender - font.descender + font.leading)
    }

    /// Width of `text` on one line.
    public static func width(_ text: String, _ font: NSFont) -> CGFloat {
        ceil(NSAttributedString(string: text, attributes: [.font: font]).size().width)
    }

    /// Lines `text` wraps to in `width` (at least 1).
    public static func lines(_ text: String, _ font: NSFont, width: CGFloat) -> Int {
        guard !text.isEmpty, width > 0 else { return 1 }
        let rect = NSAttributedString(string: text, attributes: [.font: font])
            .boundingRect(with: CGSize(width: width, height: .greatestFiniteMagnitude),
                          options: [.usesLineFragmentOrigin, .usesFontLeading])
        return max(1, Int((ceil(rect.height) / lineHeight(font)).rounded()))
    }

    /// Height of `text` wrapped in `width`, at most `maxLines` lines (the view scales the text
    /// down to fit those, so this over-estimates, which is the safe side).
    public static func textHeight(_ text: String, _ font: NSFont, width: CGFloat, maxLines: Int? = nil) -> CGFloat {
        var n = lines(text, font, width: width)
        if let maxLines { n = min(n, maxLines) }
        return CGFloat(n) * lineHeight(font)
    }

    /// True when `text` fits in `maxLines` lines of `width` at `font` scaled by `scale`.
    public static func fits(_ text: String, _ font: NSFont, width: CGFloat, maxLines: Int, scale: CGFloat = 1) -> Bool {
        let scaled = NSFont.systemFont(ofSize: font.pointSize * scale, weight: font.weight)
        return lines(text, scaled, width: width) <= maxLines
    }

    // MARK: Fonts (mirror the views)

    enum Fonts {
        static var header: NSFont { .systemFont(ofSize: 11, weight: .semibold) }
        static var answerTitle: NSFont { .systemFont(ofSize: 19, weight: .bold) }
        static var answerArtist: NSFont { .systemFont(ofSize: 13, weight: .medium) }
        static var chipLabel: NSFont { .systemFont(ofSize: 10, weight: .semibold) }
        static var chipGuess: NSFont { .systemFont(ofSize: 12, weight: .medium) }
        static var errorTitle: NSFont { .systemFont(ofSize: 14, weight: .semibold) }
        static var errorMessage: NSFont { .systemFont(ofSize: 12, weight: .regular) }
        static var entryHeading: NSFont { .systemFont(ofSize: 15, weight: .semibold) }
        static var entrySub: NSFont { .systemFont(ofSize: 11.5, weight: .regular) }
        static var entryMessage: NSFont { .systemFont(ofSize: 11, weight: .medium) }
        static var loading: NSFont { .systemFont(ofSize: 13, weight: .medium) }
        static var setEndTitle: NSFont { .systemFont(ofSize: 16, weight: .bold) }
        static var setEndSub: NSFont { .systemFont(ofSize: 12, weight: .regular) }
        static var settingsLabel: NSFont { .systemFont(ofSize: 12, weight: .medium) }
        static var settingsHelp: NSFont { .systemFont(ofSize: 10.5, weight: .medium) }
        static var settingsFooter: NSFont { .systemFont(ofSize: 11, weight: .medium) }
        static var settingsConnected: NSFont { .systemFont(ofSize: 12, weight: .semibold) }
        static var button: NSFont { .systemFont(ofSize: 12, weight: .semibold) }
        static var buttonHint: NSFont { .systemFont(ofSize: 10, weight: .semibold) }
        static var historyTitle: NSFont { .systemFont(ofSize: 12, weight: .semibold) }
        static var historyArtist: NSFont { .systemFont(ofSize: 11, weight: .medium) }
    }

    /// Scale floors: never below ~0.75, and never below 11 pt.
    static let answerTitleMinScale: CGFloat = 0.8     // 19 → 15.2 pt
    static let answerArtistMinScale: CGFloat = 0.85   // 13 → 11 pt
    static let historyTitleMinScale: CGFloat = 0.92   // 12 → 11 pt
    static let chipGuessMinScale: CGFloat = 0.92      // 12 → 11 pt
    static let answerTitleLines = 3
    static let answerArtistLines = 2
    static let historyTitleLines = 2
    static let historyArtistLines = 2
    static let chipGuessLines = 3

    // MARK: Widths

    /// Horizontal inset of the phase content from the shape's side (ear + 18).
    static func contentInset(hasNotch: Bool) -> CGFloat {
        NotchSilhouette.earInset(hasNotch: hasNotch, expanded: true) + 18
    }

    /// Width the phase content lays out in.
    public static func contentWidth(hasNotch: Bool) -> CGFloat {
        NotchMetrics.expandedSize.width - 2 * contentInset(hasNotch: hasNotch)
    }

    /// Width of the header row (ear + 16 each side).
    static func headerWidth(hasNotch: Bool) -> CGFloat {
        NotchMetrics.expandedSize.width - 2 * (NotchSilhouette.earInset(hasNotch: hasNotch, expanded: true) + 16)
    }

    static let coverSize: CGFloat = 64
    static let coverGap: CGFloat = 12

    /// Width the answer's title and artists wrap in.
    public static func answerTextWidth(hasNotch: Bool, hasCover: Bool) -> CGFloat {
        contentWidth(hasNotch: hasNotch) - (hasCover ? coverSize + coverGap : 0)
    }

    /// One verdict chip's guess text width: half the row, minus padding, icon and gap.
    static func chipTextWidth(hasNotch: Bool) -> CGFloat {
        (contentWidth(hasNotch: hasNotch) - 8) / 2 - 20 - 17 - 7
    }

    // MARK: Header

    /// Room the header leaves for the listing name, between the tab switch and the (hardware
    /// notch) gap, with the right wing's widest content (the armed "Quit playlist?" or quit +
    /// progress + gear) reserved.
    public static func headerNameRoom(hasNotch: Bool, notchWidth: CGFloat, progress: String?) -> CGFloat {
        let tabSwitch: CGFloat = 2 * 22 + 1 + 4
        let gap = hasNotch ? notchWidth + 12 : 12
        let normal = 20 + 6 + (progress.map { width($0, Fonts.header) + 6 } ?? 0) + 20
        let armed = width("Quit playlist?", NSFont.systemFont(ofSize: 11, weight: .bold)) + 18
        let spacing: CGFloat = 6 * 3
        return headerWidth(hasNotch: hasNotch) - tabSwitch - gap - max(normal, armed) - spacing - 4
    }

    /// Does the header title fit in its wing on one line? If not it moves to a caption row
    /// under the header, where it can wrap.
    public static func headerTitleFits(_ title: String, hasNotch: Bool, notchWidth: CGFloat, progress: String?) -> Bool {
        width(title, Fonts.header) <= headerNameRoom(hasNotch: hasNotch, notchWidth: notchWidth, progress: progress)
    }

    static let captionGap: CGFloat = 2

    /// Height of the caption row (0 when the title fits in the header).
    public static func captionHeight(_ title: String, hasNotch: Bool, notchWidth: CGFloat, progress: String?) -> CGFloat {
        if headerTitleFits(title, hasNotch: hasNotch, notchWidth: notchWidth, progress: progress) { return 0 }
        return textHeight(title, Fonts.header, width: contentWidth(hasNotch: hasNotch)) + captionGap
    }

    // MARK: Phase bodies (heights of the content between padding-top 8 and padding-bottom 16)

    static let buttonRow: CGFloat = 26

    public static func answerBody(title: String, artist: String, hasNotch: Bool, hasCover: Bool) -> CGFloat {
        let w = answerTextWidth(hasNotch: hasNotch, hasCover: hasCover)
        let text = textHeight(title, Fonts.answerTitle, width: w, maxLines: answerTitleLines) + 2
            + textHeight(artist, Fonts.answerArtist, width: w, maxLines: answerArtistLines)
        let block = max(hasCover ? coverSize : 0, text)
        return 18 + 8 + block + 6 + buttonRow
    }

    public static func chipHeight(guess: String, hasNotch: Bool) -> CGFloat {
        let text = lineHeight(Fonts.chipLabel) + 1
            + textHeight(guess.isEmpty ? "–" : guess, Fonts.chipGuess, width: chipTextWidth(hasNotch: hasNotch),
                         maxLines: chipGuessLines)
        return max(40, text + 2 * 7)
    }

    public static func wrongBody(titleGuess: String, artistGuess: String, hasNotch: Bool) -> CGFloat {
        let chip = max(chipHeight(guess: titleGuess, hasNotch: hasNotch), chipHeight(guess: artistGuess, hasNotch: hasNotch))
        return 22 + 12 + chip + 8 + buttonRow
    }

    public static func errorBody(message: String, hasNotch: Bool) -> CGFloat {
        let w = contentWidth(hasNotch: hasNotch) - 24 - 10
        let text = lineHeight(Fonts.errorTitle) + 3 + textHeight(message, Fonts.errorMessage, width: w)
        return max(22, text) + 8 + buttonRow
    }

    public static func sourceEntryBody(heading: String, subheading: String, message: String?, hasNotch: Bool) -> CGFloat {
        let w = contentWidth(hasNotch: hasNotch)
        let msg = message.map { textHeight($0, Fonts.entryMessage, width: w - 16) } ?? 0
        return textHeight(heading, Fonts.entryHeading, width: w) + 3
            + textHeight(subheading, Fonts.entrySub, width: w) + 10 + 30 + 8 + max(16, msg)
    }

    public static func loadingBody(_ text: String, hasNotch: Bool) -> CGFloat {
        22 + 12 + textHeight(text, Fonts.loading, width: contentWidth(hasNotch: hasNotch))
    }

    public static func setEndBody(subtitle: String, hasNotch: Bool) -> CGFloat {
        let w = contentWidth(hasNotch: hasNotch) - 62 - 16
        let text = lineHeight(Fonts.setEndTitle) + 4 + textHeight(subtitle, Fonts.setEndSub, width: w)
        return max(62, text) + 8 + buttonRow
    }

    /// Width of a `KeyHintLabel` button (title + optional hint + padding).
    static func buttonWidth(_ title: String, hint: String?, quiet: Bool = false) -> CGFloat {
        width(title, Fonts.button) + (hint.map { 5 + width($0, Fonts.buttonHint) } ?? 0) + (quiet ? 0 : 24)
    }

    /// Player choice buttons: two per row.
    static let choiceColumns = 2
    static let choiceRowHeight: CGFloat = 26
    static let choiceSpacing: CGFloat = 6

    static func choiceWidth(hasNotch: Bool) -> CGFloat {
        (contentWidth(hasNotch: hasNotch) - choiceSpacing * CGFloat(choiceColumns - 1)) / CGFloat(choiceColumns)
    }

    /// Settings: "Play songs with", the player choices, the Connect row or the snippets row,
    /// then "Now using …" beside Quit Notchle + Done.
    public static func settingsBody(controls: SpotifyConnectControls, showsSnippets: Bool? = nil,
                                    playerName: String, hasNotch: Bool) -> CGFloat {
        let w = contentWidth(hasNotch: hasNotch)
        let rows = (NotchUIRules.playerChoices.count + choiceColumns - 1) / choiceColumns
        var h = lineHeight(Fonts.settingsLabel) + 5
            + CGFloat(rows) * choiceRowHeight + CGFloat(rows - 1) * choiceSpacing
        switch controls {
        case .hidden:
            break
        case .signIn(_, let busy, let message):
            let help = busy ? "Finish the sign-in in your browser…" : (message ?? NotchUIRules.spotifyConnectHelp)
            h += 7 + 26 + 4 + textHeight(help, Fonts.settingsHelp, width: w)
        case .connected(let name):
            let room = w - 11 - 8 - buttonWidth("Sign out", hint: nil) - 8
            h += 7 + max(26, textHeight("Connected as \(name)", Fonts.settingsConnected, width: room))
        }
        if showsSnippets ?? NotchUIRules.showsSnippetsRow(controls) { h += 8 + 24 }
        let footerRoom = w - buttonWidth("Quit Notchle", hint: nil, quiet: true) - 8
            - buttonWidth("Done", hint: "esc") - 8
        let footer = max(buttonRow, textHeight(settingsFooter(playerName), Fonts.settingsFooter, width: footerRoom))
        return h + 6 + footer
    }

    static func settingsFooter(_ playerName: String) -> String {
        playerName.isEmpty ? " " : "Now using \(playerName)"
    }

    // MARK: The shape

    /// Header + caption + padding around a body of `body` points.
    static func shapeHeight(body: CGFloat, caption: CGFloat, notchHeight: CGFloat) -> CGFloat {
        max(notchHeight, 28) + caption + 8 + body + 16
    }

    /// The expanded height for the UI state (History keeps its fixed, scrolling size).
    @MainActor
    public static func expandedHeight(_ ui: NotchUIState, _ g: NotchGeometry) -> CGFloat {
        if ui.usesTallLayout { return NotchMetrics.historySize.height }
        let state = ui.model.state
        let n = g.hasNotch
        let caption = captionHeight(headerTitle(ui), hasNotch: n, notchWidth: g.notchSize.width,
                                    progress: headerProgress(state))
        let body: CGFloat
        if ui.showingSettings {
            let connect = ui.model.spotifyConnect
            let controls = NotchUIRules.spotifyConnectControls(
                mode: ui.model.settings.playerMode, status: connect?.status, clientID: connect?.clientID ?? "")
            body = settingsBody(controls: connect == nil ? .hidden : controls,
                                showsSnippets: NotchUIRules.showsSnippetsRow(controls), playerName: ui.model.playerName, hasNotch: n)
        } else {
            body = phaseBody(ui, hasNotch: n)
        }
        let h = shapeHeight(body: body, caption: caption, notchHeight: g.notchSize.height)
        return min(NotchMetrics.panelSize.height, max(NotchMetrics.expandedSize.height, ceil(h)))
    }

    @MainActor
    static func phaseBody(_ ui: NotchUIState, hasNotch n: Bool) -> CGFloat {
        let state = ui.model.state
        switch state.phase {
        case .idle:
            return sourceEntryBody(heading: SourceEntryCopy.idleHeading, subheading: SourceEntryCopy.idleSub,
                                   message: ui.urlMessage, hasNotch: n)
        case .exhausted:
            return sourceEntryBody(heading: SourceEntryCopy.exhaustedHeading,
                                   subheading: SourceEntryCopy.exhaustedSub(state.listing?.name),
                                   message: ui.urlMessage, hasNotch: n)
        case .loading:
            return loadingBody(SourceEntryCopy.loading(state.listing?.name), hasNotch: n)
        case .playingSnippet, .guessing:
            return 18 + 9 + 4 + 12 + 30 + 8 + buttonRow
        case .wrong:
            return wrongBody(titleGuess: ui.titleText, artistGuess: ui.artistText, hasNotch: n)
        case .correct, .revealed:
            let answer = NotchUIRules.revealedAnswer(state)
            let cover = NotchUIRules.revealedArtwork(state, url: ui.model.currentArtworkURL) != nil
            return answerBody(title: answer?.title ?? "–", artist: answer?.artist ?? "", hasNotch: n, hasCover: cover)
        case .setComplete(let c), .setFailed(let c):
            let complete = if case .setComplete = state.phase { true } else { false }
            let total = max(state.currentSet.count, c)
            return setEndBody(subtitle: SourceEntryCopy.setEndSubtitle(
                complete: complete, correct: c, total: total, availableNew: GameEngine.availableNewCount(state)),
                              hasNotch: n)
        case .error(let message):
            return errorBody(message: message, hasNotch: n)
        }
    }

    /// The header's title: "History", the listing's name, or "Notchle".
    @MainActor
    static func headerTitle(_ ui: NotchUIState) -> String {
        ui.tab == .history && !ui.showingSettings ? "History" : ui.model.state.listing?.name ?? "Notchle"
    }

    static func headerProgress(_ state: GameState) -> String? {
        state.currentSet.isEmpty ? nil : NotchUIRules.progressText(state)
    }

    /// The expanded (or collapsed) shape for the UI state: the one the view draws and the
    /// panel hit-tests.
    @MainActor
    public static func shapeSize(_ ui: NotchUIState, _ g: NotchGeometry) -> CGSize {
        guard ui.isExpanded else { return NotchMetrics.collapsedSize(g) }
        return CGSize(width: NotchMetrics.expandedSize.width, height: expandedHeight(ui, g))
    }

    /// The shape in screen coordinates, top-centred on the anchor.
    @MainActor
    public static func shapeFrame(_ ui: NotchUIState, _ g: NotchGeometry) -> CGRect {
        let s = shapeSize(ui, g)
        return CGRect(x: g.centerX - s.width / 2, y: g.anchorTop - s.height, width: s.width, height: s.height)
    }

    /// The area that takes the mouse: the drawn shape plus `NotchMetrics.hoverSlack`.
    @MainActor
    public static func hoverFrame(_ ui: NotchUIState, _ g: NotchGeometry) -> CGRect {
        shapeFrame(ui, g).insetBy(dx: -NotchMetrics.hoverSlack, dy: -NotchMetrics.hoverSlack)
    }
}

/// Fixed copy shared by the views and `NotchLayout`, so both measure the same strings.
enum SourceEntryCopy {
    static let idleHeading = "Paste a Spotify link"
    static let idleSub = "A playlist, album or artist. You get 5 seconds per song."
    static let exhaustedHeading = "You've heard them all"
    static func exhaustedSub(_ name: String?) -> String {
        "Every song in \(name ?? "this listing") has been played. Try another link."
    }
    static func loading(_ name: String?) -> String { name.map { "Loading \($0)…" } ?? "Loading songs…" }
    static func setEndSubtitle(complete: Bool, correct: Int, total: Int, availableNew: Int) -> String {
        if availableNew == 0 { return "No new songs left in this playlist · paste another link" }
        return complete ? "All \(total) cleared. On to new songs?"
                        : "\(total - correct) to practise. Keep them, replay all, or start fresh."
    }
}

private extension NSFont {
    var weight: NSFont.Weight {
        let traits = fontDescriptor.object(forKey: .traits) as? [NSFontDescriptor.TraitKey: Any]
        return NSFont.Weight((traits?[.weight] as? CGFloat) ?? 0)
    }
}
