import Foundation
import Testing
@testable import NotchleCore

/// Hits the real Spotify embed page. Off by default; run with `NOTCHLE_LIVE=1 scripts/test.sh`.
@Test(.enabled(if: ProcessInfo.processInfo.environment["NOTCHLE_LIVE"] == "1", "set NOTCHLE_LIVE=1 to hit open.spotify.com"))
func sourceLiveTodaysTopHits() async throws {
    let ref = try #require(SourceRef(string: "https://open.spotify.com/playlist/37i9dQZF1DXcBWIGoYBM5M"))
    let listing = try await EmbedTrackSource().listing(for: ref)
    print("live: \(listing.name) has \(listing.tracks.count) tracks")
    #expect(listing.tracks.count >= 20)
    #expect(!listing.name.isEmpty)
}
