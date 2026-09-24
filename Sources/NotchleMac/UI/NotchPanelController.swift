import AppKit
import Observation
import SwiftUI
import NotchleCore

/// Owns the borderless panel that sits over the notch and hosts the SwiftUI root view.
///
/// - Places the panel on the notch screen (or a pill under the menu bar without a notch) and
///   follows screen changes.
/// - Only the visible shape takes the mouse: the panel ignores mouse events everywhere else,
///   toggled from the pointer position, so the menu bar stays clickable around it.
/// - Handles Return/Esc/Tab/⌘R and the edit shortcuts (an accessory app has no Edit menu, so
///   ⌘V would otherwise not paste into the URL field).
@MainActor
public final class NotchPanelController {
    public let model: NotchViewModel
    public let ui: NotchUIState
    public private(set) var geometry: NotchGeometry?

    /// How the panel takes the keyboard when a phase wants typing.
    public enum KeyboardStrategy: Sendable {
        /// `orderFrontRegardless` + `makeKey` on the non-activating panel; the app stays inactive.
        case makeKey
        /// Same, plus `NSApp.activate()`.
        case makeKeyAndActivate
    }
    public var keyboardStrategy: KeyboardStrategy = .makeKey
    /// Diagnostic lines (screen detection, key status). The demo prints them.
    public var log: (String) -> Void = { _ in }

    private var panel: NotchPanel?
    private var hosting: NotchHostingView<NotchRootView>?
    private var lastState: GameState?
    private var monitors: [Any] = []
    private var observers: [NSObjectProtocol] = []

    public init(model: NotchViewModel) {
        self.model = model
        self.ui = NotchUIState(model: model)
    }

    public func show() {
        if panel == nil { build() }
        reposition()
        panel?.orderFrontRegardless()
        stateChanged()
        observeState()
        observeExpansion()
        updateMouse(at: NSEvent.mouseLocation)
    }

    // MARK: Setup

    private func build() {
        let panel = NotchPanel(contentRect: NSRect(origin: .zero, size: NotchMetrics.panelSize))
        let placeholder = NotchGeometry(hardwareNotch: nil, notchSize: NotchGeometry.fallbackNotchSize,
                                        centerX: 0, anchorTop: 0)
        let hosting = NotchHostingView(rootView: NotchRootView(ui: ui, geometry: placeholder))
        hosting.frame = NSRect(origin: .zero, size: NotchMetrics.panelSize)
        hosting.autoresizingMask = [.width, .height]
        panel.contentView = hosting
        panel.ignoresMouseEvents = true
        self.panel = panel
        self.hosting = hosting

        ui.onWantsKeyboard = { [weak self] in self?.takeKeyboard() }

        observers.append(NotificationCenter.default.addObserver(
            forName: NSApplication.didChangeScreenParametersNotification, object: nil, queue: .main
        ) { [weak self] _ in
            MainActor.assumeIsolated { self?.reposition() }
        })
        observers.append(NotificationCenter.default.addObserver(
            forName: NSWindow.didBecomeKeyNotification, object: panel, queue: .main
        ) { [weak self] _ in
            MainActor.assumeIsolated { self?.log("panel became key (app active: \(NSApp.isActive))") }
        })
        observers.append(NotificationCenter.default.addObserver(
            forName: NSWindow.didResignKeyNotification, object: panel, queue: .main
        ) { [weak self] _ in
            MainActor.assumeIsolated { self?.log("panel resigned key") }
        })

        let mouseEvents: NSEvent.EventTypeMask = [.mouseMoved, .leftMouseDragged, .leftMouseDown, .leftMouseUp]
        if let m = NSEvent.addGlobalMonitorForEvents(matching: mouseEvents, handler: { _ in
            MainActor.assumeIsolated { [weak self] in self?.updateMouse(at: NSEvent.mouseLocation) }
        }) { monitors.append(m) }
        if let m = NSEvent.addLocalMonitorForEvents(matching: mouseEvents, handler: { event in
            MainActor.assumeIsolated { [weak self] in self?.updateMouse(at: NSEvent.mouseLocation) }
            return event
        }) { monitors.append(m) }
        if let m = NSEvent.addLocalMonitorForEvents(matching: .keyDown, handler: { event in
            // Local monitors run on the main thread.
            nonisolated(unsafe) let e = event
            let consumed = MainActor.assumeIsolated { [weak self] in self?.handleKeyDown(e) ?? false }
            return consumed ? nil : event
        }) { monitors.append(m) }
    }

    /// Finds the notch screen (or falls back to the main screen) and places the panel.
    public func reposition() {
        let screens = NSScreen.screens
        let index = NotchGeometry.preferredScreenIndex(
            hasNotch: screens.map { $0.safeAreaInsets.top > 0 },
            mainIndex: NSScreen.main.flatMap { main in screens.firstIndex(of: main) })
        guard let index, let panel, let hosting else { return }
        let screen = screens[index]
        let g = NotchGeometry.detect(screenFrame: screen.frame, safeAreaTop: screen.safeAreaInsets.top,
                                     auxiliaryTopLeft: screen.auxiliaryTopLeftArea,
                                     auxiliaryTopRight: screen.auxiliaryTopRightArea,
                                     visibleFrameMaxY: screen.visibleFrame.maxY)
        if g != geometry {
            geometry = g
            hosting.rootView = NotchRootView(ui: ui, geometry: g)
            let described = g.hardwareNotch.map { "notch \(NSStringFromRect($0))" } ?? "no notch, pill under the menu bar"
            log("screen \"\(screen.localizedName)\" frame \(NSStringFromRect(screen.frame)): \(described)")
        }
        let frame = NotchMetrics.panelFrame(g)
        panel.setFrame(frame, display: true)
        log("panel frame \(NSStringFromRect(frame))")
    }

