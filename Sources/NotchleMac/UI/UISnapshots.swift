import AppKit
import SwiftUI
import NotchleCore

/// Renders every screen of the notch offscreen to PNG, over a mock menu bar, for visual review:
///
///     swift run Notchle --ui-snapshots <directory>
@MainActor
public enum UISnapshots {
    struct Scenario {
        let name: String
        let phase: GamePhase
        var expanded = true
        var hasNotch = true
        var title = ""
        var artist = ""
        var url = ""
        var urlMessage: String?
        var settings = false
        var confetti = false
        var reduceMotion = false
        var fullTrack = true
        var snippetElapsed: Double = 2
        var tall = false
        /// Track 5 ("Northbound") has two artists.
        var trackIndex = 6
        /// Tests render their own state.
        var state: GameState?
        /// How long the phase has been on (drives the collapsed pill's brief flashes).
        var secondsInPhase: Double = 0.3
    }

    static var scenarios: [Scenario] {
        let wrongVerdict = Verdict(titleCorrect: true, artistCorrect: false)
        return [
            Scenario(name: "01-collapsed-idle", phase: .idle, expanded: false),
            Scenario(name: "02-idle", phase: .idle),
            Scenario(name: "03-idle-unsupported", phase: .idle, url: "https://example.com/not-spotify",
                     urlMessage: "Unsupported link"),
            Scenario(name: "04-loading", phase: .loading),
            Scenario(name: "05-collapsed-playing", phase: .playingSnippet(tierIndex: 0), expanded: false),
            Scenario(name: "06-playing-t0", phase: .playingSnippet(tierIndex: 0), title: "Paper"),
            Scenario(name: "07-guessing-t0", phase: .guessing(tierIndex: 0), title: "Paper Lanterns", artist: "Midnight"),
            Scenario(name: "08-wrong-t0", phase: .wrong(tierIndex: 0, verdict: wrongVerdict),
                     title: "Paper Lanterns", artist: "The Kites"),
            Scenario(name: "09-playing-t1", phase: .playingSnippet(tierIndex: 1), title: "Paper Lanterns",
                     artist: "The Kites", snippetElapsed: 6),
            Scenario(name: "10-correct-confetti", phase: .correct(tierIndex: 1), confetti: true, tall: true),
            Scenario(name: "11-correct-preview-hint", phase: .correct(tierIndex: 0), fullTrack: false),
            Scenario(name: "12-correct-reduce-motion", phase: .correct(tierIndex: 2), confetti: true, reduceMotion: true),
            Scenario(name: "13-revealed-gave-up", phase: .revealed(verdict: nil)),
            Scenario(name: "14-revealed-out-of-tries", phase: .revealed(verdict: Verdict(titleCorrect: false, artistCorrect: true))),
            Scenario(name: "15-set-complete", phase: .setComplete(correctCount: 20)),
            Scenario(name: "16-set-failed", phase: .setFailed(correctCount: 14)),
            Scenario(name: "17-exhausted", phase: .exhausted),
            Scenario(name: "18-error", phase: .error(message: "Spotify is not running. Open Spotify, then press Skip.")),
            Scenario(name: "19-settings", phase: .guessing(tierIndex: 0), settings: true),
            Scenario(name: "20-pill-collapsed", phase: .playingSnippet(tierIndex: 0), expanded: false, hasNotch: false),
            Scenario(name: "22-guessing-two-artists", phase: .guessing(tierIndex: 0), title: "Northbound", trackIndex: 5),
            Scenario(name: "23-wrong-two-artists", phase: .wrong(tierIndex: 1, verdict: Verdict(titleCorrect: true, artistCorrect: false)),
                     title: "Northbound", artist: "Tove Ahlberg", trackIndex: 5),
            // Collapsed pill states (auto-close: the notch stays small unless hovered).
            Scenario(name: "24-collapsed-loading", phase: .loading, expanded: false),
            Scenario(name: "25-collapsed-snippet", phase: .playingSnippet(tierIndex: 1), expanded: false, snippetElapsed: 6),
            Scenario(name: "26-collapsed-guessing", phase: .guessing(tierIndex: 0), expanded: false),
            Scenario(name: "27-collapsed-wrong", phase: .wrong(tierIndex: 0, verdict: wrongVerdict), expanded: false),
            Scenario(name: "28-collapsed-correct-flash-confetti", phase: .correct(tierIndex: 0), expanded: false,
                     confetti: true, tall: true),
            Scenario(name: "29-collapsed-correct-after-flash", phase: .correct(tierIndex: 0), expanded: false, secondsInPhase: 3),
            Scenario(name: "30-collapsed-revealed", phase: .revealed(verdict: nil), expanded: false),
            Scenario(name: "31-collapsed-set-complete-flash", phase: .setComplete(correctCount: 20), expanded: false),
            Scenario(name: "32-collapsed-set-failed-flash", phase: .setFailed(correctCount: 14), expanded: false),
            Scenario(name: "33-collapsed-set-failed-later", phase: .setFailed(correctCount: 14), expanded: false, secondsInPhase: 6),
            Scenario(name: "34-collapsed-error", phase: .error(message: "x"), expanded: false),
            Scenario(name: "35-pill-collapsed-guessing", phase: .guessing(tierIndex: 0), expanded: false, hasNotch: false),
            Scenario(name: "21-pill-guessing", phase: .guessing(tierIndex: 1), hasNotch: false, title: "Glass"),
            // Last tier: no Skip (it would be a Give up).
            Scenario(name: "36-guessing-t2", phase: .guessing(tierIndex: 2), title: "Paper Lanterns", artist: "Midnight"),
        ]
    }

