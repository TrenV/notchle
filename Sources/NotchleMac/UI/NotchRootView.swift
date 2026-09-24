import SwiftUI
import NotchleCore

/// Root of the panel: the black notch shape (collapsed or expanded) hanging from the top
/// centre, and the confetti layer over the whole panel.
public struct NotchRootView: View {
    @Bindable var ui: NotchUIState
    let geometry: NotchGeometry
    @Environment(\.accessibilityReduceMotion) private var systemReduceMotion

    public init(ui: NotchUIState, geometry: NotchGeometry) {
        self.ui = ui
        self.geometry = geometry
    }

    private var reduceMotion: Bool { ui.reduceMotionOverride ?? systemReduceMotion }

    public var body: some View {
        let expanded = ui.isExpanded
        let size = NotchMetrics.shapeSize(geometry, expanded: expanded)
        let silhouette = NotchSilhouette(hasNotch: geometry.hasNotch, expanded: expanded)

        ZStack(alignment: .top) {
            if reduceMotion {
                CelebrationGlow(start: ui.celebrationStart, silhouette: silhouette)
                    .frame(width: size.width, height: size.height)
            }
            silhouette
                .fill(Color.black)
                .frame(width: size.width, height: size.height)
                .overlay(alignment: .top) {
                    ZStack(alignment: .top) {
                        if expanded {
                            ExpandedContent(ui: ui, geometry: geometry)
                                .frame(width: NotchMetrics.expandedSize.width,
                                       height: NotchMetrics.expandedSize.height, alignment: .top)
                                .transition(.opacity.combined(with: .scale(scale: 0.94, anchor: .top)))
                        } else {
                            CollapsedContent(ui: ui, geometry: geometry)
                                .frame(width: NotchMetrics.collapsedSize(geometry).width,
                                       height: NotchMetrics.collapsedSize(geometry).height)
                                .transition(.opacity)
                        }
                    }
                    .frame(width: size.width, height: size.height, alignment: .top)
                    .clipShape(silhouette)
                }
                .contentShape(silhouette)
            if !reduceMotion {
                ConfettiView(start: ui.celebrationStart, seed: ui.celebrationSeed,
                             origin: CGPoint(x: NotchMetrics.panelSize.width / 2, y: geometry.notchSize.height))
                    .allowsHitTesting(false)
            }
        }
        .frame(width: NotchMetrics.panelSize.width, height: NotchMetrics.panelSize.height, alignment: .top)
        .animation(reduceMotion ? .easeInOut(duration: 0.18) : .spring(response: 0.42, dampingFraction: 0.8),
                   value: expanded)
        .environment(\.colorScheme, .dark)
    }
}

// MARK: - Collapsed

struct CollapsedContent: View {
    let ui: NotchUIState
    let geometry: NotchGeometry

    var body: some View {
        let indicator = ui.collapsedIndicator()
        HStack(spacing: 0) {
            CollapsedGlyphView(glyph: indicator.glyph, snippetStart: ui.snippetStart,
                               snippetSeconds: snippetSeconds(indicator.glyph), phaseStart: ui.phaseStartedAt)
                .frame(width: wingContent)
            Spacer(minLength: 0)
            rightInfo(indicator)
                .frame(width: wingContent)
        }
        .padding(.horizontal, ear)
        .accessibilityElement(children: .combine)
    }

    /// Each wing's content area: the wing minus the ear, so nothing touches the curved edge.
    private var ear: CGFloat { NotchSilhouette.earInset(hasNotch: geometry.hasNotch, expanded: false) }
    private var wingContent: CGFloat { NotchMetrics.collapsedWing - ear }

    private func snippetSeconds(_ glyph: NotchUIRules.CollapsedGlyph) -> Double {
        if case .snippet(let t) = glyph { return NotchUIRules.seconds(ofTier: t, ui.model.state.config) }
        return 0
    }

