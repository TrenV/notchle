import AppKit
import NotchleCore

// Pure layer of the Music.app player: AppleScript source, output parsing and library matching.
// Checked against Music 1.6.6's dictionary (/System/Applications/Music.app/Contents/Resources/
// com.apple.Music.sdef, macOS 26.6): Music can `play` a *library* track, but it has no command
// that plays or adds an Apple Music catalog song by id (`add` takes files; `open location` opens
// the store page in Music's window, which would show the title). So this player plays songs the
// user has added to their library (Apple Music subscribers: with Sync Library on, added catalog
// songs stream in full), found by title + artist.

enum AppleMusicScripts {
    static let bundleID = "com.apple.Music"

    private static func tell(_ body: String) -> String {
        "with timeout of \(SpotifyScripts.eventTimeout) seconds\ntell application id \(SpotifyScripts.quoted(bundleID))\n\(body)\nend tell\nend timeout"
    }

    /// One line per library song whose name contains `term`: "databaseID<TAB>name<TAB>artist<TAB>seconds".
    /// A plain object query: unlike `search`, which the dictionary calls "identical to entering
    /// search text in the Search field", it has no UI counterpart.
    static func candidates(containing term: String) -> String {
        tell("""
            set out to ""
            set hits to (every track of library playlist 1 whose name contains \(SpotifyScripts.quoted(term)))
            repeat with t in hits
                try
                    set out to out & (database ID of t as string) & tab & (name of t) & tab & (artist of t) & tab & (duration of t as string) & linefeed
                end try
            end repeat
            return out
            """)
    }

    static func play(databaseID: Int) -> String {
        tell("play (first track of library playlist 1 whose database ID is \(databaseID))")
    }

    static func setPosition(_ seconds: Double) -> String {
        tell("set player position to \(SpotifyScripts.number(max(0, seconds)))")
    }

    static let pause = tell("pause")
    static let resume = tell("play")

    /// "state<LF>database ID<LF>position", like `SpotifyScripts.status` (id "" when nothing is loaded).
    static let status = tell("""
        set s to "stopped"
        if player state is playing then
            set s to "playing"
        else if player state is paused then
            set s to "paused"
        end if
        set i to ""
        try
            set i to (database ID of current track) as string
        end try
        set p to 0
        try
            set p to player position
        end try
        return s & linefeed & i & linefeed & (p as string)
        """)
}

/// A library song as `AppleMusicScripts.candidates` lists it.
struct LibrarySong: Sendable, Hashable {
    let databaseID: Int
    let name: String
    let artist: String
    let seconds: Double

    static func parse(_ output: String) -> [LibrarySong] {
        output.split(whereSeparator: \.isNewline).compactMap { line in
            let f = line.split(separator: "\t", omittingEmptySubsequences: false).map(String.init)
            guard f.count == 4, let id = Int(f[0].trimmingCharacters(in: .whitespaces)) else { return nil }
            return LibrarySong(databaseID: id, name: f[1], artist: f[2],
                               seconds: Double(f[3].replacingOccurrences(of: ",", with: ".")) ?? 0)
        }
    }
}

enum AppleMusicMatch {
    /// The text the library query looks for: the title without brackets or " - Remastered…"
    /// suffixes, so "So Good" finds "So Good (feat. Kendrick Lamar)" and vice versa.
    static func searchTerm(for title: String) -> String {
        var t = title
        if let r = t.range(of: " - ") { t = String(t[..<r.lowerBound]) }
        if let i = t.firstIndex(where: { $0 == "(" || $0 == "[" }) { t = String(t[..<i]) }
        let trimmed = t.trimmingCharacters(in: .whitespaces)
        return trimmed.isEmpty ? title : trimmed
    }

    static func normalized(_ s: String) -> String {
        String(s.folding(options: [.caseInsensitive, .diacriticInsensitive, .widthInsensitive], locale: nil)
            .map { $0.isLetter || $0.isNumber ? $0 : " " })
            .split(separator: " ").joined(separator: " ")
    }

    /// The library song that is `track`: same core title, the first credited artist named in the
    /// library artist, and (when both are known) a duration within 10 s; closest duration wins.
    static func best(_ candidates: [LibrarySong], for track: Track) -> LibrarySong? {
        let title = normalized(searchTerm(for: track.title))
        let lead = normalized(track.artists.first ?? "")
        let wanted = Double(track.durationMs) / 1000
        let matches = candidates.filter { c in
            normalized(searchTerm(for: c.name)) == title
                && (lead.isEmpty || " \(normalized(c.artist)) ".contains(" \(lead) "))
                && (wanted == 0 || c.seconds == 0 || abs(c.seconds - wanted) <= 10)
        }
        return matches.min { abs($0.seconds - wanted) < abs($1.seconds - wanted) }
    }
}

/// What `AppleMusicScripts.status` reported.
struct MusicStatus: Sendable, Hashable {
    let isPlaying: Bool
    let databaseID: Int?
    let position: Double

    init?(parsing output: String) {
        let lines = output.split(separator: "\n", omittingEmptySubsequences: false)
            .map { $0.trimmingCharacters(in: .whitespacesAndNewlines) }
        guard lines.count == 3, ["playing", "paused", "stopped"].contains(lines[0]) else { return nil }
        isPlaying = lines[0] == "playing"
        databaseID = Int(lines[1])
        position = Double(lines[2].replacingOccurrences(of: ",", with: ".")) ?? 0
    }
}

/// Launching and hiding Music.app (same seam as Spotify's, so the fakes are shared).
@MainActor
final class WorkspaceMusicControl: SpotifyAppControlling {
    nonisolated init() {}

    private var appURL: URL? { NSWorkspace.shared.urlForApplication(withBundleIdentifier: AppleMusicScripts.bundleID) }
    private var runningApps: [NSRunningApplication] {
        NSRunningApplication.runningApplications(withBundleIdentifier: AppleMusicScripts.bundleID).filter { !$0.isTerminated }
    }

    var isInstalled: Bool { appURL != nil }
    var isRunning: Bool { !runningApps.isEmpty }

    func launchHidden() async throws {
        guard let appURL else { throw PlayerError.unavailable("Music isn't installed") }
        let configuration = NSWorkspace.OpenConfiguration()
        configuration.activates = false
        configuration.hides = true
        configuration.addsToRecentItems = false
        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, any Error>) in
            NSWorkspace.shared.openApplication(at: appURL, configuration: configuration) { _, error in
                if let error {
                    continuation.resume(throwing: PlayerError.failed("Couldn't launch Music: \(error.localizedDescription)"))
                } else {
                    continuation.resume()
                }
            }
        }
    }

    func hide() {
        for app in runningApps { app.hide() }
    }
}
