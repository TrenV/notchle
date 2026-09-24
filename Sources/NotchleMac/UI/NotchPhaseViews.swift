import SwiftUI
import NotchleCore

/// Routes the current phase to its view. Views for `playingSnippet`, `guessing` and `wrong` are
/// never handed the track: only `AnswerView` gets it, via `NotchUIRules.revealedAnswer`.
struct PhaseContent: View {
    @Bindable var ui: NotchUIState
    var focus: FocusState<UIField?>.Binding

    var body: some View {
        let state = ui.model.state
        Group {
            switch state.phase {
            case .idle:
                SourceEntryView(ui: ui, focus: focus, heading: "Paste a Spotify link",
                                subheading: "A playlist, album or artist. You get 5 seconds per song.")
            case .exhausted:
                SourceEntryView(ui: ui, focus: focus, heading: "You've heard them all",
                                subheading: "Every song in \(state.listing?.name ?? "this listing") has been played. Try another link.")
            case .loading:
                LoadingView(name: state.listing?.name)
            case .playingSnippet(let tier):
                GuessView(ui: ui, focus: focus, tier: tier, playing: true)
            case .guessing(let tier):
                GuessView(ui: ui, focus: focus, tier: tier, playing: false)
            case .wrong(let tier, let verdict):
                WrongView(ui: ui, tier: tier, verdict: verdict)
            case .correct(let tier):
                AnswerView(ui: ui, answer: NotchUIRules.revealedAnswer(state), outcome: .correct(tier: tier))
            case .revealed(let verdict):
                AnswerView(ui: ui, answer: NotchUIRules.revealedAnswer(state), outcome: .revealed(verdict: verdict))
            case .setComplete(let n):
                SetEndView(ui: ui, correct: n, total: max(state.currentSet.count, n), complete: true)
            case .setFailed(let n):
                SetEndView(ui: ui, correct: n, total: max(state.currentSet.count, n), complete: false)
            case .error(let message):
                ErrorView(ui: ui, message: message)
            }
        }
        .transition(.opacity)
        .animation(.easeOut(duration: 0.18), value: state.phase)
    }
}

// MARK: idle / exhausted

struct SourceEntryView: View {
    @Bindable var ui: NotchUIState
    var focus: FocusState<UIField?>.Binding
    let heading: String
    let subheading: String

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            Text(heading)
                .font(.system(size: 15, weight: .semibold))
                .foregroundStyle(NotchPalette.primaryText)
            Text(subheading)
                .font(.system(size: 11.5))
                .foregroundStyle(NotchPalette.secondaryText)
                .lineLimit(2)
                .padding(.top, 3)
            Spacer(minLength: 10)
            HStack(spacing: 8) {
                NotchTextField(placeholder: "https://open.spotify.com/playlist/…", text: $ui.urlText,
                               field: .url, focus: focus)
                Button { ui.load() } label: { KeyHintLabel(title: "Load", hint: "⏎") }
                    .buttonStyle(NotchButtonStyle(kind: .primary))
            }
            HStack(spacing: 5) {
                if let message = ui.urlMessage {
                    Image(systemName: "exclamationmark.circle.fill")
                    Text(message)
                } else {
                    Text("Links from open.spotify.com or spotify: URIs")
                        .foregroundStyle(NotchPalette.tertiaryText)
                }
            }
            .font(.system(size: 11, weight: .medium))
            .foregroundStyle(NotchPalette.red)
            .frame(height: 16)
            .padding(.top, 8)
        }
        .onChange(of: ui.urlText) { ui.urlMessage = nil }
    }
}

// MARK: loading

struct LoadingView: View {
    let name: String?

    var body: some View {
        VStack(spacing: 12) {
            NotchSpinner()
            Text(name.map { "Loading \($0)…" } ?? "Loading songs…")
                .font(.system(size: 13, weight: .medium))
                .foregroundStyle(NotchPalette.secondaryText)
                .lineLimit(1)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }
}

// MARK: playingSnippet / guessing

struct GuessView: View {
    @Bindable var ui: NotchUIState
    var focus: FocusState<UIField?>.Binding
    let tier: Int
    let playing: Bool

    private var artistCount: Int { NotchUIRules.artistCount(ui.model.state) }

