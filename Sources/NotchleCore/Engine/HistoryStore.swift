import Foundation

/// JSON file persistence of the play history: `history.json` next to `progress.json`.
/// Foundation-only. Reset progress does not touch it; `clear()` does.
public struct HistoryStore: Sendable {
    public let fileURL: URL
    /// Oldest entries are dropped beyond this many.
    public let cap: Int
    public static let defaultCap = 10_000

    public init(directory: URL, cap: Int = HistoryStore.defaultCap) {
        self.fileURL = directory.appendingPathComponent("history.json")
        self.cap = max(1, cap)
    }

    /// Entries in the order they were appended. Missing or corrupt file: empty, never a throw.
    public func load() -> [HistoryEntry] {
        guard let data = try? Data(contentsOf: fileURL),
              let entries = try? Self.decoder.decode([HistoryEntry].self, from: data)
        else { return [] }
        return entries
    }

    /// Appends `entry` to what is on disk, caps, saves, and returns the new history.
    @discardableResult
    public func append(_ entry: HistoryEntry) throws -> [HistoryEntry] {
        let entries = Self.capped(load() + [entry], cap)
        try save(entries)
        return entries
    }

    /// Writes `entries` (capped) atomically, creating the directory if needed.
    public func save(_ entries: [HistoryEntry]) throws {
        try FileManager.default.createDirectory(
            at: fileURL.deletingLastPathComponent(), withIntermediateDirectories: true)
        let data = try Self.encoder.encode(Self.capped(entries, cap))
        try data.write(to: fileURL, options: .atomic)
    }

    public func clear() throws { try save([]) }

    /// Sets the cover of the entry with `id` (if it is still there), saves, returns the history.
    @discardableResult
    public func setArtworkURL(_ url: URL?, for id: UUID) throws -> [HistoryEntry] {
        var entries = load()
        guard let i = entries.firstIndex(where: { $0.id == id }) else { return entries }
        entries[i] = entries[i].withArtworkURL(url)
        try save(entries)
        return entries
    }

    /// Keeps the newest `cap` entries (the last ones appended).
    public static func capped(_ entries: [HistoryEntry], _ cap: Int) -> [HistoryEntry] {
        entries.count > cap ? Array(entries.suffix(cap)) : entries
    }

    private static var encoder: JSONEncoder {
        let e = JSONEncoder()
        e.outputFormatting = [.prettyPrinted, .sortedKeys]
        e.dateEncodingStrategy = .iso8601
        return e
    }

    private static var decoder: JSONDecoder {
        let d = JSONDecoder()
        d.dateDecodingStrategy = .iso8601
        return d
    }
}