    /// Writes one PNG per scenario; returns the file URLs.
    @discardableResult
    public static func renderAll(to directory: URL) -> [URL] {
        try? FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        return scenarios.compactMap { scenario in
            let url = directory.appendingPathComponent("\(scenario.name).png")
            guard let data = render(scenario) else {
                FileHandle.standardError.write(Data("failed: \(scenario.name)\n".utf8))
                return nil
            }
            do { try data.write(to: url) } catch { return nil }
            FileHandle.standardError.write(Data("wrote \(url.path)\n".utf8))
            return url
        }
    }

    static func sampleState(_ phase: GamePhase, index: Int = 6) -> GameState {
        var s = GameState()
        let tracks = DemoGame.tracks
        s.listing = SourceListing(ref: SourceRef(kind: .playlist, id: "demo"), name: "Notchle Demo Mix", tracks: tracks)
        s.currentSet = tracks
        s.index = index
        s.setNumber = 1
        s.phase = phase
        switch phase {
        case .idle:
            s.listing = nil; s.currentSet = []; s.index = 0
        case .loading:
            s.currentSet = []; s.index = 0
        case .exhausted:
            s.currentSet = []; s.index = 0
        case .setComplete, .setFailed:
            s.index = 19
        default: break
        }
        return s
    }

    static func render(_ sc: Scenario) -> Data? {
        let model = NotchViewModel(state: sc.state ?? sampleState(sc.phase, index: sc.trackIndex))
        model.playerName = "Spotify app"
        model.playerPlaysFullTrack = sc.fullTrack
        let ui = NotchUIState(model: model)
        ui.isExpanded = sc.expanded
        ui.titleText = sc.title
        ui.artistText = sc.artist
        ui.urlText = sc.url
        ui.urlMessage = sc.urlMessage
        ui.showingSettings = sc.settings
        ui.reduceMotionOverride = sc.reduceMotion
        ui.snippetStart = Date().addingTimeInterval(-sc.snippetElapsed)
        ui.phaseStartedAt = Date().addingTimeInterval(-sc.secondsInPhase)

        let notchSize = NotchGeometry.fallbackNotchSize
        let geometry = NotchGeometry(
            hardwareNotch: sc.hasNotch ? CGRect(origin: .zero, size: notchSize) : nil,
            notchSize: notchSize, centerX: NotchMetrics.panelSize.width / 2, anchorTop: NotchMetrics.panelSize.height)
        let height: CGFloat = sc.tall ? NotchMetrics.panelSize.height
            : (sc.expanded ? NotchMetrics.expandedSize.height + 60 : 96)
        let size = CGSize(width: NotchMetrics.panelSize.width, height: height)
        let root = ZStack(alignment: .top) {
            MockDesktop(hasNotch: sc.hasNotch, notchSize: notchSize)
            NotchRootView(ui: ui, geometry: geometry)
                .padding(.top, sc.hasNotch ? 0 : 24 + NotchGeometry.pillGap)
        }
        .frame(width: size.width, height: size.height, alignment: .top)
        .clipped()

        let hosting = NSHostingView(rootView: root)
        hosting.frame = CGRect(origin: .zero, size: size)
        let window = NSWindow(contentRect: CGRect(x: -30_000, y: -30_000, width: size.width, height: size.height),
                              styleMask: [.borderless], backing: .buffered, defer: false)
        window.appearance = NSAppearance(named: .darkAqua)
        window.contentView = hosting
        window.orderFrontRegardless()
        if sc.confetti { ui.celebrationStart = Date().addingTimeInterval(-0.25) }
        RunLoop.main.run(until: Date().addingTimeInterval(sc.confetti ? 0.2 : 0.35))
        hosting.layoutSubtreeIfNeeded()
        hosting.display()
        guard let rep = hosting.bitmapImageRepForCachingDisplay(in: hosting.bounds) else { return nil }
        hosting.cacheDisplay(in: hosting.bounds, to: rep)
        window.orderOut(nil)
        return rep.representation(using: .png, properties: [:])
    }
}

/// Wallpaper + translucent menu bar (+ the black hardware notch), so the shape reads as it
/// would on screen.
private struct MockDesktop: View {
    let hasNotch: Bool
    let notchSize: CGSize

    var body: some View {
        ZStack(alignment: .top) {
            LinearGradient(colors: [Color(red: 0.36, green: 0.42, blue: 0.62), Color(red: 0.78, green: 0.56, blue: 0.52)],
                           startPoint: .topLeading, endPoint: .bottomTrailing)
            Rectangle().fill(Color.black.opacity(0.18)).frame(height: hasNotch ? notchSize.height : 24)
                .overlay(alignment: .leading) {
                    HStack(spacing: 16) {
                        Image(systemName: "apple.logo")
                        Text("Finder").fontWeight(.bold)
                        Text("File"); Text("Edit"); Text("View")
                    }
                    .font(.system(size: 13))
                    .foregroundStyle(.white)
                    .padding(.leading, 14)
                }
                .overlay(alignment: .trailing) {
                    HStack(spacing: 14) {
                        Image(systemName: "wifi"); Image(systemName: "battery.75percent"); Text("Thu 24 Sep 14:05")
                    }
                    .font(.system(size: 13))
                    .foregroundStyle(.white)
                    .padding(.trailing, 14)
                }
            if hasNotch {
                NotchShape(topRadius: 6, bottomRadius: 10).fill(Color.black)
                    .frame(width: notchSize.width + 12, height: notchSize.height)
            }
        }
    }
}
