import Foundation
import Testing
@testable import NotchleCore

@Test func fixtureIsBundled() throws {
    let url = try #require(Bundle.module.url(forResource: "embed-playlist-todays-top-hits", withExtension: "html", subdirectory: "Fixtures"))
    #expect(try Data(contentsOf: url).count > 10_000)
}
