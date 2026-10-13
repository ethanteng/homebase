import Foundation
import Synchronization
#if canImport(FoundationNetworking)
import FoundationNetworking
#endif
@testable import UncloudKit

/// A pretend host. Each test gets a host name of its own, so tests running side by side never
/// answer each other's requests.
final class StubHost: Sendable {
    struct Reply: Sendable {
        var status = 200
        var headers: [String: String] = [:]
        var body = Data()
        /// Never answer, so the request only ends by being cancelled.
        var hang = false

        static func json(_ text: String, status: Int = 200, headers: [String: String] = [:]) -> Reply {
            Reply(status: status, headers: headers.merging(["Content-Type": "application/json"]) { $1 }, body: Data(text.utf8))
        }
    }

    let address: HostAddress
    private let handler: @Sendable (URLRequest) -> Reply
    private let seen = Mutex<[URLRequest]>([])

    init(_ handler: @escaping @Sendable (URLRequest) -> Reply) {
        address = try! HostAddress(parsing: "https://t\(UUID().uuidString.prefix(8).lowercased()).test")
        self.handler = handler
        StubProtocol.hosts.withLock { $0[address.url.host!] = self }
    }

    /// Every request this host has been sent, in order.
    var requests: [URLRequest] { seen.withLock { $0 } }

    func client(token: String? = nil) -> UncloudClient {
        UncloudClient(address: address, token: token, session: URLSession(configuration: Self.configuration))
    }

    func transfers() -> Transfers { Transfers(configuration: Self.configuration) }

    static var configuration: URLSessionConfiguration {
        let configuration = URLSessionConfiguration.uncloud
        configuration.protocolClasses = [StubProtocol.self]
        return configuration
    }

    fileprivate func answer(_ request: URLRequest) -> Reply {
        seen.withLock { $0.append(request) }
        return handler(request)
    }
}

final class StubProtocol: URLProtocol {
    static let hosts = Mutex<[String: StubHost]>([:])

    override class func canInit(with request: URLRequest) -> Bool {
        guard let host = request.url?.host else { return false }
        return hosts.withLock { $0[host] != nil }
    }

    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }

    override func startLoading() {
        guard let url = request.url, let host = Self.hosts.withLock({ $0[url.host ?? ""] }) else { return }
        var request = request
        // A body handed to URLSession reaches a protocol as a stream.
        if request.httpBody == nil, let stream = request.httpBodyStream {
            request.httpBody = Self.read(stream)
        }
        let reply = host.answer(request)
        if reply.hang { return }
        let response = HTTPURLResponse(url: url, statusCode: reply.status, httpVersion: "HTTP/1.1", headerFields: reply.headers)!
        client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
        client?.urlProtocol(self, didLoad: reply.body)
        client?.urlProtocolDidFinishLoading(self)
    }

    override func stopLoading() {}

    private static func read(_ stream: InputStream) -> Data {
        stream.open()
        defer { stream.close() }
        var data = Data()
        var buffer = [UInt8](repeating: 0, count: 4096)
        while stream.hasBytesAvailable {
            let count = stream.read(&buffer, maxLength: buffer.count)
            guard count > 0 else { break }
            data.append(buffer, count: count)
        }
        return data
    }
}
