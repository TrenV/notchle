import AppKit
import NotchleCore

/// Finding, launching and hiding the Spotify desktop app. A seam so `SpotifyAppPlayer`'s
/// launch-and-wait logic is testable without Spotify installed.
@MainActor
protocol SpotifyAppControlling: AnyObject, Sendable {
    var isInstalled: Bool { get }
    var isRunning: Bool { get }
    /// Launches Spotify without activating it and with its windows hidden.
    func launchHidden() async throws
    /// Hides Spotify's windows so the now-playing title isn't on screen.
    func hide()
}

/// The real thing: LaunchServices via `NSWorkspace`.
@MainActor
final class WorkspaceSpotifyControl: SpotifyAppControlling {
    nonisolated init() {}

    private var appURL: URL? {
        NSWorkspace.shared.urlForApplication(withBundleIdentifier: SpotifyScripts.bundleID)
    }

    private var runningApps: [NSRunningApplication] {
        NSRunningApplication.runningApplications(withBundleIdentifier: SpotifyScripts.bundleID)
            .filter { !$0.isTerminated }
    }

    var isInstalled: Bool { appURL != nil }
    var isRunning: Bool { !runningApps.isEmpty }

    func launchHidden() async throws {
        guard let appURL else { throw PlayerError.unavailable("Spotify isn't installed") }
        let configuration = NSWorkspace.OpenConfiguration()
        configuration.activates = false
        configuration.hides = true
        configuration.addsToRecentItems = false
        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, any Error>) in
            NSWorkspace.shared.openApplication(at: appURL, configuration: configuration) { _, error in
                if let error {
                    continuation.resume(throwing: PlayerError.failed("Couldn't launch Spotify: \(error.localizedDescription)"))
                } else {
                    continuation.resume()
                }
            }
        }
    }

    func hide() {
        for app in runningApps { app.hide() }
    }
}
