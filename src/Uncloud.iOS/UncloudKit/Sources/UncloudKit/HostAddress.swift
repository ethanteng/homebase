import Foundation

/// Where an Uncloud is: its scheme, host and port, and nothing else. Read from whatever somebody
/// typed or pasted the way the Mac app reads it (`PairingLink.ParseAddress`), so an address copied
/// out of the browser with a page on the end of it still works.
public struct HostAddress: Hashable, Sendable, CustomStringConvertible {
    public let url: URL

    public init(parsing text: String) throws {
        var text = text.trimmingCharacters(in: .whitespacesAndNewlines)
        if !text.contains("://") {
            // "https:" with the rest missing is no address, rather than a host called "https".
            if text.isEmpty || text.lowercased().hasPrefix("http:") || text.lowercased().hasPrefix("https:") {
                throw HostAddressError.unreadable
            }
            text = "https://" + text
        }
        guard let components = URLComponents(string: text),
              let scheme = components.scheme?.lowercased(), scheme == "https" || scheme == "http",
              var host = components.host?.lowercased(), !host.isEmpty
        else { throw HostAddressError.unreadable }
        // A phone only ever reaches a host through its tunnel, which is https. Plain http is left
        // for a simulator talking to a server run from source on the same Mac.
        if scheme == "http" && !Self.isLoopback(host) { throw HostAddressError.notSecure }
        if host.contains(":") && !host.hasPrefix("[") { host = "[\(host)]" }
        let port = components.port.map { ":\($0)" } ?? ""
        guard let url = URL(string: "\(scheme)://\(host)\(port)") else { throw HostAddressError.unreadable }
        self.url = url
    }

    /// The host as a person would say it: its name, and its port only if it isn't the usual one.
    public var displayHost: String {
        let host = url.host ?? url.absoluteString
        guard let port = url.port else { return host }
        return "\(host):\(port)"
    }

    public var description: String { url.absoluteString }

    private static func isLoopback(_ host: String) -> Bool {
        host == "localhost" || host == "::1" || host == "[::1]" || host.hasPrefix("127.")
    }
}

extension HostAddress: Codable {
    public init(from decoder: any Decoder) throws {
        let text = try decoder.singleValueContainer().decode(String.self)
        try self.init(parsing: text)
    }

    public func encode(to encoder: any Encoder) throws {
        var container = encoder.singleValueContainer()
        try container.encode(url.absoluteString)
    }
}

public enum HostAddressError: Error, Equatable, LocalizedError {
    case unreadable
    case notSecure

    public var errorDescription: String? {
        switch self {
        case .unreadable:
            "Enter the address you open Uncloud at, such as https://uncloud.example.ts.net."
        case .notSecure:
            "This app reaches Uncloud over https://. Use the address Reach From Anywhere gave it, "
                + "such as https://uncloud.example.ts.net."
        }
    }
}
