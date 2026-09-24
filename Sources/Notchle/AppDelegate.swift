import AppKit
import NotchleMac

/// No Dock icon (LSUIElement), so a small menu-bar item is the way to quit or reset.
@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    private var coordinator: AppCoordinator?
    private var panel: NotchPanelController?
    private var statusItem: NSStatusItem?

    func applicationDidFinishLaunching(_ notification: Notification) {
        let coordinator = AppCoordinator(spotifyConnect: AppCoordinator.sharedSpotifyConnect)
        let panel = NotchPanelController(model: coordinator.model)
        panel.show()
        self.coordinator = coordinator
        self.panel = panel
        installStatusItem()
    }

    private func installStatusItem() {
        let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        item.button?.image = NSImage(systemSymbolName: "music.note", accessibilityDescription: "Notchle")
        let menu = NSMenu()
        menu.addItem(withTitle: "New link…", action: #selector(newLink), keyEquivalent: "").target = self
        menu.addItem(withTitle: "Reset progress", action: #selector(resetProgress), keyEquivalent: "").target = self
        menu.addItem(.separator())
        menu.addItem(withTitle: "Quit Notchle", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q")
        item.menu = menu
        statusItem = item
    }

    @objc private func newLink() {
        coordinator?.send(.reset)
        panel?.show()
    }

    @objc private func resetProgress() {
        let alert = NSAlert()
        alert.messageText = "Reset progress?"
        alert.informativeText = "Songs from sets you already cleared can come back."
        alert.addButton(withTitle: "Reset")
        alert.addButton(withTitle: "Cancel")
        NSApp.activate()
        if alert.runModal() == .alertFirstButtonReturn {
            coordinator?.resetProgress()
        }
    }
}
