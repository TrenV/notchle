import Foundation

/// Runs AppleScript source and returns its result as text. The seam between
/// `SpotifyAppPlayer` and Apple Events: tests inject a fake that records scripts and
/// returns canned results or `AppleScriptFailure`s.
protocol AppleScriptRunning: Sendable {
    /// Throws `AppleScriptFailure` when the script fails to compile or run.
    func run(_ source: String) async throws -> String
}

/// Executes scripts with `NSAppleScript` on one dedicated, process-wide serial GCD queue.
///
/// Why a dedicated queue: `NSAppleScript` is not thread-safe, so every compile and execute must
/// happen on one serial context, never two at once. Why not `@MainActor`: an Apple Event blocks
/// its thread until the target answers (up to `SpotifyScripts.eventTimeout`; while macOS shows the
/// Automation consent prompt, until the user answers), and the 50 ms status poll runs for seconds
/// per snippet; on the main thread that would stall the notch UI and its confetti.
///
/// Why a plain GCD queue and not an actor with a `DispatchSerialQueue` custom executor (the first
/// version): the runtime may drain such an executor on whichever thread switches to it, and a
/// sample of a CLI host showed the script executing on the main thread. `queue.async` plus a
/// continuation keeps the blocking call off the caller's thread, always.
///
/// Host requirement (observed 2026-09-24, Spotify 1.3.1.234): Apple Event replies only arrive
/// in a process whose main thread runs `NSApplication.run()` or `dispatchMain()`. In a process
/// whose entry point is Swift's async `main` (including the `swift test` runner) the event is sent
/// but the reply never comes, on any thread, and `with timeout` doesn't fire. Notchle.app runs
/// NSApplication, so this only affects tests; see `PlaybackLiveSpotifyTests`.
final class NSAppleScriptRunner: AppleScriptRunning {
    /// One queue for the whole process, shared by every runner instance, so two players (or
    /// tests running in parallel) can never drive NSAppleScript on two threads at once.
    private static let queue = DispatchQueue(label: "com.notchle.applescript", qos: .userInitiated)

    init() {}

    func run(_ source: String) async throws -> String {
        try await onQueue { try Self.execute(source) }
    }

    /// Compiles without executing (no Apple Event is sent). Used to check script text against
    /// Spotify's scripting dictionary.
    func compile(_ source: String) async throws {
        try await onQueue { try Self.compileOnly(source) }
    }

    private func onQueue<T: Sendable>(_ work: @escaping @Sendable () throws -> T) async throws -> T {
        try await withCheckedThrowingContinuation { continuation in
            Self.queue.async {
                continuation.resume(with: Result { try work() })
            }
        }
    }

    private static func execute(_ source: String) throws -> String {
        dispatchPrecondition(condition: .onQueue(queue))
        guard let script = NSAppleScript(source: source) else {
            throw AppleScriptFailure(number: -2700, message: "could not create script")
        }
        var errorInfo: NSDictionary?
        let result = script.executeAndReturnError(&errorInfo)
        if let errorInfo { throw failure(from: errorInfo) }
        return result.stringValue ?? ""
    }

    private static func compileOnly(_ source: String) throws {
        dispatchPrecondition(condition: .onQueue(queue))
        guard let script = NSAppleScript(source: source) else {
            throw AppleScriptFailure(number: -2700, message: "could not create script")
        }
        var errorInfo: NSDictionary?
        if !script.compileAndReturnError(&errorInfo) {
            throw failure(from: errorInfo)
        }
    }

    private static func failure(from errorInfo: NSDictionary?) -> AppleScriptFailure {
        let number = (errorInfo?[NSAppleScript.errorNumber] as? NSNumber)?.intValue ?? -2700
        let message = errorInfo?[NSAppleScript.errorMessage] as? String
            ?? errorInfo?[NSAppleScript.errorBriefMessage] as? String
            ?? "unknown AppleScript error"
        return AppleScriptFailure(number: number, message: message)
    }
}
