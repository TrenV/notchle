import CoreGraphics
import Testing
@testable import NotchleMac

@Suite struct UIGeometryTests {
    // What NSScreen reports on this Mac17,8 (16" MacBook Pro), measured 2026-09-24.
    let frame = CGRect(x: 0, y: 0, width: 1728, height: 1117)
    let left = CGRect(x: 0, y: 1085, width: 771, height: 32)
    let right = CGRect(x: 956, y: 1085, width: 772, height: 32)

    @Test func notchFromAuxiliaryAreas() {
        let g = NotchGeometry.detect(screenFrame: frame, safeAreaTop: 32, auxiliaryTopLeft: left,
                                     auxiliaryTopRight: right, visibleFrameMaxY: 1085)
        #expect(g.hasNotch)
        #expect(g.hardwareNotch == CGRect(x: 771, y: 1085, width: 185, height: 32))
        #expect(g.notchSize == CGSize(width: 185, height: 32))
        #expect(g.centerX == 863.5)
        #expect(g.anchorTop == 1117)
    }

    @Test func notchOnASecondaryScreenIsOffsetByItsOrigin() {
        let f = CGRect(x: -1512, y: 200, width: 1512, height: 982)
        let g = NotchGeometry.detect(screenFrame: f, safeAreaTop: 32,
                                     auxiliaryTopLeft: CGRect(x: -1512, y: 1150, width: 662, height: 32),
                                     auxiliaryTopRight: CGRect(x: -662 + 0, y: 1150, width: 662, height: 32),
                                     visibleFrameMaxY: 1150)
        #expect(g.hardwareNotch == CGRect(x: -850, y: 1150, width: 188, height: 32))
        #expect(g.anchorTop == 1182)
    }

    @Test func noNotchGivesAPillUnderTheMenuBar() {
        let ext = CGRect(x: 1728, y: -323, width: 3440, height: 1440)
        let g = NotchGeometry.detect(screenFrame: ext, safeAreaTop: 0, auxiliaryTopLeft: nil,
                                     auxiliaryTopRight: nil, visibleFrameMaxY: 1117 - 24)
        #expect(!g.hasNotch)
        #expect(g.notchSize == NotchGeometry.fallbackNotchSize)
        #expect(g.centerX == ext.midX)
        #expect(g.anchorTop == 1093 - NotchGeometry.pillGap)
    }

    @Test func safeAreaWithoutAuxiliaryAreasIsNoNotch() {
        let g = NotchGeometry.detect(screenFrame: frame, safeAreaTop: 32, auxiliaryTopLeft: nil,
                                     auxiliaryTopRight: right, visibleFrameMaxY: 1085)
        #expect(!g.hasNotch)
    }

    @Test func prefersTheNotchScreen() {
        #expect(NotchGeometry.preferredScreenIndex(hasNotch: [false, true], mainIndex: 0) == 1)
        #expect(NotchGeometry.preferredScreenIndex(hasNotch: [false, false], mainIndex: 1) == 1)
        #expect(NotchGeometry.preferredScreenIndex(hasNotch: [false], mainIndex: nil) == 0)
        #expect(NotchGeometry.preferredScreenIndex(hasNotch: [], mainIndex: nil) == nil)
    }

    @Test func panelAndShapeHangFromTheNotch() {
        let g = NotchGeometry.detect(screenFrame: frame, safeAreaTop: 32, auxiliaryTopLeft: left,
                                     auxiliaryTopRight: right, visibleFrameMaxY: 1085)
        let panel = NotchMetrics.panelFrame(g)
        #expect(panel.maxY == 1117)
        #expect(panel.midX == g.centerX)

        let collapsed = NotchMetrics.shapeFrame(g, expanded: false)
        #expect(collapsed.width == 185 + 2 * NotchMetrics.collapsedWing)
        #expect(collapsed.height == 32)
        #expect(collapsed.maxY == 1117)
        #expect(collapsed.midX == g.centerX)
        #expect(collapsed.contains(CGPoint(x: 771 + 1, y: 1100)))    // over the hardware notch

        let expanded = NotchMetrics.shapeFrame(g, expanded: true)
        #expect(expanded.size == NotchMetrics.expandedSize)
        #expect(expanded.maxY == 1117)
        #expect(panel.contains(expanded))
    }

    @Test func hoverAreaIsTheShapePlusSlackOnly() {
        let g = NotchGeometry.detect(screenFrame: frame, safeAreaTop: 32, auxiliaryTopLeft: left,
                                     auxiliaryTopRight: right, visibleFrameMaxY: 1085)
        let hover = NotchMetrics.hoverFrame(g, expanded: false)
        #expect(hover.contains(CGPoint(x: g.centerX, y: 1116)))
        // Menu bar items right next to the collapsed shape stay clickable.
        #expect(!hover.contains(CGPoint(x: 600, y: 1100)))
        #expect(!hover.contains(CGPoint(x: 1100, y: 1100)))
        // Below the collapsed shape is not hover.
        #expect(!hover.contains(CGPoint(x: g.centerX, y: 1117 - 32 - 20)))
    }
}
