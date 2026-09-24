import AppKit
import Foundation

/// Album-cover bytes, cached on disk at ~/Library/Caches/Notchle/artwork/<trackID>.jpg and in
/// memory. Offline or failed downloads give nil (the views then keep their layout, no image).
@MainActor
public final class ArtworkImageCache {
    public static let shared = ArtworkImageCache()

    /// Snapshots and tests: return an image without touching disk or network.
    public var override: ((String) -> NSImage?)?

    private let directory: URL
    private var memory: [String: NSImage] = [:]
    private var inFlight: [String: Task<NSImage?, Never>] = [:]

    public init(directory: URL? = nil) {
        let caches = FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask).first
            ?? FileManager.default.temporaryDirectory
        self.directory = directory ?? caches.appendingPathComponent("Notchle/artwork", isDirectory: true)
    }

    public func fileURL(for trackID: String) -> URL? {
        guard !trackID.isEmpty, trackID.allSatisfy({ $0.isASCII && ($0.isLetter || $0.isNumber) }) else { return nil }
        return directory.appendingPathComponent("\(trackID).jpg")
    }

    /// Memory or disk only, synchronous (so a cached cover is there on the first frame).
    public func cachedImage(for trackID: String) -> NSImage? {
        if let override { return override(trackID) }
        if let hit = memory[trackID] { return hit }
        guard let file = fileURL(for: trackID), let image = NSImage(contentsOf: file) else { return nil }
        memory[trackID] = image
        return image
    }

    /// Cached image, or downloads `url`, stores it and returns it. nil on any failure.
    public func image(for trackID: String, url: URL) async -> NSImage? {
        if let cached = cachedImage(for: trackID) { return cached }
        if override != nil { return nil }
        if let running = inFlight[trackID] { return await running.value }
        guard let file = fileURL(for: trackID) else { return nil }
        let directory = self.directory
        let task = Task<NSImage?, Never> {
            guard let (data, response) = try? await URLSession.shared.data(from: url),
                  ((response as? HTTPURLResponse)?.statusCode ?? 200) < 300,
                  let image = NSImage(data: data)
            else { return nil }
            try? FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
            try? data.write(to: file, options: .atomic)
            return image
        }
        inFlight[trackID] = task
        let image = await task.value
        inFlight[trackID] = nil
        if let image { memory[trackID] = image }
        return image
    }
}
