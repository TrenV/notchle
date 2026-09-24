import AppKit
import NotchleCore

/// Debug harness for the notch UI: a fake game (no Spotify, no audio) behind a real panel.
///
///     swift run Notchle                      # interactive: paste anything with "spotify" or "demo"
///     swift run Notchle --ui-demo-auto       # scripted cycle through every GamePhase
///     options: --ui-quit-after <seconds>  --ui-activate (NSApp.activate when taking the keyboard)
///              --ui-start guessing        (skip the URL step)
///
/// In the fake game the right answer is the title and every artist shown in the
/// correct/revealed screens; type "error" as the link to see the error screen.
@MainActor
public final class UIDemo {
    public let model: NotchViewModel
    public let controller: NotchPanelController
    private let game: DemoGame
    private let auto: Bool
    private let quitAfter: TimeInterval?
    private let startInGuess: Bool

    public init(arguments: [String]) {
        func value(after flag: String) -> String? {
            guard let i = arguments.firstIndex(of: flag), i + 1 < arguments.count else { return nil }
            return arguments[i + 1]
        }
        auto = arguments.contains("--ui-demo-auto")
        quitAfter = value(after: "--ui-quit-after").flatMap(TimeInterval.init)
        startInGuess = value(after: "--ui-start") == "guessing"

        model = NotchViewModel()
        model.playerName = "Spotify app (demo)"
        controller = NotchPanelController(model: model)
        game = DemoGame(model: model)
        if arguments.contains("--ui-activate") { controller.keyboardStrategy = .makeKeyAndActivate }
    }

    public func start() {
        let log: (String) -> Void = { line in
            FileHandle.standardError.write(Data("[notchle-ui] \(line)\n".utf8))
        }
        controller.log = log
        game.log = log
        controller.ui.parseSource = { text in
            let t = text.lowercased()
            if t == "error" { return SourceRef(kind: .playlist, id: "error") }
            return t.contains("spotify") || t.contains("demo") ? SourceRef(kind: .playlist, id: "demo") : nil
        }
        model.send = { [game] action in game.send(action) }
        model.updateSettings = { [model] settings in
            model.settings = settings
            model.playerName = settings.playerMode == .preview ? "30-second previews (demo)" : "Spotify app (demo)"
            model.playerPlaysFullTrack = settings.playerMode != .preview
            log("updateSettings: \(settings.playerMode)")
        }
        controller.show()
        log("demo started (auto: \(auto), keyboard: \(controller.keyboardStrategy))")
        observeTyping()
        if startInGuess { game.send(.load(SourceRef(kind: .playlist, id: "demo"))) }
        if auto { runScript() }
        if let quitAfter {
            DispatchQueue.main.asyncAfter(deadline: .now() + quitAfter) {
                log("quitting after \(quitAfter)s")
                NSApp.terminate(nil)
            }
        }
    }

    /// Logs what reaches the text fields, to check real keyboard routing.
    private func observeTyping() {
        let ui = controller.ui
        withObservationTracking {
            _ = (ui.urlText, ui.titleText, ui.artistText)
        } onChange: { [weak self] in
            Task { @MainActor [weak self] in
                guard let self else { return }
                self.controller.log("typed: url=\"\(ui.urlText)\" title=\"\(ui.titleText)\" artist=\"\(ui.artistText)\"")
                self.observeTyping()
            }
        }
    }

    // MARK: Scripted cycle

    private func runScript() {
        let ui = controller.ui
        let g = game
        var steps: [(TimeInterval, () -> Void)] = []
        func step(_ delay: TimeInterval, _ body: @escaping () -> Void) { steps.append((delay, body)) }

        step(0.5) { ui.urlText = "https://open.spotify.com/playlist/demo" }
        step(1.5) { ui.load() }                                           // loading → playing(0)
        step(3.0) { ui.titleText = "Paper"; ui.artistText = "Kites" }      // typing during snippet
        step(4.0) { g.send(.submit(Guess(title: "Paper", artist: "Kites"))) } // wrong(0)
        step(3.0) { g.send(.retry) }                                        // playing(1)
        step(2.0) { if let t = g.current { g.send(.submit(Guess(title: t.title, artist: t.artists.joined(separator: ", ")))) } } // correct
        step(4.0) { g.send(.next) }                                         // playing(0)
        step(6.0) { g.send(.giveUp) }                                       // revealed
        step(3.0) { g.forceSetEnd(complete: true) }
        step(3.0) { g.forceSetEnd(complete: false) }
        step(3.0) { g.set(.exhausted) }
        step(3.0) { g.set(.error(message: "Spotify is not running. Open Spotify and press Skip.")) }
        step(3.0) { g.send(.reset) }

        var total: TimeInterval = 0
        for (delay, body) in steps {
            total += delay
            DispatchQueue.main.asyncAfter(deadline: .now() + total) { body() }
        }
    }
}

/// Minimal stand-in for `GameEngine` so every screen can be driven by hand.
@MainActor
final class DemoGame {
    let model: NotchViewModel
    var log: (String) -> Void = { _ in }
    private var snippetToken = 0

