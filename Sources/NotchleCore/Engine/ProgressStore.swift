import Foundation

public struct Progress: Sendable, Hashable, Codable {
    public var settings: AppSettings
    public var clearedTrackIDs: Set<String>

    public init(settings: AppSettings = AppSettings(), clearedTrackIDs: Set<String> = []) {
        self.settings = settings
        self.clearedTrackIDs = clearedTrackIDs
    }
}

/// JSON file persistence. Foundation-only; `directory` is Application Support/Notchle on macOS.
public struct ProgressStore: Sendable {
    public let fileURL: URL

    public init(directory: URL) {
        self.fileURL = directory.appendingPathComponent("progress.json")
    }

    /// Missing or unreadable file yields a default `Progress`, never a throw.
    public func load() -> Progress {
        guard let data = try? Data(contentsOf: fileURL),
              let progress = try? JSONDecoder().decode(Progress.self, from: data)
        else { return Progress() }
        return progress
    }

    /// Pretty-printed JSON, written atomically (temp file + rename) so a crash mid-write never
    /// leaves a truncated file. Creates the directory if it is missing.
    public func save(_ progress: Progress) throws {
        try FileManager.default.createDirectory(
            at: fileURL.deletingLastPathComponent(), withIntermediateDirectories: true)
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        // Sets encode in hash order; sort the ids so the file is stable and diffable.
        let data = try encoder.encode(StoredProgress(progress))
        try data.write(to: fileURL, options: .atomic)
    }
}

/// On-disk shape: identical keys to `Progress`, with the id set as a sorted array.
private struct StoredProgress: Encodable {
    let settings: AppSettings
    let clearedTrackIDs: [String]

    init(_ progress: Progress) {
        settings = progress.settings
        clearedTrackIDs = progress.clearedTrackIDs.sorted()
    }
}