    @ViewBuilder
    private func rightInfo(_ indicator: NotchUIRules.CollapsedIndicator) -> some View {
        if let text = indicator.text {
            Text(text)
                .font(.system(size: 11.5, weight: indicator.emphasized ? .heavy : .semibold,
                              design: .rounded))
                .monospacedDigit()
                .foregroundStyle(indicator.emphasized ? NotchPalette.green : NotchPalette.primaryText.opacity(0.85))
                .shadow(color: indicator.emphasized ? NotchPalette.green.opacity(0.6) : .clear, radius: 4)
                .lineLimit(1)
                .minimumScaleFactor(0.7)
                .animation(.spring(response: 0.3, dampingFraction: 0.6), value: indicator.emphasized)
        } else {
            NotchSpinner(size: 12, lineWidth: 2)
        }
    }
}

/// Left wing of the collapsed pill. Tells the state at a glance; never the answer.
struct CollapsedGlyphView: View {
    let glyph: NotchUIRules.CollapsedGlyph
    let snippetStart: Date?
    let snippetSeconds: Double
    let phaseStart: Date
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        Group {
            switch glyph {
            case .note:
                Image(systemName: "music.note")
                    .font(.system(size: 13, weight: .bold))
                    .foregroundStyle(NotchPalette.green)
            case .snippet:
                SnippetRing(start: snippetStart, seconds: snippetSeconds, pulse: !reduceMotion)
            case .awaitingGuess:
                AwaitingGuessGlyph(pulse: !reduceMotion)
            case .correctFlash:
                Image(systemName: "checkmark.circle.fill")
                    .font(.system(size: 15, weight: .bold))
                    .foregroundStyle(NotchPalette.green)
                    .shadow(color: NotchPalette.green.opacity(0.7), radius: 5)
                    .transition(.scale(scale: 0.3).combined(with: .opacity))
            case .playing:
                EqualizerGlyph(playing: true, color: NotchPalette.secondaryText)
            case .setComplete:
                Image(systemName: "trophy.fill")
                    .font(.system(size: 12, weight: .bold))
                    .foregroundStyle(Color(red: 1, green: 0.8, blue: 0.25))
            case .setFailed:
                Image(systemName: "arrow.counterclockwise")
                    .font(.system(size: 12, weight: .bold))
                    .foregroundStyle(NotchPalette.primaryText.opacity(0.8))
            case .warning:
                Image(systemName: "exclamationmark.triangle.fill")
                    .font(.system(size: 12, weight: .semibold))
                    .foregroundStyle(.yellow)
            }
        }
        .animation(.spring(response: 0.3, dampingFraction: 0.55), value: glyph)
        .accessibilityLabel(accessibilityText)
    }

    private var accessibilityText: String {
        switch glyph {
        case .note: "Notchle"
        case .snippet: "Snippet playing"
        case .awaitingGuess: "Waiting for your guess"
        case .correctFlash: "Correct"
        case .playing: "Song playing"
        case .setComplete: "Set complete"
        case .setFailed: "Set over"
        case .warning: "Error"
        }
    }
}

/// Small ring filling over the snippet's seconds, with a soft pulse in the middle.
struct SnippetRing: View {
    let start: Date?
    let seconds: Double
    var pulse: Bool

    var body: some View {
        TimelineView(.animation(minimumInterval: 1.0 / 30)) { context in
            let elapsed = start.map { context.date.timeIntervalSince($0) } ?? 0
            let fraction = seconds > 0 ? min(1, max(0, elapsed / seconds)) : 0
            let beat = pulse ? 0.5 + 0.5 * sin(elapsed * 2 * .pi * 1.6) : 1
            ZStack {
                Circle().stroke(Color.white.opacity(0.15), lineWidth: 2.2)
                Circle()
                    .trim(from: 0, to: fraction)
                    .stroke(NotchPalette.green, style: StrokeStyle(lineWidth: 2.2, lineCap: .round))
                    .rotationEffect(.degrees(-90))
                Circle()
                    .fill(NotchPalette.green)
                    .frame(width: 5, height: 5)
                    .scaleEffect(0.7 + 0.5 * beat)
                    .opacity(0.55 + 0.45 * beat)
            }
            .frame(width: 17, height: 17)
        }
    }
}

