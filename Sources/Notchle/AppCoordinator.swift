import AppKit
import NotchleCore
import NotchleMac

/// Runs the game: feeds actions into the pure `GameEngine`, publishes the new state to the
/// UI, and executes the engine's effects against the track source, player and store.
@MainActor
final class AppCoordinator {
    let model: NotchViewModel

    private var engine: GameEngine
    private var settings: AppSettings
    private var player: Player
    private let source: TrackSource
    private let store: ProgressStore
    private let historyStore: HistoryStore
    private let artwork: ArtworkResolver
    private let makePlayer: @MainActor (PlayerMode) -> Player

    /// Playback operations run strictly one after another. A new operation cancels the one
    /// in flight (a snippet, typically) and waits for it to wind down before starting, so a
    /// "continue playing" can never race ahead of the snippet's own pause.
    private var playbackTask: Task<Void, Never>?
    private var fetchTask: Task<Void, Never>?

    /// Waits until every queued playback operation has finished (tests).
    func drainPlayback() async {
        while let task = playbackTask {
            await task.value
            if playbackTask == task { return }
        }
    }

    init(
        source: TrackSource = EmbedTrackSource(),
        store: ProgressStore = AppCoordinator.defaultStore(),
        historyStore: HistoryStore? = nil,
        artwork: ArtworkResolver = ArtworkResolver(),
        makePlayer: @escaping @MainActor (PlayerMode) -> Player = AppCoordinator.makeDefaultPlayer,
        spotifyConnect: SpotifyConnectModel? = nil
    ) {
        self.source = source
        self.store = store
        // Next to progress.json by default (so tests with a temp store never touch the real one).
        self.historyStore = historyStore ?? HistoryStore(directory: store.fileURL.deletingLastPathComponent())
        self.artwork = artwork
        self.makePlayer = makePlayer
        let progress = store.load()
        self.settings = progress.settings
        self.engine = GameEngine(
            config: progress.settings.config,
            clearedTrackIDs: progress.clearedTrackIDs,
            seed: UInt64.random(in: .min ... .max)
        )
        self.player = makePlayer(progress.settings.playerMode)
        self.model = NotchViewModel(state: engine.state, settings: progress.settings)
        model.spotifyConnect = spotifyConnect
        model.history = self.historyStore.load()
        publishPlayer()
        model.clearHistory = { [weak self] in self?.clearHistory() }
        model.send = { [weak self] action in self?.send(action) }
        model.updateSettings = { [weak self] new in self?.apply(settings: new) }
    }

    func send(_ action: GameAction) {
        let effects = engine.send(action)
        model.state = engine.state
        if !Self.showsAnswer(engine.state.phase) { model.currentArtworkURL = nil }
        effects.forEach(run)
    }

    /// Clears the list of cleared songs, so every track can come back.
    func resetProgress() {
        send(.reset)
        engine = GameEngine(config: settings.config, clearedTrackIDs: [], seed: UInt64.random(in: .min ... .max))
        model.state = engine.state
        save()
    }

    /// Empties the play history (its own two-step button; Reset progress leaves it alone).
    func clearHistory() {
        do { try historyStore.clear() } catch { NSLog("Notchle: clearing history failed: \(error)") }
        model.history = []
    }

    // MARK: - Effects

    /// Runs one effect directly, bypassing the engine (tests).
    func runForTesting(_ effect: GameEffect) { run(effect) }

    private func run(_ effect: GameEffect) {
        switch effect {
        case .fetch(let ref):
            settings.lastSource = ref
            model.settings = settings
            save()
            fetchTask?.cancel()
            fetchTask = Task { [source] in
                do {
                    let listing = try await source.listing(for: ref)
                    guard !Task.isCancelled else { return }
                    self.send(.loaded(listing))
                } catch {
                    guard !Task.isCancelled else { return }
                    self.send(.loadFailed(message: Self.describe(error)))
                }
            }

        case .playSnippet(let track, let start, let seconds):
            enqueuePlayback { player in
                do {
                    try await player.playSnippet(of: track, from: start, seconds: seconds)
                    guard !Task.isCancelled else { return }
                    self.send(.snippetFinished)
                } catch is CancellationError {
                    // Superseded by a guess, a skip or a new snippet.
                } catch {
                    guard !Task.isCancelled else { return }
                    self.send(.playbackFailed(message: Self.describe(error)))
                }
            }

        case .continuePlaying:
            enqueuePlayback { player in
                try? await player.continuePlaying()
            }

        case .restartTrack(let track):
            // Like continuePlaying: the answer is already on screen, so a failure here is not
            // worth an error screen (the song just doesn't restart).
            enqueuePlayback { player in
                do {
                    try await player.restartTrack(track)
                } catch {
                    NSLog("Notchle: restarting the song failed: \(error)")
                }
            }

        case .stop:
            enqueuePlayback { player in
                await player.stop()
            }

        case .persistProgress:
            save()

        case let .recordOutcome(track, outcome, wrongGuesses, skips):
            let entry = HistoryEntry(date: Date(), track: track, outcome: outcome,
                                     wrongGuesses: wrongGuesses, skips: skips,
                                     listingName: engine.state.listing?.name ?? "",
                                     listingRef: engine.state.listing?.ref)
            do {
                model.history = try historyStore.append(entry)
            } catch {
                NSLog("Notchle: saving history failed: \(error)")
                model.history = HistoryStore.capped(model.history + [entry], historyStore.cap)
            }
            resolveArtwork(for: track.id, entryID: entry.id)
        }
    }

