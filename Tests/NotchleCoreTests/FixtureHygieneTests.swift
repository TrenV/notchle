import Foundation
import Testing

/// Saved Spotify pages must be shrunk to the data the parsers read (scripts/redact-fixtures.sh):
/// no web-player token, no Spotify page config, no page code.
@Test func fixturesCarryNoSpotifyAccessToken() throws {
    let dir = try #require(Bundle.module.url(forResource: "Fixtures", withExtension: nil))
    let files = try FileManager.default.contentsOfDirectory(at: dir, includingPropertiesForKeys: nil)
        .filter { $0.pathExtension == "html" }
    #expect(files.count >= 4)
    for file in files {
        let text = try String(contentsOf: file, encoding: .utf8)
        let name = file.lastPathComponent
        #expect(!text.contains("accessToken"), "token in \(name)")
        #expect(!text.contains("clientId"), "web-player config in \(name)")
        #expect(!text.contains("<link") && !text.contains("<style"), "page code in \(name)")
    }
}