/// "?" with a small attention dot: the game waits for a guess.
struct AwaitingGuessGlyph: View {
    var pulse: Bool

    var body: some View {
        TimelineView(.animation(minimumInterval: 1.0 / 30, paused: !pulse)) { context in
            let t = context.date.timeIntervalSinceReferenceDate
            let beat = pulse ? 0.5 + 0.5 * sin(t * 2 * .pi * 0.9) : 1
            ZStack(alignment: .topTrailing) {
                Text("?")
                    .font(.system(size: 13, weight: .heavy, design: .rounded))
                    .foregroundStyle(Color.black)
                    .frame(width: 17, height: 17)
                    .background(Circle().fill(Color.white.opacity(0.92)))
                Circle()
                    .fill(Color(red: 1, green: 0.6, blue: 0.2))
                    .frame(width: 6, height: 6)
                    .overlay(Circle().stroke(Color.black, lineWidth: 1.2))
                    .opacity(0.5 + 0.5 * beat)
                    .offset(x: 2.5, y: -2)
            }
        }
    }
}

// MARK: - Expanded

struct ExpandedContent: View {
    @Bindable var ui: NotchUIState
    let geometry: NotchGeometry
    @FocusState private var focus: UIField?

    var body: some View {
        let state = ui.model.state
        let inset = NotchSilhouette.earInset(hasNotch: geometry.hasNotch, expanded: true)
        VStack(spacing: 0) {
            header(state)
                .frame(height: max(geometry.notchSize.height, 28))
                .padding(.horizontal, inset + 16)
            Group {
                if ui.showingSettings {
                    NotchSettingsView(ui: ui)
                } else {
                    PhaseContent(ui: ui, focus: $focus)
                }
            }
            .padding(.horizontal, inset + 18)
            .padding(.top, 8)
            .padding(.bottom, 16)
            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
        }
        .onAppear { applyRequestedFocus() }
        .onChange(of: ui.focusToken) { applyRequestedFocus() }
        .onChange(of: ui.model.state.phase) { applyRequestedFocusSoon() }
        .onChange(of: focus) { ui.focusedField = focus }
    }

    private func applyRequestedFocus() {
        focus = ui.requestedFocus
        // The field may only exist after this layout pass (new phase view); try again then.
        applyRequestedFocusSoon()
    }

    private func applyRequestedFocusSoon() {
        DispatchQueue.main.async { focus = ui.requestedFocus }
    }

    @ViewBuilder
    private func header(_ state: GameState) -> some View {
        HStack(spacing: 6) {
            Image(systemName: "music.note")
                .font(.system(size: 11, weight: .bold))
                .foregroundStyle(NotchPalette.green)
            Text(state.listing?.name ?? "Notchle")
                .font(.system(size: 11, weight: .semibold))
                .foregroundStyle(NotchPalette.secondaryText)
                .lineLimit(1)
                .truncationMode(.tail)
            // Keep the middle clear: that is where the camera housing sits.
            Spacer(minLength: geometry.hasNotch ? geometry.notchSize.width + 12 : 12)
            if NotchUIRules.showsQuit(state.phase) {
                QuitButton(ui: ui)
            }
            if !state.currentSet.isEmpty && !ui.isQuitArmed() {
                Text(NotchUIRules.progressText(state))
                    .font(.system(size: 11, weight: .semibold, design: .rounded))
                    .monospacedDigit()
                    .foregroundStyle(NotchPalette.secondaryText)
            }
            // While "Quit playlist?" is armed it has the wing to itself (it doesn't fit beside these).
            if !ui.isQuitArmed() { settingsButton }
        }
        .frame(maxHeight: .infinity, alignment: .center)
    }

    private var settingsButton: some View {
            Button {
                ui.showingSettings.toggle()
            } label: {
                Image(systemName: ui.showingSettings ? "xmark" : "gearshape.fill")
                    .font(.system(size: 11, weight: .semibold))
                    .foregroundStyle(NotchPalette.secondaryText)
                    .frame(width: 20, height: 20)
                    .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .help(ui.showingSettings ? "Close settings" : "Settings")
    }
}
