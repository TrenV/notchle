import Foundation
import NotchleCore

// Pure layer of the Spotify-app player: AppleScript source builders, status parsing and the
// mapping from AppleScript error numbers to `PlayerError`. No Apple Events happen here, so
// everything in this file is unit-tested (Tests/NotchleMacTests/PlaybackSpotifyScriptsTests).

/// An AppleScript run that failed, as reported by `NSAppleScript`'s error dictionary.
struct AppleScriptFailure: Error, Sendable, Hashable {
    /// `NSAppleScript.errorNumber`, e.g. -1743 (not authorised) or -600 (app not running).
    let number: Int
    let message: String
}

enum SpotifyScripts {
    /// Scripts address the app by bundle id, so a renamed or localised app bundle still works.
    static let bundleID = "com.spotify.client"

    /// An AppleScript string literal holding `text`. Backslash and double quote are the only
    /// characters with meaning inside an AppleScript string, so escaping those two keeps any
    /// input inside the literal (no way to break out and run other commands).
    static func quoted(_ text: String) -> String {
        var escaped = ""
        escaped.reserveCapacity(text.count + 2)
        for character in text {
            switch character {
            case "\\": escaped += "\\\\"
            case "\"": escaped += "\\\""
            default: escaped.append(character)
            }
        }
        return "\"\(escaped)\""
    }

    /// A real number as an AppleScript literal. AppleScript source always uses "." as the
    /// decimal separator, whatever the user's locale; `String(format:)` without a locale does too.
    static func number(_ value: Double) -> String {
        let finite = value.isFinite ? value : 0
        return String(format: "%.3f", finite)
    }

    /// Seconds an Apple Event may wait for Spotify's answer before failing with -1712
    /// (AppleScript's default is 120 s, far too long to hang a snippet on).
    static let eventTimeout = 10

    private static func withTimeout(_ body: String) -> String {
        "with timeout of \(eventTimeout) seconds\n\(body)\nend timeout"
    }

    private static func tell(_ body: String) -> String {
        withTimeout("tell application id \(quoted(bundleID)) to \(body)")
    }

    static func play(uri: String) -> String { tell("play track \(quoted(uri))") }
    static func setPosition(_ seconds: Double) -> String { tell("set player position to \(number(max(0, seconds)))") }
    static let pause = tell("pause")
    static let resume = tell("play")

    /// Answers "state<LF>track uri<LF>position". Also the "is Spotify ready?" probe after a launch.
    /// The state is spelled out rather than coerced (`player state as string`) so the output does
    /// not depend on how the enumeration coerces; missing track/position degrade to ""/0.
    static let status = withTimeout("""
        tell application id \(quoted(bundleID))
            set s to "stopped"
            if player state is playing then
                set s to "playing"
            else if player state is paused then
                set s to "paused"
            end if
            set i to ""
            try
                set i to id of current track
            end try
            set p to 0
            try
                set p to player position
            end try
            return s & linefeed & i & linefeed & (p as string)
        end tell
        """)
}

/// What `SpotifyScripts.status` reported.
struct SpotifyStatus: Sendable, Hashable {
    enum State: String, Sendable { case playing, paused, stopped }

    let state: State
    /// `spotify:track:<id>` (or `spotify:ad:…` while an ad plays); "" when there is no track.
    let trackURI: String
    /// Seconds into the current track.
    let position: Double

    var isPlaying: Bool { state == .playing }
    var isAd: Bool { trackURI.hasPrefix("spotify:ad:") }

    /// Parses the three-line output of `SpotifyScripts.status`. Real-to-text coercion follows the
    /// user's number format, so "12,5" (Dutch) is accepted as well as "12.5".
    init?(parsing output: String) {
        let lines = output.split(separator: "\n", omittingEmptySubsequences: false)
            .map { $0.trimmingCharacters(in: .whitespacesAndNewlines) }
        guard lines.count == 3, let state = State(rawValue: lines[0]) else { return nil }
        let number = lines[2].replacingOccurrences(of: ",", with: ".")
        self.state = state
        self.trackURI = lines[1]
        self.position = Double(number) ?? 0
    }

    init(state: State, trackURI: String, position: Double) {
        self.state = state
        self.trackURI = trackURI
        self.position = position
    }
}

/// How an AppleScript failure should be handled.
enum SpotifyFailureKind: Sendable, Hashable {
    /// The user denied (or has not granted) Automation access to Spotify.
    case notAuthorized
    /// Spotify quit or is not running; worth one relaunch-and-retry.
    case notRunning
    case other
}

extension AppleScriptFailure {
    var kind: SpotifyFailureKind {
        switch number {
        // errAEEventNotPermitted: the user said "Don't Allow" in the Automation prompt (or in
        // System Settings > Privacy & Security > Automation).
        // errAEEventWouldRequireUserConsent (-1744): consent is needed but could not be asked.
        case -1743, -1744: .notAuthorized
        // procNotFound (-600): application isn't running.
        // connectionInvalid (-609): the app quit between being addressed and answering.
        case -600, -609: .notRunning
        default: .other
        }
    }

    /// The error the game sees when no (further) retry will happen.
    var playerError: PlayerError {
        switch kind {
        case .notAuthorized: .notAuthorized
        case .notRunning: .failed("Spotify stopped responding (AppleScript error \(number))")
        case .other: .failed("Spotify AppleScript error \(number): \(message)")
        }
    }
}
