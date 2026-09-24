import AppKit
import SwiftUI
import NotchleCore

/// Small settings view shown in place of the phase content (gear in the header).
struct NotchSettingsView: View {
    @Bindable var ui: NotchUIState

    var body: some View {
        let model = ui.model
        let connect = model.spotifyConnect
        let controls = NotchUIRules.spotifyConnectControls(
            mode: model.settings.playerMode, status: connect?.status, clientID: connect?.clientID ?? "")
        VStack(alignment: .leading, spacing: 0) {
            Text("Play songs with")
                .font(.system(size: 12, weight: .medium))
                .foregroundStyle(NotchPalette.secondaryText)
            // Two per row: four labels side by side (a segmented picker) ran off both edges.
            LazyVGrid(columns: Array(repeating: GridItem(.flexible(), spacing: NotchLayout.choiceSpacing),
                                     count: NotchLayout.choiceColumns),
                      spacing: NotchLayout.choiceSpacing) {
                ForEach(NotchUIRules.playerChoices, id: \.mode) { choice in
                    PlayerChoiceButton(label: choice.label, selected: model.settings.playerMode == choice.mode) {
                        ui.setPlayerMode(choice.mode)
                    }
                }
            }
            .padding(.top, 5)

            if let connect, controls != .hidden {
                SpotifyConnectRow(connect: connect, controls: controls)
                    .padding(.top, 7)
            }
            if NotchUIRules.showsSnippetsRow(controls) {
                HStack {
                    Text("Snippets")
                        .font(.system(size: 12, weight: .medium))
                        .foregroundStyle(NotchPalette.secondaryText)
                    Spacer()
                    Text(model.settings.config.tiers.map(NotchUIRules.secondsLabel).joined(separator: " · "))
                        .font(.system(size: 12, weight: .semibold, design: .rounded))
                        .foregroundStyle(NotchPalette.primaryText.opacity(0.85))
                }
                .frame(height: 24)
                .padding(.top, 8)
            }
            Spacer(minLength: 6)
            HStack {
                Text(NotchLayout.settingsFooter(model.playerName))
                    .font(.system(size: 11, weight: .medium))
                    .foregroundStyle(NotchPalette.tertiaryText)
                    .fixedSize(horizontal: false, vertical: true)
                Spacer(minLength: 8)
                // The menu-bar ♪ item can sit hidden behind the notch, so quitting lives here too.
                Button { NSApp.terminate(nil) } label: { Text("Quit Notchle") }
                    .buttonStyle(NotchButtonStyle(kind: .quiet))
                    .padding(.trailing, 8)
                Button { ui.showingSettings = false } label: { KeyHintLabel(title: "Done", hint: "esc") }
                    .buttonStyle(NotchButtonStyle(kind: .secondary))
            }
        }
        .onAppear { connect?.refresh() }
    }
}

/// Client ID + Connect Spotify, or "Connected as …" + Sign out. Never shows track info.
private struct SpotifyConnectRow: View {
    @Bindable var connect: SpotifyConnectModel
    let controls: SpotifyConnectControls

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            switch controls {
            case .hidden:
                EmptyView()
            case .signIn(let canConnect, let busy, let message):
                HStack(spacing: 8) {
                    TextField("", text: $connect.clientID)
                        .textFieldStyle(.plain)
                        .font(.system(size: 12, weight: .medium, design: .monospaced))
                        .foregroundStyle(NotchPalette.primaryText)
                        .autocorrectionDisabled()
                        .background(alignment: .leading) {
                            if connect.clientID.isEmpty {
                                Text("Client ID")
                                    .font(.system(size: 12, weight: .medium))
                                    .foregroundStyle(NotchPalette.tertiaryText)
                                    .allowsHitTesting(false)
                            }
                        }
                        .padding(.horizontal, 8)
                        .frame(height: 26)
                        .background(RoundedRectangle(cornerRadius: 8, style: .continuous).fill(NotchPalette.fieldFill))
                        .disabled(busy)
                    Button {
                        busy ? connect.cancelConnect() : connect.connect()
                    } label: { Text(busy ? "Cancel" : "Connect Spotify") }
                        .buttonStyle(NotchButtonStyle(kind: busy ? .secondary : .primary))
                        .disabled(!canConnect)
                }
                Text(busy ? "Finish the sign-in in your browser…" : (message ?? NotchUIRules.spotifyConnectHelp))
                    .font(.system(size: 10.5, weight: .medium))
                    .foregroundStyle(message != nil && !busy ? Color(red: 1, green: 0.55, blue: 0.45) : NotchPalette.tertiaryText)
                    .fixedSize(horizontal: false, vertical: true)
            case .connected(let name):
                HStack {
                    Image(systemName: "checkmark.circle.fill")
                        .font(.system(size: 11, weight: .bold))
                        .foregroundStyle(NotchPalette.green)
                    Text("Connected as \(name)")
                        .font(.system(size: 12, weight: .semibold))
                        .foregroundStyle(NotchPalette.primaryText)
                        .fixedSize(horizontal: false, vertical: true)
                    Spacer()
                    Button { connect.signOut() } label: { Text("Sign out") }
                        .buttonStyle(NotchButtonStyle(kind: .secondary))
                }
            }
        }
    }
}

/// One player choice: a capsule, green when chosen. Its label always fits on one line
/// (checked in UILayoutTests), shrinking to 11 pt at most.
private struct PlayerChoiceButton: View {
    let label: String
    let selected: Bool
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            Text(label)
                .font(.system(size: 12, weight: selected ? .semibold : .medium))
                .foregroundStyle(selected ? Color.black : NotchPalette.primaryText.opacity(0.85))
                .lineLimit(1)
                .minimumScaleFactor(0.92)
                .padding(.horizontal, 10)
                .frame(maxWidth: .infinity)
                .frame(height: NotchLayout.choiceRowHeight)
                .background(Capsule().fill(selected ? NotchPalette.green : Color.white.opacity(0.09)))
                .contentShape(Capsule())
        }
        .buttonStyle(.plain)
        .accessibilityAddTraits(selected ? .isSelected : [])
    }
}
