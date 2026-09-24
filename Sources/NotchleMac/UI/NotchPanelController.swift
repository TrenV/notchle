import AppKit
import NotchleCore

// STUB — Wave 2, agent D replaces this file (and adds the SwiftUI views next to it).
/// Owns the borderless panel that sits over the notch and hosts the SwiftUI root view.
@MainActor
public final class NotchPanelController {
    public let model: NotchViewModel

    public init(model: NotchViewModel) {
        self.model = model
    }

    public func show() {}
}
