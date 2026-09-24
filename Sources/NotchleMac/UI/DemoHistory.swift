import AppKit
import NotchleCore

/// Made-up history and covers for the demo and the snapshots (no disk, no network).
@MainActor
enum DemoHistory {
    /// A few days of play: the demo mix (tracks demo0…) and an older, made-up playlist.
    static func entries(now: Date = Date()) -> [HistoryEntry] {
        let demoRef = SourceRef(kind: .playlist, id: "demo")
        let otherRef = SourceRef(kind: .playlist, id: "roadtrip")
        let others: [(String, [String])] = [
            ("Harbour Lights", ["Cassia Moon"]), ("Tin Roof Rain", ["The Low Tides"]),
            ("Blue Motel", ["Ray Callow", "Etta Dune"]), ("Paper Planes Home", ["Mira Sol"]),
            ("Seventeen Summers", ["Aurora Fleet"]), ("Radio Silence", ["Hollis Grey"]),
        ]
        // (hours ago, demo track index or -1-otherIndex, tier or nil, wrong, skips)
        let plan: [(Double, Int, Int?, Int, Int)] = [
            (0.3, 0, 0, 0, 0), (0.4, 1, 1, 1, 0), (0.5, 2, nil, 2, 1), (0.7, 3, 0, 0, 0),
            (0.9, 4, 2, 1, 1), (1.1, 6, 0, 0, 0), (1.3, 7, 1, 0, 1),
            (26, -1, 0, 0, 0), (26.2, -2, nil, 3, 0), (26.4, -3, 1, 1, 0), (26.6, -4, 0, 0, 0),
            (74, -5, 2, 0, 2), (74.3, -6, 0, 0, 0),
        ]
        return plan.enumerated().map { i, p in
            let (hours, index, tier, wrong, skips) = p
            let date = now.addingTimeInterval(-hours * 3600)
            let track: Track
            let name: String
            let ref: SourceRef
            if index >= 0 {
                track = DemoGame.tracks[index]; name = "Notchle Demo Mix"; ref = demoRef
            } else {
                let o = others[-index - 1]
                track = Track(id: "road\(-index)", uri: "spotify:track:road\(-index)", title: o.0,
                              artists: o.1, durationMs: 200_000, previewURL: nil)
                name = "Roadtrip Classics"; ref = otherRef
            }
            let outcome: TrackOutcome = tier.map { .correct(tierIndex: $0) } ?? .missed
            return HistoryEntry(id: UUID(uuidString: String(format: "00000000-0000-0000-0000-%012d", i)) ?? UUID(),
                                date: date, track: track, outcome: outcome, wrongGuesses: wrong, skips: skips,
                                listingName: name, listingRef: ref,
                                artworkURL: i % 5 == 4 ? nil : URL(string: "https://example.invalid/\(track.id).jpg"))
        }.reversed()   // stored oldest first, like the real file
    }

    /// A generated cover per track id: a two-colour gradient with a soft disc.
    static func cover(for trackID: String) -> NSImage? {
        var hash: UInt64 = 1469598103934665603
        for b in trackID.utf8 { hash = (hash ^ UInt64(b)) &* 1099511628211 }
        let h1 = CGFloat(hash % 360) / 360, h2 = CGFloat((hash >> 12) % 360) / 360
        let size = NSSize(width: 128, height: 128)
        return NSImage(size: size, flipped: false) { rect in
            let g = NSGradient(starting: NSColor(hue: h1, saturation: 0.65, brightness: 0.85, alpha: 1),
                               ending: NSColor(hue: h2, saturation: 0.75, brightness: 0.35, alpha: 1))
            g?.draw(in: rect, angle: -60)
            NSColor.white.withAlphaComponent(0.18).setFill()
            NSBezierPath(ovalIn: rect.insetBy(dx: 30, dy: 30).offsetBy(dx: 14, dy: -10)).fill()
            NSColor.black.withAlphaComponent(0.35).setFill()
            NSBezierPath(ovalIn: NSRect(x: 72, y: 38, width: 16, height: 16)).fill()
            return true
        }
    }
}
