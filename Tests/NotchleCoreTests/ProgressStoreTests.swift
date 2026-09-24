import Foundation
import Testing
@testable import NotchleCore

private func tempDirectory() -> URL {
    FileManager.default.temporaryDirectory
        .appendingPathComponent("notchle-progress-\(UUID().uuidString)", isDirectory: true)
}

@Suite struct ProgressStoreTests {
    @Test func roundTripCreatesMissingDirectories() throws {
        let root = tempDirectory()
        defer { try? FileManager.default.removeItem(at: root) }
        let store = ProgressStore(directory: root.appendingPathComponent("a/b/Notchle", isDirectory: true))
        let progress = Progress(
            settings: AppSettings(playerMode: .preview, lastSource: SourceRef(kind: .album, id: "abc"),
                                  config: GameConfig(tiers: [3, 6], setSize: 10, snippetStart: 12)),
            clearedTrackIDs: ["t2", "t1", "t3"])

        try store.save(progress)
        #expect(store.load() == progress)
        #expect(ProgressStore(directory: store.fileURL.deletingLastPathComponent()).load() == progress)
    }

    @Test func fileIsPrettyJSONWithSortedIDs() throws {
        let root = tempDirectory()
        defer { try? FileManager.default.removeItem(at: root) }
        let store = ProgressStore(directory: root)
        try store.save(Progress(clearedTrackIDs: ["b", "c", "a"]))
        let text = try String(contentsOf: store.fileURL, encoding: .utf8)
        #expect(text.contains("\n  "))
        let json = try #require(JSONSerialization.jsonObject(with: Data(text.utf8)) as? [String: Any])
        #expect(json["clearedTrackIDs"] as? [String] == ["a", "b", "c"])
    }

    @Test func saveOverwritesPreviousFile() throws {
        let root = tempDirectory()
        defer { try? FileManager.default.removeItem(at: root) }
        let store = ProgressStore(directory: root)
        try store.save(Progress(clearedTrackIDs: ["old"]))
        try store.save(Progress(clearedTrackIDs: ["new"]))
        #expect(store.load().clearedTrackIDs == ["new"])
    }

    @Test func missingFileLoadsDefault() {
        let store = ProgressStore(directory: tempDirectory())
        #expect(!FileManager.default.fileExists(atPath: store.fileURL.path))
        #expect(store.load() == Progress())
    }

    @Test(arguments: ["", "not json", "{\"clearedTrackIDs\": 5}", "[]"])
    func corruptFileLoadsDefault(_ contents: String) throws {
        let root = tempDirectory()
        defer { try? FileManager.default.removeItem(at: root) }
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        let store = ProgressStore(directory: root)
        try Data(contents.utf8).write(to: store.fileURL)
        #expect(store.load() == Progress())
    }
}
