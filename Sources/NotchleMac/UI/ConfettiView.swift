import SwiftUI

/// Deterministic confetti physics (seeded), so a burst is cheap to draw and testable.
enum Confetti {
    static let count = 80
    static let duration: TimeInterval = 1.5
    static let gravity: CGFloat = 820

    struct Particle: Equatable {
        var velocity: CGVector
        var size: CGSize
        var hue: Int
        var spin: Double
        var flutter: Double
        var isDot: Bool
        var delay: Double
    }

    static let colors: [Color] = [
        Color(red: 0.12, green: 0.84, blue: 0.38), Color(red: 1.0, green: 0.8, blue: 0.2),
        Color(red: 1.0, green: 0.36, blue: 0.45), Color(red: 0.35, green: 0.65, blue: 1.0),
        Color(red: 0.75, green: 0.45, blue: 1.0), Color(red: 1.0, green: 0.55, blue: 0.2),
        .white,
    ]

    /// SplitMix64: tiny, seedable, good enough for sprinkles.
    struct RNG {
        var state: UInt64
        mutating func next() -> UInt64 {
            state &+= 0x9E37_79B9_7F4A_7C15
            var z = state
            z = (z ^ (z >> 30)) &* 0xBF58_476D_1CE4_E5B9
            z = (z ^ (z >> 27)) &* 0x94D0_49BB_1331_11EB
            return z ^ (z >> 31)
        }
        mutating func unit() -> Double { Double(next() >> 11) / Double(1 << 53) }
        mutating func range(_ r: ClosedRange<Double>) -> Double { r.lowerBound + (r.upperBound - r.lowerBound) * unit() }
    }

    static func particles(seed: UInt64, count: Int = count) -> [Particle] {
        var rng = RNG(state: seed)
        return (0..<count).map { _ in
            // Mostly downward and sideways, a few thrown up first: "falling out of the notch".
            let angle = rng.range(-0.15 * .pi ... 1.15 * .pi)
            let speed = rng.range(160...460)
            let dot = rng.unit() < 0.25
            let w = rng.range(4...7)
            return Particle(velocity: CGVector(dx: cos(angle) * speed, dy: sin(angle) * speed * 0.8 + 40),
                            size: dot ? CGSize(width: w * 0.8, height: w * 0.8) : CGSize(width: w, height: w * 1.9),
                            hue: Int(rng.next() % UInt64(colors.count)),
                            spin: rng.range(-9...9),
                            flutter: rng.range(6...14),
                            isDot: dot,
                            delay: rng.range(0...0.12))
        }
    }

    /// Position of a particle `t` seconds into the burst, relative to the origin (y grows down).
    static func offset(_ p: Particle, at t: Double) -> CGPoint {
        let t = max(0, t - p.delay)
        let drag = 1 - min(0.45, t * 0.3)
        return CGPoint(x: p.velocity.dx * t * drag, y: p.velocity.dy * t + 0.5 * gravity * t * t)
    }

    static func opacity(at t: Double) -> Double {
        let fadeStart = duration * 0.65
        return t < fadeStart ? 1 : max(0, 1 - (t - fadeStart) / (duration - fadeStart))
    }
}

/// ~1.5s burst of ~80 colourful particles from `origin` (the notch). Draws nothing when idle.
struct ConfettiView: View {
    let start: Date?
    let seed: UInt64
    let origin: CGPoint

    var body: some View {
        if let start, Date().timeIntervalSince(start) < Confetti.duration {
            let particles = Confetti.particles(seed: seed)
            TimelineView(.animation) { context in
                let t = context.date.timeIntervalSince(start)
                Canvas { ctx, _ in
                    guard t < Confetti.duration else { return }
                    ctx.opacity = Confetti.opacity(at: t)
                    for p in particles where t >= p.delay {
                        let o = Confetti.offset(p, at: t)
                        var c = ctx
                        c.translateBy(x: origin.x + o.x, y: origin.y + o.y)
                        c.rotate(by: .radians(p.spin * t))
                        // Flutter: squash the width to fake a tumbling paper strip.
                        c.scaleBy(x: p.isDot ? 1 : CGFloat(abs(cos(p.flutter * t))) * 0.8 + 0.2, y: 1)
                        let rect = CGRect(x: -p.size.width / 2, y: -p.size.height / 2,
                                          width: p.size.width, height: p.size.height)
                        let shape = p.isDot ? Path(ellipseIn: rect) : Path(roundedRect: rect, cornerRadius: 1)
                        c.fill(shape, with: .color(Confetti.colors[p.hue]))
                    }
                }
            }
            .id(start)
        }
    }
}

/// Reduce Motion alternative: a soft green glow that swells and fades once around the shape.
struct CelebrationGlow: View {
    let start: Date?
    let silhouette: NotchSilhouette
    static let duration: TimeInterval = 1.4
    static let green = Color(red: 0.12, green: 0.84, blue: 0.38)

    var body: some View {
        if let start, Date().timeIntervalSince(start) < Self.duration {
            TimelineView(.animation) { context in
                let t = min(1, max(0, context.date.timeIntervalSince(start) / Self.duration))
                let strength = sin(t * .pi)
                // Stacked soft strokes rather than a blur filter: renders the same everywhere.
                ZStack {
                    ForEach(0..<6, id: \.self) { i in
                        silhouette
                            .stroke(Self.green.opacity(0.22 - Double(i) * 0.03), lineWidth: CGFloat(4 + i * 5))
                    }
                }
                .opacity(strength)
            }
            .id(start)
        }
    }
}
