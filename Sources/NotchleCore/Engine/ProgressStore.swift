import Foundation

// STUB — Wave 2, agent C replaces this file. Public API below is frozen.
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
    public func load() -> Progress { Progress() }

    public func save(_ progress: Progress) throws {}
}
