import Foundation

// STUB — Wave 2, agent A replaces this file.
extension SourceRef {
    /// Parses open.spotify.com links (with or without locale prefix like /intl-nl/, query
    /// strings, trailing slashes) and `spotify:<kind>:<id>` URIs. nil when not a supported link.
    public init?(string: String) {
        return nil
    }

    /// The public embed page for this ref.
    public var embedURL: URL {
        URL(string: "https://open.spotify.com/embed/\(kind.rawValue)/\(id)")!
    }
}
