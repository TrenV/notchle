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
        let state = ui.model.state
        HStack(spacing: 0) {
            leftGlyph(state.phase)
                .frame(width: NotchMetrics.collapsedWing)
            Spacer(minLength: geometry.notchSize.width)
            rightInfo(state)
                .frame(width: NotchMetrics.collapsedWing)
        }
        .padding(.horizontal, NotchSilhouette.earInset(hasNotch: geometry.hasNotch, expanded: false) / 2)
        .accessibilityElement(children: .combine)
    }

    @ViewBuilder
    private func leftGlyph(_ phase: GamePhase) -> some View {
        switch phase {
        case .playingSnippet, .correct, .revealed:
            EqualizerGlyph(playing: true)
        case .error:
            Image(systemName: "exclamationmark.triangle.fill")
                .font(.system(size: 12, weight: .semibold))
                .foregroundStyle(.yellow)
        default:
            Image(systemName: "music.note")
                .font(.system(size: 13, weight: .bold))
                .foregroundStyle(NotchPalette.green)
        }
    }

    @ViewBuilder
    private func rightInfo(_ state: GameState) -> some View {
        if case .loading = state.phase {
            NotchSpinner(size: 12, lineWidth: 2)
        } else {
            Text(NotchUIRules.progressText(state))
                .font(.system(size: 11.5, weight: .semibold, design: .rounded))
                .monospacedDigit()
                .foregroundStyle(NotchPalette.primaryText.opacity(0.85))
                .lineLimit(1)
                .minimumScaleFactor(0.7)
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
            if !state.currentSet.isEmpty {
                Text(NotchUIRules.progressText(state))
                    .font(.system(size: 11, weight: .semibold, design: .rounded))
                    .monospacedDigit()
                    .foregroundStyle(NotchPalette.secondaryText)
            }
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
        .frame(maxHeight: .infinity, alignment: .center)
    }
}
