import SwiftUI

/// The notch silhouette: flat top that flares outward into the menu bar with small concave
/// "ears", straight sides, rounded bottom corners. Both radii animate, so the collapsed and
/// expanded shapes morph into each other.
struct NotchShape: Shape {
    var topRadius: CGFloat
    var bottomRadius: CGFloat

    var animatableData: AnimatablePair<CGFloat, CGFloat> {
        get { AnimatablePair(topRadius, bottomRadius) }
        set { topRadius = newValue.first; bottomRadius = newValue.second }
    }

    func path(in rect: CGRect) -> Path {
        let t = min(topRadius, rect.width / 4)
        let b = min(bottomRadius, (rect.width - 2 * t) / 2, rect.height - t)
        var p = Path()
        p.move(to: CGPoint(x: rect.minX, y: rect.minY))
        // Left ear: concave curve from the top edge down into the side.
        p.addQuadCurve(to: CGPoint(x: rect.minX + t, y: rect.minY + t),
                       control: CGPoint(x: rect.minX + t, y: rect.minY))
        p.addLine(to: CGPoint(x: rect.minX + t, y: rect.maxY - b))
        p.addQuadCurve(to: CGPoint(x: rect.minX + t + b, y: rect.maxY),
                       control: CGPoint(x: rect.minX + t, y: rect.maxY))
        p.addLine(to: CGPoint(x: rect.maxX - t - b, y: rect.maxY))
        p.addQuadCurve(to: CGPoint(x: rect.maxX - t, y: rect.maxY - b),
                       control: CGPoint(x: rect.maxX - t, y: rect.maxY))
        p.addLine(to: CGPoint(x: rect.maxX - t, y: rect.minY + t))
        // Right ear.
        p.addQuadCurve(to: CGPoint(x: rect.maxX, y: rect.minY),
                       control: CGPoint(x: rect.maxX - t, y: rect.minY))
        p.closeSubpath()
        return p
    }
}

/// The floating pill used on a screen without a notch: rounded on every corner.
struct PillShape: Shape {
    var radius: CGFloat

    var animatableData: CGFloat {
        get { radius }
        set { radius = newValue }
    }

    func path(in rect: CGRect) -> Path {
        Path(roundedRect: rect, cornerRadius: min(radius, rect.height / 2), style: .continuous)
    }
}

/// Either silhouette, so views can use one type.
struct NotchSilhouette: Shape {
    var hasNotch: Bool
    var expanded: Bool

    var animatableData: AnimatablePair<CGFloat, CGFloat> {
        get { AnimatablePair(topRadius, bottomRadius) }
        set { topRadius = newValue.first; bottomRadius = newValue.second }
    }

    private(set) var topRadius: CGFloat
    private(set) var bottomRadius: CGFloat

    init(hasNotch: Bool, expanded: Bool) {
        self.hasNotch = hasNotch
        self.expanded = expanded
        topRadius = expanded ? 10 : 6
        bottomRadius = hasNotch ? (expanded ? 26 : 12) : (expanded ? 26 : 16)
    }

    func path(in rect: CGRect) -> Path {
        hasNotch
            ? NotchShape(topRadius: topRadius, bottomRadius: bottomRadius).path(in: rect)
            : PillShape(radius: bottomRadius).path(in: rect)
    }

    /// Horizontal inset of the body from the frame edge (the ears).
    static func earInset(hasNotch: Bool, expanded: Bool) -> CGFloat {
        hasNotch ? (expanded ? 10 : 6) : 0
    }
}