    /// Looks up the cover now that the answer is decided (never earlier: spoiler rule), then
    /// stores it with the history entry and shows it if that track's answer is still on screen.
    /// Recording never waits for this; a failure leaves the entry without a cover.
    private func resolveArtwork(for trackID: String, entryID: UUID) {
        let resolver = artwork
        let id = UUID()
        artworkTasks[id] = Task {
            defer { self.artworkTasks[id] = nil }
            guard let url = await resolver.artworkURL(forTrackID: trackID) else { return }
            if self.engine.state.currentTrack?.id == trackID, Self.showsAnswer(self.engine.state.phase) {
                self.model.currentArtworkURL = url
            }
            do {
                self.model.history = try self.historyStore.setArtworkURL(url, for: entryID)
            } catch {
                NSLog("Notchle: saving the cover failed: \(error)")
                self.model.history = self.model.history.map { $0.id == entryID ? $0.withArtworkURL(url) : $0 }
            }
        }
    }

    /// Cover lookups in flight; each removes itself when done (runs on the main actor, so it
    /// cannot finish before it is stored).
    private var artworkTasks: [UUID: Task<Void, Never>] = [:]

    /// Waits for every pending cover lookup (tests).
    func drainArtwork() async {
        while !artworkTasks.isEmpty {
            for t in Array(artworkTasks.values) { await t.value }
        }
    }

    private static func showsAnswer(_ phase: GamePhase) -> Bool {
        switch phase {
        case .correct, .revealed: true
        default: false
        }
    }

    private func enqueuePlayback(_ operation: @escaping @MainActor (Player) async -> Void) {
        let previous = playbackTask
        previous?.cancel()
        let player = self.player
        playbackTask = Task {
            await previous?.value
            await operation(player)
        }
    }

    // MARK: - Settings and persistence

    private func apply(settings new: AppSettings) {
        let modeChanged = new.playerMode != settings.playerMode
        let configChanged = new.config != settings.config
        settings = new
        model.settings = new
        if modeChanged {
            let old = player
            playbackTask?.cancel()
            playbackTask = Task { await old.stop() }
            player = makePlayer(new.playerMode)
            publishPlayer()
        }
        if configChanged {
            send(.configure(new.config))
        }
        save()
    }

    private func publishPlayer() {
        model.playerName = player.displayName
        model.playerPlaysFullTrack = player.playsFullTrack
    }

    private func save() {
        do {
            try store.save(Progress(settings: settings, clearedTrackIDs: engine.state.clearedTrackIDs))
        } catch {
            NSLog("Notchle: saving progress failed: \(error)")
        }
    }

    static func makeDefaultPlayer(_ mode: PlayerMode) -> Player {
        switch mode {
        case .spotifyApp: SpotifyAppPlayer()
        case .spotifyConnect: SpotifyConnectPlayer(api: sharedSpotifyConnect.api)
        case .preview: PreviewPlayer()
        }
    }

    /// One Spotify Connect sign-in for the app: the settings view and every Connect player share
    /// its `SpotifyWebAPI` (and so its token refresh).
    static let sharedSpotifyConnect = SpotifyConnectModel.live()

    static func defaultStore() -> ProgressStore {
        let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first
            ?? FileManager.default.temporaryDirectory
        return ProgressStore(directory: base.appendingPathComponent("Notchle", isDirectory: true))
    }

    // MARK: - Messages

    static func describe(_ error: Error) -> String {
        switch error {
        case PlayerError.notAuthorized:
            return "Notchle isn't allowed to control Spotify. Allow it in System Settings › Privacy & Security › Automation."
        case PlayerError.unavailable(let reason):
            return "\(reason). Switch to 30-second previews in settings."
        case PlayerError.noPreview:
            return "This song has no preview clip. Skip it, or switch to the Spotify app in settings."
        case PlayerError.failed(let reason):
            return reason
        case SourceError.invalidURL:
            return "That doesn't look like a Spotify playlist, album or artist link."
        case SourceError.notFound:
            return "Spotify couldn't find that. Is it public?"
        case SourceError.network(let reason):
            return "Couldn't reach Spotify: \(reason)"
        case SourceError.parseFailed(let reason):
            return "Couldn't read that Spotify page: \(reason)"
        case SourceError.empty:
            return "No playable songs on that page."
        default:
            return error.localizedDescription
        }
    }
}