    var body: some View {
        let config = ui.model.state.config
        let seconds = NotchUIRules.seconds(ofTier: tier, config)
        VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: 8) {
                if playing {
                    EqualizerGlyph(playing: true)
                    Text("Listening · \(NotchUIRules.secondsLabel(seconds))")
                } else {
                    Image(systemName: "questionmark.bubble.fill")
                        .foregroundStyle(NotchPalette.secondaryText)
                    Text("What's this song?")
                }
                Spacer()
                RestartButton(ui: ui)
                AttemptDots(tiers: config.tiers, current: tier)
            }
            .font(.system(size: 12.5, weight: .semibold))
            .foregroundStyle(NotchPalette.primaryText)
            .frame(height: 18)
            SnippetProgressBar(start: ui.snippetStart, seconds: seconds, finished: !playing)
                .padding(.top, 9)
            HStack(spacing: 8) {
                NotchTextField(placeholder: "Title", text: $ui.titleText, field: .title, focus: focus)
                NotchTextField(placeholder: NotchUIRules.artistPlaceholder(artistCount: artistCount),
                               text: $ui.artistText, field: .artist, focus: focus)
            }
            .padding(.top, 12)
            Spacer(minLength: 8)
            HStack {
                Button { ui.model.send(.giveUp) } label: { KeyHintLabel(title: "Give up", hint: "esc") }
                    .buttonStyle(NotchButtonStyle(kind: .quiet))
                if let skip = NotchUIRules.skipSeconds(ui.phase, config) {
                    Button { ui.skip() } label: {
                        KeyHintLabel(title: "Skip · \(NotchUIRules.secondsLabel(skip))", hint: "⌘⇧S")
                    }
                    .buttonStyle(NotchButtonStyle(kind: .secondary))
                    .help("Use up this try and hear \(NotchUIRules.secondsLabel(skip))")
                    .padding(.leading, 10)
                }
                Spacer()
                Button { ui.submitGuess() } label: { KeyHintLabel(title: "Submit", hint: "⏎") }
                    .buttonStyle(NotchButtonStyle(kind: .primary))
                    .disabled(!ui.canSubmitGuess)
            }
        }
    }
}

// MARK: wrong

struct WrongView: View {
    @Bindable var ui: NotchUIState
    let tier: Int
    let verdict: Verdict

    var body: some View {
        let config = ui.model.state.config
        let retry = NotchUIRules.retrySeconds(after: tier, config)
        VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: 8) {
                Image(systemName: "xmark.octagon.fill").foregroundStyle(NotchPalette.red)
                Text(verdict.titleCorrect || verdict.artistCorrect ? "Half right" : "Not quite")
                Spacer()
                RestartButton(ui: ui)
                AttemptDots(tiers: config.tiers, current: tier, currentMissed: true)
            }
            .font(.system(size: 12.5, weight: .semibold))
            .foregroundStyle(NotchPalette.primaryText)
            .frame(height: 22)
            HStack(spacing: 8) {
                VerdictChip(label: "Title", correct: verdict.titleCorrect, guess: ui.titleText)
                VerdictChip(label: "Artist(s)", correct: verdict.artistCorrect, guess: ui.artistText,
                            hint: NotchUIRules.artistHint(verdict, artistCount: NotchUIRules.artistCount(ui.model.state)))
            }
            .padding(.top, 12)
            Spacer(minLength: 8)
            HStack {
                Button { ui.model.send(.giveUp) } label: { KeyHintLabel(title: "Give up", hint: "esc") }
                    .buttonStyle(NotchButtonStyle(kind: .quiet))
                Spacer()
                Button { ui.model.send(.retry) } label: {
                    KeyHintLabel(title: "Retry · \(NotchUIRules.secondsLabel(retry))", hint: "⌘R")
                }
                .buttonStyle(NotchButtonStyle(kind: .primary))
            }
        }
    }
}

// MARK: correct / revealed

enum AnswerOutcome {
    case correct(tier: Int)
    case revealed(verdict: Verdict?)
}

struct AnswerView: View {
    let ui: NotchUIState
    let answer: (title: String, artist: String)?
    let outcome: AnswerOutcome

