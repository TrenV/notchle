import SwiftUI
import NotchleCore

/// Small settings view shown in place of the phase content (gear in the header).
struct NotchSettingsView: View {
    @Bindable var ui: NotchUIState

    var body: some View {
        let model = ui.model
        VStack(alignment: .leading, spacing: 0) {
            Text("Settings")
                .font(.system(size: 15, weight: .semibold))
                .foregroundStyle(NotchPalette.primaryText)
            row("Play songs with") {
                Picker("", selection: Binding(get: { model.settings.playerMode },
                                              set: { ui.setPlayerMode($0) })) {
                    Text("Spotify app").tag(PlayerMode.spotifyApp)
                    Text("30-second previews").tag(PlayerMode.preview)
                }
                .pickerStyle(.segmented)
                .labelsHidden()
                .frame(width: 250)
            }
            .padding(.top, 12)
            row("Snippets") {
                Text(model.settings.config.tiers.map(NotchUIRules.secondsLabel).joined(separator: " · "))
                    .font(.system(size: 12, weight: .semibold, design: .rounded))
                    .foregroundStyle(NotchPalette.primaryText.opacity(0.85))
            }
            .padding(.top, 10)
            Spacer(minLength: 6)
            HStack {
                Text(model.playerName.isEmpty ? " " : "Now using \(model.playerName)")
                    .font(.system(size: 11, weight: .medium))
                    .foregroundStyle(NotchPalette.tertiaryText)
                Spacer()
                Button { ui.showingSettings = false } label: { KeyHintLabel(title: "Done", hint: "esc") }
                    .buttonStyle(NotchButtonStyle(kind: .secondary))
            }
        }
    }

    private func row<Content: View>(_ label: String, @ViewBuilder content: () -> Content) -> some View {
        HStack {
            Text(label)
                .font(.system(size: 12, weight: .medium))
                .foregroundStyle(NotchPalette.secondaryText)
            Spacer()
            content()
        }
        .frame(height: 24)
    }
}
