import CoreGraphics

/// Where the notch shape hangs on a screen. Pure maths over the rects `NSScreen` reports, so it
/// is unit-testable without a display. All rects are AppKit screen coordinates (origin bottom-left).
public struct NotchGeometry: Sendable, Equatable {
    /// The hardware notch, nil on a screen without one (external display).
    public let hardwareNotch: CGRect?
    /// Size of the "notch" the collapsed shape is built around: the real one, or a same-looking
    /// stand-in for the floating pill on a screen without a notch.
    public let notchSize: CGSize
    /// Horizontal centre of the shape.
    public let centerX: CGFloat
    /// The y the shape's top edge hangs from: the very top of the screen with a notch,
    /// just under the menu bar without one.
    public let anchorTop: CGFloat

    public var hasNotch: Bool { hardwareNotch != nil }

    /// Stand-in notch size when the screen has none (matches a 14"/16" MacBook Pro notch).
    public static let fallbackNotchSize = CGSize(width: 185, height: 32)
    /// Gap between the menu bar and the floating pill on a screen without a notch.
    public static let pillGap: CGFloat = 6

    public init(hardwareNotch: CGRect?, notchSize: CGSize, centerX: CGFloat, anchorTop: CGFloat) {
        self.hardwareNotch = hardwareNotch
        self.notchSize = notchSize
        self.centerX = centerX
        self.anchorTop = anchorTop
    }

    /// Derives the geometry from what `NSScreen` reports.
    /// - Notch width = screen width − auxiliaryTopLeftArea.width − auxiliaryTopRightArea.width,
    ///   notch height = safeAreaInsets.top, and the notch starts where the left area ends.
    /// - Without a notch (no safe-area inset or no auxiliary areas) the shape becomes a pill
    ///   centred under the menu bar (`visibleFrameMaxY` is the menu bar's bottom edge).
    public static func detect(screenFrame: CGRect, safeAreaTop: CGFloat,
                              auxiliaryTopLeft: CGRect?, auxiliaryTopRight: CGRect?,
                              visibleFrameMaxY: CGFloat) -> NotchGeometry {
        if safeAreaTop > 0, let left = auxiliaryTopLeft, let right = auxiliaryTopRight {
            let width = screenFrame.width - left.width - right.width
            if width > 0 {
                let notch = CGRect(x: screenFrame.minX + left.width, y: screenFrame.maxY - safeAreaTop,
                                   width: width, height: safeAreaTop)
                return NotchGeometry(hardwareNotch: notch, notchSize: notch.size,
                                     centerX: notch.midX, anchorTop: screenFrame.maxY)
            }
        }
        let top = min(visibleFrameMaxY, screenFrame.maxY) - pillGap
        return NotchGeometry(hardwareNotch: nil, notchSize: fallbackNotchSize,
                             centerX: screenFrame.midX, anchorTop: top)
    }

    /// Index of the screen to use: the first one with a notch, else the main screen, else the first.
    public static func preferredScreenIndex(hasNotch: [Bool], mainIndex: Int?) -> Int? {
        if let i = hasNotch.firstIndex(of: true) { return i }
        if let m = mainIndex, hasNotch.indices.contains(m) { return m }
        return hasNotch.isEmpty ? nil : 0
    }
}

/// Fixed sizes of the notch UI.
public enum NotchMetrics {
    /// Extra width on each side of the notch when collapsed (glyph left, progress right).
    public static let collapsedWing: CGFloat = 50
    public static let expandedSize = CGSize(width: 468, height: 178)
    /// The panel is larger than the expanded shape so confetti can fall out below it.
    /// Everything outside the visible shape lets clicks through.
    public static let panelSize = CGSize(width: 760, height: 440)
    /// Hover slack around the shape, so the edge does not flicker.
    public static let hoverSlack: CGFloat = 6

    public static func collapsedSize(_ g: NotchGeometry) -> CGSize {
        CGSize(width: g.notchSize.width + 2 * collapsedWing, height: g.notchSize.height)
    }

    public static func shapeSize(_ g: NotchGeometry, expanded: Bool) -> CGSize {
        expanded ? expandedSize : collapsedSize(g)
    }

    /// Panel frame in screen coordinates: top-centred on the anchor.
    public static func panelFrame(_ g: NotchGeometry) -> CGRect {
        CGRect(x: g.centerX - panelSize.width / 2, y: g.anchorTop - panelSize.height,
               width: panelSize.width, height: panelSize.height)
    }

    /// The visible shape in screen coordinates.
    public static func shapeFrame(_ g: NotchGeometry, expanded: Bool) -> CGRect {
        let s = shapeSize(g, expanded: expanded)
        return CGRect(x: g.centerX - s.width / 2, y: g.anchorTop - s.height, width: s.width, height: s.height)
    }

    /// The area that takes the mouse: the shape plus a little slack. With a notch the slack
    /// also extends above the screen top so the very top pixel row counts.
    public static func hoverFrame(_ g: NotchGeometry, expanded: Bool) -> CGRect {
        shapeFrame(g, expanded: expanded).insetBy(dx: -hoverSlack, dy: -hoverSlack)
    }
}
