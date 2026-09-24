import AppKit
import NotchleCore
import NotchleMac

// Placeholder entry point; the integrator replaces this with the AppCoordinator in Wave 3.
// For now it runs the notch UI demo harness (Sources/NotchleMac/UI/UIDemo.swift):
//   swift run Notchle [--ui-demo-auto] [--ui-quit-after <s>] [--ui-activate] [--ui-start guessing]
//   swift run Notchle --ui-snapshots <dir>     # render every screen to PNG and exit
let app = NSApplication.shared
app.setActivationPolicy(.accessory)
let arguments = Array(CommandLine.arguments.dropFirst())

if let i = arguments.firstIndex(of: "--ui-snapshots"), i + 1 < arguments.count {
    UISnapshots.renderAll(to: URL(fileURLWithPath: arguments[i + 1]))
    exit(0)
}

let demo = UIDemo(arguments: arguments)
demo.start()
app.run()
