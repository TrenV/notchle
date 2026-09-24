import SwiftUI
import NotchleCore

enum NotchPalette {
    static let green = Color(red: 0.12, green: 0.84, blue: 0.38)
    static let red = Color(red: 1.0, green: 0.36, blue: 0.36)
    static let primaryText = Color.white
    static let secondaryText = Color.white.opacity(0.55)
    static let tertiaryText = Color.white.opacity(0.32)
    static let fieldFill = Color.white.opacity(0.09)
    static let fieldStroke = Color.white.opacity(0.14)
    static let fieldStrokeFocused = Color.white.opacity(0.45)
}

struct NotchButtonStyle: ButtonStyle {
    enum Kind { case primary, secondary, quiet }
    var kind: Kind = .secondary
    @Environment(\.isEnabled) private var isEnabled

    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .font(.system(size: 12, weight: .semibold))
            .lineLimit(1)
            .padding(.horizontal, kind == .quiet ? 0 : 12)
            .frame(height: 26)
            .foregroundStyle(foreground)
            .background(Capsule().fill(background))
            .opacity(isEnabled ? (configuration.isPressed ? 0.75 : 1) : 0.4)
            .scaleEffect(configuration.isPressed ? 0.97 : 1)
            .contentShape(Capsule())
    }

    private var foreground: Color {
        switch kind {
        case .primary: .black
        case .secondary: .white
        case .quiet: NotchPalette.secondaryText
        }
    }

    private var background: Color {
        switch kind {
        case .primary: NotchPalette.green
        case .secondary: Color.white.opacity(0.13)
        case .quiet: .clear
        }
    }
}

/// A button label with a trailing keyboard hint ("Next ⏎").
struct KeyHintLabel: View {
    let title: String
    var hint: String?

    var body: some View {
        HStack(spacing: 5) {
            Text(title)
            if let hint {
                Text(hint).font(.system(size: 10, weight: .semibold)).opacity(0.55)
            }
        }
    }
}

struct NotchTextField: View {
    let placeholder: String
    @Binding var text: String
    let field: UIField
    var focus: FocusState<UIField?>.Binding

    var body: some View {
        let focused = focus.wrappedValue == field
        // Own placeholder: the macOS prompt ignores foreground styling and draws bright white.
        TextField("", text: $text)
            .textFieldStyle(.plain)
            .font(.system(size: 13, weight: .medium))
            .foregroundStyle(NotchPalette.primaryText)
            .autocorrectionDisabled()
            .focused(focus, equals: field)
            .background(alignment: .leading) {
                if text.isEmpty {
                    Text(placeholder)
                        .font(.system(size: 13, weight: .medium))
                        .foregroundStyle(NotchPalette.tertiaryText)
                        .lineLimit(1)
                        .allowsHitTesting(false)
                }
            }
            .padding(.horizontal, 10)
            .frame(height: 30)
            .background(RoundedRectangle(cornerRadius: 9, style: .continuous).fill(NotchPalette.fieldFill))
            .overlay(
                RoundedRectangle(cornerRadius: 9, style: .continuous)
                    .strokeBorder(focused ? NotchPalette.fieldStrokeFocused : NotchPalette.fieldStroke, lineWidth: 1)
            )
            .animation(.easeOut(duration: 0.12), value: focused)
    }
}

/// Small secondary ↺ button: replays the snippet or restarts the song (⌘⇧R). Hidden where
/// `NotchUIRules.restartLabel` is nil. Its label is a fixed string, never the track.
struct RestartButton: View {
    let ui: NotchUIState

    var body: some View {
        if let label = NotchUIRules.restartLabel(ui.phase) {
            Button { ui.restart() } label: {
                Image(systemName: "arrow.counterclockwise")
                    .font(.system(size: 10.5, weight: .bold))
                    .foregroundStyle(NotchPalette.secondaryText)
                    .frame(width: 20, height: 18)
                    .background(Capsule().fill(Color.white.opacity(0.09)))
                    .contentShape(Capsule())
            }
            .buttonStyle(.plain)
            .help("\(label) (⌘⇧R)")
            .accessibilityLabel(label)
        }
    }
}

/// Header button that quits the playlist, in two steps: ✕, then a red "Quit playlist?"
/// capsule for `NotchUIRules.quitConfirmWindow` seconds; clicking that (or ⌘N) quits.
struct QuitButton: View {
    let ui: NotchUIState

    var body: some View {
        let armed = ui.isQuitArmed()
        Button { ui.quitPressed() } label: {
            Group {
                if armed {
                    Text("Quit playlist?")
                        .font(.system(size: 11, weight: .bold))
                        .fixedSize()                    // the listing name truncates instead
                        .foregroundStyle(Color.white)
                        .padding(.horizontal, 9)
                        .frame(height: 20)
                        .background(Capsule().fill(NotchPalette.red))
                } else {
                    Image(systemName: "xmark.circle")
                        .font(.system(size: 12, weight: .semibold))
                        .foregroundStyle(NotchPalette.secondaryText)
                        .frame(width: 20, height: 20)
                }
            }
            .contentShape(Capsule())
        }
        .buttonStyle(.plain)
        .help(armed ? "Click again to quit (esc cancels)" : "Quit playlist (⌘N)")
        .accessibilityLabel(armed ? "Confirm quit playlist" : "Quit playlist")
        .animation(.easeOut(duration: 0.15), value: armed)
    }
}