    static let tracks: [Track] = [
        ("Paper Lanterns", ["The Midnight Kites"]), ("Glass Harbour", ["Nova Reyes"]),
        ("Slow Satellites", ["Juniper & The Owls"]), ("Coastline Radio", ["Mara Linde"]),
        ("Velvet Static", ["Odd Weather"]), ("Northbound", ["Tove Ahlberg", "Kasper Ruud"]),
        ("Lemon Skies", ["Sunday Club"]), ("Afterglow Avenue", ["Neon Tapes"]),
        ("Hollow Moon", ["Iris Vale"]), ("Carousel", ["The Paper Boats"]),
        ("Fever Dream Summer", ["Lola Park"]), ("Undertow", ["Blue Harbor"]),
        ("Monochrome", ["Elliot Stray"]), ("Wildflower Tape", ["June Arcade"]),
        ("Neon Rain", ["Kyoto Drive"]), ("Silver Lining", ["Amber Fields"]),
        ("Gravity Games", ["Otto Frame"]), ("Midnight Ferry", ["Sea of Lamps"]),
        ("Golden Hour Ghosts", ["Wren & Wilder"]), ("Last Train Home", ["The Quiet Hours"]),
    ].enumerated().map { i, t in
        Track(id: "demo\(i)", uri: "spotify:track:demo\(i)", title: t.0, artists: t.1,
              durationMs: 200_000, previewURL: nil)
    }

    init(model: NotchViewModel) { self.model = model }

    var current: Track? { model.state.currentTrack }

    func set(_ phase: GamePhase) {
        model.state.phase = phase
        log("phase → \(phase)")
    }

    func forceSetEnd(complete: Bool) {
        var s = model.state
        s.results = (0..<s.currentSet.count).map { i in complete || i % 3 != 0 ? .correct(tierIndex: 0) : .missed }
        s.index = max(0, s.currentSet.count - 1)
        model.state = s
        set(complete ? .setComplete(correctCount: s.correctCount) : .setFailed(correctCount: s.correctCount))
    }

    func send(_ action: GameAction) {
        log("send \(action)")
        var s = model.state
        switch (action, s.phase) {
        case (.load(let ref), _):
            set(.loading)
            DispatchQueue.main.asyncAfter(deadline: .now() + 1.0) { [self] in
                if ref.id == "error" { set(.error(message: "Could not load that link: the page had no tracks.")); return }
                var s = model.state
                s.listing = SourceListing(ref: ref, name: "Notchle Demo Mix", tracks: Self.tracks)
                s.currentSet = Self.tracks
                s.index = 0
                s.results = []
                s.setNumber = 1
                model.state = s
                playSnippet(tier: 0)
            }
        case (.submit(let guess), .playingSnippet(let tier)), (.submit(let guess), .guessing(let tier)):
            guard let track = s.currentTrack else { return }
            let titleOK = guess.title.lowercased() == track.title.lowercased()
            // Rule: every credited artist, any order.
            let typed = guess.artist.lowercased()
            let artistOK = track.artists.allSatisfy { typed.contains($0.lowercased()) }
            let verdict = Verdict(titleCorrect: titleOK, artistCorrect: artistOK)
            snippetToken += 1
            if verdict.isCorrect {
                s.results.append(.correct(tierIndex: tier))
                s.celebrationCount += 1
                s.phase = .correct(tierIndex: tier)
            } else if tier + 1 < s.config.tiers.count {
                s.phase = .wrong(tierIndex: tier, verdict: verdict)
            } else {
                s.results.append(.missed)
                s.phase = .revealed(verdict: verdict)
            }
            model.state = s
            log("phase → \(s.phase)")
        case (.retry, .wrong(let tier, _)):
            playSnippet(tier: tier + 1)
        case (.giveUp, .playingSnippet), (.giveUp, .guessing), (.giveUp, .wrong):
            snippetToken += 1
            s.results.append(.missed)
            model.state = s
            set(.revealed(verdict: nil))
        case (.next, .correct), (.next, .revealed), (.next, .error):
            if case .error = s.phase, s.currentSet.isEmpty { set(.idle); return }
            if case .error = s.phase { s.results.append(.missed) }
            s.index += 1
            model.state = s
            if s.index >= s.currentSet.count {
                set(s.correctCount == s.currentSet.count ? .setComplete(correctCount: s.correctCount)
                                                          : .setFailed(correctCount: s.correctCount))
            } else {
                playSnippet(tier: 0)
            }
        case (.nextSet, .setComplete):
            set(.exhausted)
        case (.replaySet, .setFailed):
            s.index = 0
            s.results = []
            s.currentSet.shuffle()
            model.state = s
            playSnippet(tier: 0)
        case (.reset, _):
            snippetToken += 1
            s.phase = .idle
            s.listing = nil
            s.currentSet = []
            s.index = 0
            s.results = []
            model.state = s
            log("phase → idle")
        default:
            log("ignored \(action) in \(s.phase)")
        }
    }

    private func playSnippet(tier: Int) {
        snippetToken += 1
        let token = snippetToken
        set(.playingSnippet(tierIndex: tier))
        let seconds = NotchUIRules.seconds(ofTier: tier, model.state.config)
        DispatchQueue.main.asyncAfter(deadline: .now() + seconds) { [self] in
            guard token == snippetToken, case .playingSnippet(tier) = model.state.phase else { return }
            set(.guessing(tierIndex: tier))
        }
    }
}