    var body: some View {
        let state = ui.model.state
        VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: 6) {
                switch outcome {
                case .correct(let tier):
                    Image(systemName: "checkmark.seal.fill").foregroundStyle(NotchPalette.green)
                    Text("Got it in \(NotchUIRules.secondsLabel(NotchUIRules.seconds(ofTier: tier, state.config)))")
                        .foregroundStyle(NotchPalette.green)
                case .revealed(let verdict):
                    Image(systemName: "eye.fill").foregroundStyle(NotchPalette.secondaryText)
                    Text(verdict == nil ? "You gave up. It was" : "Out of tries. It was")
                        .foregroundStyle(NotchPalette.secondaryText)
                }
                Spacer()
                RestartButton(ui: ui)
                EqualizerGlyph(playing: true, color: NotchPalette.secondaryText)
            }
            .font(.system(size: 12, weight: .semibold))
            .frame(height: 18)
            VStack(alignment: .leading, spacing: 2) {
                Text(answer?.title ?? "–")
                    .font(.system(size: 19, weight: .bold))
                    .foregroundStyle(NotchPalette.primaryText)
                Text(answer?.artist ?? "")
                    .font(.system(size: 13, weight: .medium))
                    .foregroundStyle(NotchPalette.secondaryText)
            }
            .lineLimit(1)
            .truncationMode(.tail)
            .padding(.top, 10)
            Spacer(minLength: 6)
            HStack(alignment: .center) {
                if !ui.model.playerPlaysFullTrack {
                    Label("Preview ends at 30s", systemImage: "info.circle")
                        .font(.system(size: 11, weight: .medium))
                        .foregroundStyle(NotchPalette.tertiaryText)
                }
                Spacer()
                Button { ui.model.send(.next) } label: { KeyHintLabel(title: "Next", hint: "⏎") }
                    .buttonStyle(NotchButtonStyle(kind: .primary))
            }
        }
    }
}

// MARK: setComplete / setFailed

struct SetEndView: View {
    let ui: NotchUIState
    let correct: Int
    let total: Int
    let complete: Bool

    var body: some View {
        HStack(alignment: .center, spacing: 18) {
            ZStack {
                Circle().stroke(Color.white.opacity(0.1), lineWidth: 5)
                Circle()
                    .trim(from: 0, to: total > 0 ? CGFloat(correct) / CGFloat(total) : 0)
                    .stroke(complete ? NotchPalette.green : Color.white.opacity(0.8),
                            style: StrokeStyle(lineWidth: 5, lineCap: .round))
                    .rotationEffect(.degrees(-90))
                Text("\(correct)/\(total)")
                    .font(.system(size: 17, weight: .bold, design: .rounded))
                    .monospacedDigit()
                    .foregroundStyle(NotchPalette.primaryText)
            }
            .frame(width: 78, height: 78)
            VStack(alignment: .leading, spacing: 4) {
                Text(complete ? "Perfect set!" : "Set over")
                    .font(.system(size: 16, weight: .bold))
                    .foregroundStyle(complete ? NotchPalette.green : NotchPalette.primaryText)
                Text(complete ? "The next \(total) songs are unlocked."
                              : "Get all \(total) right to unlock the next set.")
                    .font(.system(size: 12))
                    .foregroundStyle(NotchPalette.secondaryText)
                    .fixedSize(horizontal: false, vertical: true)
                Spacer(minLength: 6)
                HStack {
                    Spacer()
                    if complete {
                        Button { ui.model.send(.nextSet) } label: { KeyHintLabel(title: "Next set", hint: "⏎") }
                            .buttonStyle(NotchButtonStyle(kind: .primary))
                    } else {
                        Button { ui.model.send(.replaySet) } label: { KeyHintLabel(title: "Replay set", hint: "⏎") }
                            .buttonStyle(NotchButtonStyle(kind: .primary))
                    }
                }
            }
        }
        .frame(maxHeight: .infinity)
    }
}

// MARK: error

struct ErrorView: View {
    let ui: NotchUIState
    let message: String

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(alignment: .top, spacing: 10) {
                Image(systemName: "exclamationmark.triangle.fill")
                    .font(.system(size: 18))
                    .foregroundStyle(.yellow)
                VStack(alignment: .leading, spacing: 3) {
                    Text("Something went wrong")
                        .font(.system(size: 14, weight: .semibold))
                        .foregroundStyle(NotchPalette.primaryText)
                    Text(message)
                        .font(.system(size: 12))
                        .foregroundStyle(NotchPalette.secondaryText)
                        .lineLimit(3)
                        .fixedSize(horizontal: false, vertical: true)
                }
            }
            Spacer(minLength: 8)
            HStack {
                Button { ui.model.send(.reset) } label: { Text("Reset") }
                    .buttonStyle(NotchButtonStyle(kind: .secondary))
                Spacer()
                Button { ui.model.send(.next) } label: { KeyHintLabel(title: "Skip", hint: "⏎") }
                    .buttonStyle(NotchButtonStyle(kind: .primary))
            }
        }
    }
}
