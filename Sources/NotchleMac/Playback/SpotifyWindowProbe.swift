import AppKit
import ApplicationServices

/// Diagnostic (`Notchle --probe-spotify-window`): does Spotify stay out of sight on a track
/// change if its windows are minimized, or moved off-screen, first? Needs the Accessibility
/// permission. Plays a few seconds of one track per strategy and pauses. Prints a verdict.
@MainActor
public enum SpotifyWindowProbe {
    static let bundleID = "com.spotify.client"
    static let testTrack = "spotify:track:2FZcjBYK4dTt48q94pJbJD"

    /// Results also go to this file: launched via `open`, stdout isn't visible.
    public static let logURL = FileManager.default.homeDirectoryForCurrentUser
        .appendingPathComponent("Library/Logs/Notchle-spotify-window-probe.txt")

    private static func print(_ line: String) {
        Swift.print(line)
        let data = Data((line + "\n").utf8)
        if let h = try? FileHandle(forWritingTo: logURL) { h.seekToEndOfFile(); h.write(data); try? h.close() }
        else { try? data.write(to: logURL) }
    }

    public static func run() async {
        try? FileManager.default.removeItem(at: logURL)
        print("Notchle Spotify window probe, \(Date())")
        let trusted = AXIsProcessTrustedWithOptions(["AXTrustedCheckOptionPrompt": true] as CFDictionary)
        guard trusted else {
            print("""
            Notchle needs the Accessibility permission for this test.
            System Settings › Privacy & Security › Accessibility › turn Notchle on, then run it again.
            """)
            return
        }
        guard let spotify = NSRunningApplication.runningApplications(withBundleIdentifier: bundleID).first else {
            print("Spotify isn't running. Open Spotify and run the test again.")
            return
        }
        let ax = AXUIElementCreateApplication(spotify.processIdentifier)
        print("Spotify windows via Accessibility: \(windows(ax).count)")

        for strategy in ["baseline (hidden only)", "minimized", "off-screen"] {
            spotify.hide()
            try? await Task.sleep(for: .milliseconds(400))
            switch strategy {
            case "minimized": for w in windows(ax) { set(w, kAXMinimizedAttribute, kCFBooleanTrue) }
            case "off-screen": for w in windows(ax) { setPosition(w, CGPoint(x: -20000, y: -20000)) }
            default: break
            }
            try? await Task.sleep(for: .milliseconds(400))
            let result = await watch(spotify) { osascript("tell application \"Spotify\" to play track \"\(testTrack)\"") }
            osascript("tell application \"Spotify\" to pause")
            print("\(strategy): \(result.visibleMs == 0 ? "NEVER visible" : "visible for ~\(result.visibleMs) ms") · activated: \(result.activated) · positions: \(result.positions)")
            // Undo, so the next strategy starts from the same place.
            for w in windows(ax) { set(w, kAXMinimizedAttribute, kCFBooleanFalse) }
            if strategy == "off-screen", let screen = NSScreen.main {
                for w in windows(ax) { setPosition(w, CGPoint(x: screen.frame.midX - 400, y: 120)) }
            }
            spotify.hide()
            try? await Task.sleep(for: .milliseconds(600))
        }
        print("Done. Spotify is paused and hidden.")
    }

    /// Samples Spotify's on-screen windows every 5 ms for 2 s after `trigger`.
    private static func watch(_ app: NSRunningApplication, _ trigger: () -> Void) async
        -> (visibleMs: Int, activated: Bool, positions: String) {
        trigger()
        var visible = 0, activated = false
        var seen = Set<String>()
        let end = Date().addingTimeInterval(2)
        while Date() < end {
            let list = (CGWindowListCopyWindowInfo([.optionOnScreenOnly, .excludeDesktopElements], kCGNullWindowID) as? [[String: Any]]) ?? []
            let mine = list.filter { ($0[kCGWindowOwnerPID as String] as? pid_t) == app.processIdentifier
                && ($0[kCGWindowLayer as String] as? Int) == 0 }
            let onScreen = mine.filter { w in
                guard let b = w[kCGWindowBounds as String] as? [String: CGFloat],
                      let rect = CGRect(dictionaryRepresentation: b as CFDictionary) else { return false }
                seen.insert("\(Int(rect.minX)),\(Int(rect.minY))")
                return NSScreen.screens.contains { $0.frame.intersects(flipped(rect)) }
            }
            if !onScreen.isEmpty { visible += 5 }
            if app.isActive { activated = true }
            try? await Task.sleep(for: .milliseconds(5))
        }
        return (visible, activated, seen.isEmpty ? "none" : seen.sorted().joined(separator: " "))
    }

    /// CGWindow bounds are top-left based; NSScreen frames bottom-left.
    private static func flipped(_ r: CGRect) -> CGRect {
        let h = NSScreen.screens.first?.frame.height ?? 0
        return CGRect(x: r.minX, y: h - r.maxY, width: r.width, height: r.height)
    }

    private static func windows(_ ax: AXUIElement) -> [AXUIElement] {
        var value: CFTypeRef?
        AXUIElementCopyAttributeValue(ax, kAXWindowsAttribute as CFString, &value)
        return (value as? [AXUIElement]) ?? []
    }

    private static func set(_ w: AXUIElement, _ attr: String, _ value: CFTypeRef) {
        AXUIElementSetAttributeValue(w, attr as CFString, value)
    }

    private static func setPosition(_ w: AXUIElement, _ p: CGPoint) {
        var point = p
        if let v = AXValueCreate(.cgPoint, &point) { set(w, kAXPositionAttribute, v) }
    }

    @discardableResult
    private static func osascript(_ script: String) -> Int32 {
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/usr/bin/osascript")
        p.arguments = ["-e", script]
        try? p.run(); p.waitUntilExit()
        return p.terminationStatus
    }
}