    // MARK: Game state

    private func observeState() {
        withObservationTracking { _ = model.state } onChange: { [weak self] in
            Task { @MainActor [weak self] in
                self?.stateChanged()
                self?.observeState()
            }
        }
    }

    private func observeExpansion() {
        withObservationTracking { _ = ui.isExpanded } onChange: { [weak self] in
            Task { @MainActor [weak self] in
                guard let self else { return }
                self.updateMouse(at: NSEvent.mouseLocation)
                self.observeExpansion()
            }
        }
    }

    private func stateChanged() {
        let new = model.state
        guard new != lastState else { return }
        ui.stateDidChange(from: lastState, to: new)
        lastState = new
    }

    // MARK: Keyboard

    /// Makes the panel key so the focused field takes typing without a click first.
    public func takeKeyboard() {
        guard let panel else { return }
        panel.orderFrontRegardless()
        panel.makeKey()
        if keyboardStrategy == .makeKeyAndActivate { NSApp.activate() }
        ui.requestFocus(ui.requestedFocus)
        // Log once focus has settled (the field may only appear with the new phase view).
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.3) { [weak self] in
            guard let self, let panel = self.panel else { return }
            let responder = panel.firstResponder.map { String(describing: type(of: $0)) } ?? "nil"
            let front = NSWorkspace.shared.frontmostApplication?.localizedName ?? "?"
            self.log("takeKeyboard(\(self.keyboardStrategy)) +0.3s: level=\(panel.level.rawValue) isKey=\(panel.isKeyWindow) appActive=\(NSApp.isActive) frontmost=\(front) firstResponder=\(responder) focus=\(String(describing: self.ui.focusedField))")
        }
    }

    /// Hands the keyboard back after an Esc collapse.
    private func releaseKeyboard() {
        guard let panel, panel.isKeyWindow else { return }
        if NSApp.isActive { NSApp.deactivate() }
        panel.orderOut(nil)
        panel.orderFrontRegardless()
    }

    private func handleKeyDown(_ event: NSEvent) -> Bool {
        guard let panel, event.window === panel else { return false }
        let flags = event.modifierFlags.intersection(.deviceIndependentFlagsMask)
        if let selector = Self.editAction(keyCode: event.keyCode, characters: event.charactersIgnoringModifiers,
                                          modifiers: flags) {
            return NSApp.sendAction(selector, to: nil, from: panel)
        }
        // Let an input method finish composing (Return confirms the composition).
        if let editor = panel.firstResponder as? NSTextView, editor.hasMarkedText() { return false }
        guard let key = Self.keyInput(keyCode: event.keyCode, characters: event.charactersIgnoringModifiers,
                                      modifiers: flags),
              let command = NotchUIRules.command(for: key, phase: model.state.phase, focused: ui.focusedField,
                                                 settingsOpen: ui.showingSettings)
        else { return false }
        ui.perform(command)
        if command == .collapse { releaseKeyboard() }
        return true
    }

    /// Maps a key event to the keys the notch handles. Pure, for tests.
    nonisolated public static func keyInput(keyCode: UInt16, characters: String?, modifiers: NSEvent.ModifierFlags) -> UIKeyInput? {
        let mods = modifiers.intersection([.command, .option, .control, .shift])
        switch keyCode {
        case 36, 76: return mods.isEmpty ? .returnKey : nil       // Return, keypad Enter
        case 53: return mods.isEmpty ? .escape : nil               // Esc
        case 48:                                                   // Tab
            if mods.isEmpty { return .tab }
            return mods == .shift ? .backTab : nil
        default:
            if mods == .command, characters?.lowercased() == "r" { return .commandR }
            return nil
        }
    }

    /// ⌘X/⌘C/⌘V/⌘A/⌘Z/⇧⌘Z → the standard edit actions (there is no Edit menu to provide them).
    nonisolated public static func editAction(keyCode: UInt16, characters: String?, modifiers: NSEvent.ModifierFlags) -> Selector? {
        let mods = modifiers.intersection([.command, .option, .control, .shift])
        switch (mods, characters?.lowercased()) {
        case (.command, "x"): return #selector(NSText.cut(_:))
        case (.command, "c"): return #selector(NSText.copy(_:))
        case (.command, "v"): return #selector(NSText.paste(_:))
        case (.command, "a"): return #selector(NSText.selectAll(_:))
        case (.command, "z"): return Selector(("undo:"))
        case ([.command, .shift], "z"): return Selector(("redo:"))
        default: return nil
        }
    }

    // MARK: Mouse

    /// Only the shape takes the mouse; everywhere else the panel lets clicks through.
    private func updateMouse(at point: NSPoint) {
        guard let panel, let geometry else { return }
        let inside = NotchMetrics.hoverFrame(geometry, expanded: ui.isExpanded).contains(point)
        if panel.ignoresMouseEvents == inside { panel.ignoresMouseEvents = !inside }
        ui.hoverChanged(inside)
    }
}
