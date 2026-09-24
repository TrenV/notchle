import Foundation
import Network
import NotchleCore

/// One-shot HTTP listener on 127.0.0.1:<free port> that catches Spotify's sign-in redirect.
/// Answers everything but GET /callback with 404 (browsers also ask for /favicon.ico); the
/// callback gets a small "You can close this tab" page. Request parsing is the pure
/// `SpotifyAccounts.parseCallback(requestLine:expectedState:)`.
public final class LoopbackCallbackListener: SpotifyCallbackListening, @unchecked Sendable {
    private let listener: NWListener
    private let queue = DispatchQueue(label: "com.trenv.notchle.spotify-callback")
    private let expectedState: String
    private let lock = NSLock()
    // Guarded by `lock`.
    private var result: Result<String, any Error>?
    private var waiter: CheckedContinuation<String, any Error>?
    public private(set) var port: Int = 0

    private init(listener: NWListener, expectedState: String) {
        self.listener = listener
        self.expectedState = expectedState
    }

    /// Starts listening and returns once the port is known.
    public static func start(expectedState: String) async throws -> LoopbackCallbackListener {
        let parameters = NWParameters.tcp
        parameters.requiredLocalEndpoint = .hostPort(host: .ipv4(.loopback), port: .any)
        parameters.allowLocalEndpointReuse = true
        let listener = try NWListener(using: parameters)
        let callback = LoopbackCallbackListener(listener: listener, expectedState: expectedState)
        listener.newConnectionHandler = { [weak callback] connection in callback?.handle(connection) }
        let port: Int = try await withCheckedThrowingContinuation { continuation in
            let once = OnceFlag()
            listener.stateUpdateHandler = { state in
                switch state {
                case .ready:
                    if once.claim() { continuation.resume(returning: Int(listener.port?.rawValue ?? 0)) }
                case .failed(let error):
                    if once.claim() { continuation.resume(throwing: PlayerError.failed("Couldn't start the sign-in listener: \(error)")) }
                    callback.finish(.failure(PlayerError.failed("The sign-in listener stopped: \(error)")))
                case .cancelled:
                    if once.claim() { continuation.resume(throwing: CancellationError()) }
                default: break
                }
            }
            listener.start(queue: callback.queue)
        }
        callback.port = port
        return callback
    }

    public func waitForCode() async throws -> String {
        try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { continuation in
                lock.lock()
                if let result {
                    lock.unlock()
                    continuation.resume(with: result)
                } else {
                    waiter = continuation
                    lock.unlock()
                }
            }
        } onCancel: {
            self.finish(.failure(CancellationError()))
        }
    }

    public func close() {
        listener.cancel()
        finish(.failure(CancellationError()))
    }

    // MARK: - Internals

    private func finish(_ outcome: Result<String, any Error>) {
        lock.lock()
        guard result == nil else { lock.unlock(); return }
        result = outcome
        let waiter = self.waiter
        self.waiter = nil
        lock.unlock()
        waiter?.resume(with: outcome)
    }

    private func handle(_ connection: NWConnection) {
        connection.start(queue: queue)
        receive(connection, buffer: Data())
    }

    /// Reads the request head (up to 16 KiB), then answers from its first line.
    private func receive(_ connection: NWConnection, buffer: Data) {
        connection.receive(minimumIncompleteLength: 1, maximumLength: 16 * 1024) { [self] data, _, isComplete, error in
            var head = buffer
            if let data { head.append(data) }
            let text = String(decoding: head, as: UTF8.self)
            if error == nil, !isComplete, !text.contains("\r\n\r\n"), head.count < 16 * 1024 {
                receive(connection, buffer: head)
                return
            }
            let requestLine = text.components(separatedBy: "\r\n").first ?? ""
            do {
                switch try SpotifyAccounts.parseCallback(requestLine: requestLine, expectedState: expectedState) {
                case .notCallback:
                    respond(connection, status: "404 Not Found", html: Self.page("Not found"))
                case .code(let code):
                    respond(connection, status: "200 OK", html: Self.page(Self.successText))
                    finish(.success(code))
                }
            } catch {
                respond(connection, status: "400 Bad Request", html: Self.page(Self.failureText))
                finish(.failure(error))
            }
        }
    }

    static let successText = "Notchle is connected to Spotify. You can close this tab."
    static let failureText = "Spotify sign-in didn't complete. Go back to Notchle and try Connect Spotify again."

    static func page(_ text: String) -> String {
        "<!doctype html><meta charset=utf-8><title>Notchle</title>"
            + "<body style=\"font-family:-apple-system,sans-serif;padding:2em\"><p>\(text)</p>"
    }

    private func respond(_ connection: NWConnection, status: String, html: String) {
        let body = Data(html.utf8)
        var response = Data(("HTTP/1.1 \(status)\r\nContent-Type: text/html; charset=utf-8\r\n"
            + "Content-Length: \(body.count)\r\nConnection: close\r\n\r\n").utf8)
        response.append(body)
        connection.send(content: response, completion: .contentProcessed { _ in connection.cancel() })
    }
}

/// Resume-once guard for the listener's state callback.
private final class OnceFlag: @unchecked Sendable {
    private let lock = NSLock()
    private var claimed = false
    func claim() -> Bool {
        lock.lock(); defer { lock.unlock() }
        if claimed { return false }
        claimed = true
        return true
    }
}
