import Foundation
import Testing

/// Saved Spotify pages must not carry the anonymous web-player token (scripts/redact-fixtures.sh).
@Test func fixturesCarryNoSpotifyAccessToken() throws {
    let dir = try #require(Bundle.module.url(forResource: "Fixtures", withExtension: nil))
    let files = try FileManager.default.contentsOfDirectory(at: dir, includingPropertiesForKeys: nil)
        .filter { $0.pathExtension == "html" }
    #expect(files.count >= 4)
    for file in files {
        let text = try String(contentsOf: file, encoding: .utf8)
        #expect(!text.contains("\"accessToken\":\"BQ"), "unredacted token in \(file.lastPathComponent)")
    }
}
