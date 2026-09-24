import Foundation
#if canImport(FoundationNetworking)
import FoundationNetworking
#endif

// A second HTTP seam next to the frozen `HTTPClient` (which only does GET): the Spotify Web API
// needs PUT/POST with a body, and the status + Retry-After of every answer.

public struct HTTPRequest: Sendable, Hashable {
    public var method: String
    public var url: URL
    public var headers: [String: String]
    public var body: Data?
    /// Seconds; nil means the platform default.
    public var timeout: Double?

    public init(method: String, url: URL, headers: [String: String] = [:], body: Data? = nil, timeout: Double? = nil) {
        self.method = method
        self.url = url
        self.headers = headers
        self.body = body
        self.timeout = timeout
    }

    /// The body as UTF-8 text (tests and debugging).
    public var bodyText: String { body.map { String(decoding: $0, as: UTF8.self) } ?? "" }
}

public struct HTTPResponse: Sendable, Hashable {
    public var status: Int
    /// Header names lowercased.
    public var headers: [String: String]
    public var body: Data

    public init(status: Int, headers: [String: String] = [:], body: Data = Data()) {
        self.status = status
        self.headers = Dictionary(headers.map { ($0.key.lowercased(), $0.value) }, uniquingKeysWith: { a, _ in a })
        self.body = body
    }

    public init(status: Int, headers: [String: String] = [:], text: String) {
        self.init(status: status, headers: headers, body: Data(text.utf8))
    }

    public var isSuccess: Bool { (200..<300).contains(status) }
    public var text: String { String(decoding: body, as: UTF8.self) }
}

public protocol HTTPRequesting: Sendable {
    /// Throws only for transport failures (and cancellation); any HTTP status is a response.
    func send(_ request: HTTPRequest) async throws -> HTTPResponse
}

public struct URLSessionHTTPRequester: HTTPRequesting {
    public init() {}

    public func send(_ request: HTTPRequest) async throws -> HTTPResponse {
        var urlRequest = URLRequest(url: request.url)
        urlRequest.httpMethod = request.method
        urlRequest.httpBody = request.body
        if let timeout = request.timeout { urlRequest.timeoutInterval = timeout }
        for (name, value) in request.headers { urlRequest.setValue(value, forHTTPHeaderField: name) }
        let (data, response) = try await URLSession.shared.data(for: urlRequest)
        let http = response as? HTTPURLResponse
        var headers: [String: String] = [:]
        for (key, value) in http?.allHeaderFields ?? [:] {
            if let key = key as? String, let value = value as? String { headers[key] = value }
        }
        return HTTPResponse(status: http?.statusCode ?? 0, headers: headers, body: data)
    }
}
