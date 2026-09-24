import AppKit
import Foundation
import Testing
@testable import NotchleCore
@testable import NotchleMac

@Suite struct PlaybackSpotifyScriptsTests {
    @Test func playScriptQuotesTheURI() {
        #expect(SpotifyScripts.play(uri: "spotify:track:2FZcjBYK4dTt48q94pJbJD") == """
            with timeout of 10 seconds
            tell application id "com.spotify.client" to play track "spotify:track:2FZcjBYK4dTt48q94pJbJD"
            end timeout
            """)
    }

    @Test func quotingEscapesQuotesAndBackslashes() {
        #expect(SpotifyScripts.quoted(#"a"b\c"#) == #""a\"b\\c""#)
        #expect(SpotifyScripts.quoted("") == #""""#)
    }

    /// The real AppleScript compiler is the judge: a quoted literal must evaluate back to exactly
    /// the original text, including an attempt to break out of the string and run a command.
    @Test(arguments: [
        "spotify:track:2FZcjBYK4dTt48q94pJbJD",
        #"x" & (do shell script "echo pwned") & ""#,
        #"back\slash \" and "quotes""#,
        "multi\nline ünïcødé ✓",
    ])
    func quotedLiteralRoundTripsThroughAppleScript(_ text: String) async throws {
        let output = try await NSAppleScriptRunner().run("return \(SpotifyScripts.quoted(text))")
        #expect(output == text)
    }

    @Test func positionUsesADotAndClampsNegatives() {
        #expect(SpotifyScripts.setPosition(12.5).contains(#"tell application id "com.spotify.client" to set player position to 12.500"# + "\n"))
        #expect(SpotifyScripts.setPosition(-3).contains("to 0.000\n"))
        #expect(SpotifyScripts.number(.nan) == "0.000")
    }

    @Test func pauseAndResume() {
        #expect(SpotifyScripts.pause.contains(#"tell application id "com.spotify.client" to pause"# + "\n"))
        #expect(SpotifyScripts.resume.contains(#"tell application id "com.spotify.client" to play"# + "\n"))
        for script in [SpotifyScripts.pause, SpotifyScripts.resume, SpotifyScripts.status] {
            #expect(script.hasPrefix("with timeout of 10 seconds\n") && script.hasSuffix("\nend timeout"))
        }
    }

    @Test func parsesStatus() throws {
        let playing = try #require(SpotifyStatus(parsing: "playing\nspotify:track:abc\n12.25"))
        #expect(playing == SpotifyStatus(state: .playing, trackURI: "spotify:track:abc", position: 12.25))
        #expect(playing.isPlaying && !playing.isAd)

        let dutch = try #require(SpotifyStatus(parsing: "paused\nspotify:track:abc\n3,5"))
        #expect(dutch.position == 3.5 && dutch.state == .paused)

        let empty = try #require(SpotifyStatus(parsing: "stopped\n\n0"))
        #expect(empty.trackURI == "" && !empty.isPlaying)

        #expect(try #require(SpotifyStatus(parsing: "playing\nspotify:ad:123\n1")).isAd)
        #expect(SpotifyStatus(parsing: "weird") == nil)
        #expect(SpotifyStatus(parsing: "buffering\nx\n1") == nil)
    }

    @Test func mapsAppleScriptErrors() {
        #expect(AppleScriptFailure(number: -1743, message: "").kind == .notAuthorized)
        #expect(AppleScriptFailure(number: -1743, message: "").playerError == .notAuthorized)
        #expect(AppleScriptFailure(number: -1744, message: "").playerError == .notAuthorized)
        #expect(AppleScriptFailure(number: -600, message: "").kind == .notRunning)
        #expect(AppleScriptFailure(number: -609, message: "").kind == .notRunning)
        #expect(AppleScriptFailure(number: -600, message: "").playerError
            == .failed("Spotify stopped responding (AppleScript error -600)"))
        #expect(AppleScriptFailure(number: -1728, message: "Can't get current track.").kind == .other)
        #expect(AppleScriptFailure(number: -1728, message: "Can't get current track.").playerError
            == .failed("Spotify AppleScript error -1728: Can't get current track."))
    }

    static var spotifyInstalled: Bool {
        NSWorkspace.shared.urlForApplication(withBundleIdentifier: "com.spotify.client") != nil
    }

    /// Compiles (never runs) every script against Spotify's scripting dictionary, so a typo in a
    /// Spotify term fails here rather than at play time. Compiling sends no Apple Event.
    @Test(.enabled(if: spotifyInstalled, "needs the Spotify app's scripting dictionary"))
    func scriptsCompileAgainstSpotifysDictionary() async throws {
        let runner = NSAppleScriptRunner()
        for script in [SpotifyScripts.status, SpotifyScripts.play(uri: "spotify:track:x"),
                       SpotifyScripts.setPosition(1), SpotifyScripts.pause, SpotifyScripts.resume] {
            try await runner.compile(script)
        }
        // Control: a term Spotify doesn't define must not compile, or this test proves nothing.
        await #expect(throws: AppleScriptFailure.self) {
            try await runner.compile(#"tell application id "com.spotify.client" to set x to player shmosition of wibble 3"#)
        }
    }
}

@Suite struct PlaybackAppleScriptRunnerTests {
    @Test func returnsTheResultAsText() async throws {
        #expect(try await NSAppleScriptRunner().run("return 1 + 1") == "2")
        #expect(try await NSAppleScriptRunner().run(#"return "a" & linefeed & "b""#) == "a\nb")
    }

    @Test func reportsErrorNumberAndMessage() async {
        await #expect(throws: AppleScriptFailure(number: -1743, message: "denied")) {
            try await NSAppleScriptRunner().run(#"error "denied" number -1743"#)
        }
        await #expect(throws: AppleScriptFailure.self) {
            try await NSAppleScriptRunner().run("this is not applescript (")
        }
    }

    @Test func concurrentCallersAreSerialisedOnOneQueue() async throws {
        let runner = NSAppleScriptRunner()
        let results = try await withThrowingTaskGroup(of: (Int, String).self) { group in
            for i in 0..<40 {
                group.addTask { (i, try await runner.run("return \(i) * 2")) }
            }
            return try await group.reduce(into: [Int: String]()) { $0[$1.0] = $1.1 }
        }
        #expect(results.count == 40)
        #expect(results.allSatisfy { $0.value == String($0.key * 2) })
    }
}
