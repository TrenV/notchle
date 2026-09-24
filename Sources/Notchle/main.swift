import AppKit
import NotchleMac

// Normal launch runs the game. Debug tools (Sources/NotchleMac/UI/UIDemo.swift):
//   Notchle --ui-demo [--ui-demo-auto] [--ui-quit-after <s>] [--ui-activate] [--ui-start guessing]
//   Notchle --ui-snapshots <dir>     # render every screen to PNG and exit
let app = NSApplication.shared
app.setActivationPolicy(.accessory)
let arguments = Array(CommandLine.arguments.dropFirst())

if let i = arguments.firstIndex(of: "--ui-snapshots"), i + 1 < arguments.count {
    _ = MainActor.assumeIsolated { UISnapshots.renderAll(to: URL(fileURLWithPath: arguments[i + 1])) }
    exit(0)
}

if arguments.contains("--probe-spotify-connect") {
    Task { @MainActor in
        await SpotifyConnectProbe.run()
        exit(0)
    }
    app.run()
} else if arguments.contains("--probe-spotify-window") {
    Task { @MainActor in
        await SpotifyWindowProbe.run()
        exit(0)
    }
    app.run()
} else if arguments.contains("--ui-demo") {
    let demo = MainActor.assumeIsolated { UIDemo(arguments: arguments) }
    MainActor.assumeIsolated { demo.start() }
    app.run()
} else {
    let delegate = MainActor.assumeIsolated { AppDelegate() }
    app.delegate = delegate
    app.run()
}
