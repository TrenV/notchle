import AppKit

/// Diagnostic (`Notchle --probe-music-window`): does Music.app stay out of sight when Notchle
/// launches it hidden and then plays library songs by AppleScript, the way `AppleMusicPlayer`
/// does? Samples Music's on-screen windows (CGWindowList, every 5 ms) and whether it became the
/// active app, for 2.5 s after each step. Plays ~2 s of two songs from the library, then pauses.
/// Needs no Accessibility permission; macOS asks once for Automation access to Music.
@MainActor
public enum MusicWindowProbe {
    static let bundleID = "com.apple.Music"

    public static let logURL = FileManager.default.homeDirectoryForCurrentUser
        .appendingPathComponent("Library/Logs/Notchle-music-window-probe.txt")

    private static func print(_ line: String) {
        Swift.print(line)
        let data = Data((line + "\n").utf8)
        if let h = try? FileHandle(forWritingTo: logURL) { h.seekToEndOfFile(); h.write(data); try? h.close() }
        else { try? data.write(to: logURL) }
    }

    public static func run() async {
        try? FileManager.default.removeItem(at: logURL)
        print("Notchle Music window probe, \(Date())")
        var music = running()
        if music == nil {
            guard let url = NSWorkspace.shared.urlForApplication(withBundleIdentifier: bundleID) else {
                print("Music.app not found."); return
            }
            let config = NSWorkspace.OpenConfiguration()
            config.activates = false
            config.hides = true
            let result = await watch(nil) {
                NSWorkspace.shared.openApplication(at: url, configuration: config) { _, _ in }
            }
            music = running()
            print("launch hidden: \(verdict(result))")
        } else {
            print("Music was already running (launch step not measured; quit Music and rerun to measure it).")
        }
        guard let music else { print("Music didn't start."); return }
        music.hide()
        try? await Task.sleep(for: .milliseconds(500))
        for n in [1, 2] {
            let result = await watch(music) {
                osascript("tell application id \"\(bundleID)\" to play (track \(n) of library playlist 1)")
            }
            print("play library song \(n): \(verdict(result))")
        }
        osascript("tell application id \"\(bundleID)\" to pause")
        print("Done. Music is paused. Verdict: Notchle's Apple Music player is safe only if every line says NEVER visible and activated: false.")
    }

    private static func running() -> NSRunningApplication? {
        NSRunningApplication.runningApplications(withBundleIdentifier: bundleID).first { !$0.isTerminated }
    }

    private static func verdict(_ r: (visibleMs: Int, activated: Bool)) -> String {
        "\(r.visibleMs == 0 ? "NEVER visible" : "visible for ~\(r.visibleMs) ms") · activated: \(r.activated)"
    }

    private static func watch(_ app: NSRunningApplication?, _ trigger: () -> Void) async -> (visibleMs: Int, activated: Bool) {
        trigger()
        var visible = 0, activated = false
        let end = Date().addingTimeInterval(2.5)
        while Date() < end {
            let target = app ?? running()
            if let target {
                let list = (CGWindowListCopyWindowInfo([.optionOnScreenOnly, .excludeDesktopElements], kCGNullWindowID) as? [[String: Any]]) ?? []
                if list.contains(where: { ($0[kCGWindowOwnerPID as String] as? pid_t) == target.processIdentifier
                    && ($0[kCGWindowLayer as String] as? Int) == 0 }) { visible += 5 }
                if target.isActive { activated = true }
            }
            try? await Task.sleep(for: .milliseconds(5))
        }
        return (visible, activated)
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