/// Three pills, one per tier ("5s 10s 15s"): used tiers red, current white, later dim.
struct AttemptDots: View {
    let tiers: [Double]
    let current: Int
    /// true in `.wrong`: the current tier was just missed too.
    var currentMissed = false

    var body: some View {
        HStack(spacing: 4) {
            ForEach(Array(tiers.enumerated()), id: \.offset) { i, s in
                let missed = i < current || (i == current && currentMissed)
                Text(NotchUIRules.secondsLabel(s))
                    .font(.system(size: 9.5, weight: .bold, design: .rounded))
                    .monospacedDigit()
                    .foregroundStyle(i == current && !currentMissed ? Color.black : (missed ? NotchPalette.red : NotchPalette.tertiaryText))
                    .padding(.horizontal, 6)
                    .frame(height: 16)
                    .background(
                        Capsule().fill(i == current && !currentMissed ? Color.white
                                       : (missed ? NotchPalette.red.opacity(0.16) : Color.white.opacity(0.07)))
                    )
            }
        }
        .accessibilityElement(children: .ignore)
        .accessibilityLabel("Attempt \(current + 1) of \(tiers.count)")
    }
}

/// Thin bar filling over `seconds` from `start`, animated locally (the engine has no timers).
struct SnippetProgressBar: View {
    let start: Date?
    let seconds: Double
    /// Draw it full and dim (snippet over).
    var finished = false

    var body: some View {
        TimelineView(.animation(minimumInterval: 1.0 / 60, paused: finished || start == nil)) { context in
            let fraction: Double = {
                if finished { return 1 }
                guard let start, seconds > 0 else { return 0 }
                return min(1, max(0, context.date.timeIntervalSince(start) / seconds))
            }()
            GeometryReader { proxy in
                ZStack(alignment: .leading) {
                    Capsule().fill(Color.white.opacity(0.1))
                    Capsule()
                        .fill(finished ? Color.white.opacity(0.35) : NotchPalette.green)
                        .frame(width: max(4, proxy.size.width * fraction))
                }
            }
            .frame(height: 4)
        }
    }
}

/// Little animated equaliser shown while something plays.
struct EqualizerGlyph: View {
    var playing: Bool
    var color: Color = NotchPalette.green
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        let playing = playing && !reduceMotion
        TimelineView(.animation(minimumInterval: 1.0 / 30, paused: !playing)) { context in
            let t = context.date.timeIntervalSinceReferenceDate
            HStack(alignment: .center, spacing: 2) {
                ForEach(0..<4, id: \.self) { i in
                    let phase = t * (5.0 + Double(i) * 1.3) + Double(i) * 1.7
                    let h = playing ? 4 + 8 * (0.5 + 0.5 * sin(phase)) : 4
                    Capsule().fill(color).frame(width: 2.5, height: h)
                }
            }
            .frame(width: 18, height: 14)
        }
    }
}

/// ✓/✗ chip for one half of a verdict, with what the player typed.
struct VerdictChip: View {
    let label: String
    let correct: Bool
    let guess: String
    var hint: String?

    var body: some View {
        HStack(spacing: 7) {
            Image(systemName: correct ? "checkmark.circle.fill" : "xmark.circle.fill")
                .font(.system(size: 14, weight: .semibold))
                .foregroundStyle(correct ? NotchPalette.green : NotchPalette.red)
            VStack(alignment: .leading, spacing: 1) {
                HStack(spacing: 4) {
                    Text(label)
                        .foregroundStyle(NotchPalette.secondaryText)
                    if let hint {
                        Text("· \(hint)").foregroundStyle(NotchPalette.red)
                    }
                }
                .font(.system(size: 10, weight: .semibold))
                Text(guess.isEmpty ? "–" : guess)
                    .font(.system(size: 12, weight: .medium))
                    .foregroundStyle(correct ? NotchPalette.primaryText : NotchPalette.primaryText.opacity(0.7))
                    .strikethrough(!correct, color: NotchPalette.red.opacity(0.7))
                    .lineLimit(1)
            }
            Spacer(minLength: 0)
        }
        .padding(.horizontal, 10)
        .frame(height: 40)
        .frame(maxWidth: .infinity)
        .background(RoundedRectangle(cornerRadius: 10, style: .continuous)
            .fill((correct ? NotchPalette.green : NotchPalette.red).opacity(0.1)))
    }
}

/// Light spinner (the AppKit one draws dark on the black shape).
struct NotchSpinner: View {
    var size: CGFloat = 22
    var lineWidth: CGFloat = 2.5

    var body: some View {
        TimelineView(.animation) { context in
            let angle = context.date.timeIntervalSinceReferenceDate.truncatingRemainder(dividingBy: 1) * 360
            Circle()
                .trim(from: 0.08, to: 0.78)
                .stroke(AngularGradient(colors: [Color.white.opacity(0), .white], center: .center),
                        style: StrokeStyle(lineWidth: lineWidth, lineCap: .round))
                .rotationEffect(.degrees(angle))
                .frame(width: size, height: size)
        }
        .accessibilityLabel("Loading")
    }
}
