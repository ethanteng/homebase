import Foundation
#if canImport(FoundationNetworking)
import FoundationNetworking
#endif

/// One Uncloud host, asked as one account, or as nobody before signing in.
///
/// The host's API was written for its own web pages, and a native app gets through its checks for
/// the right reasons (see docs/ios.md): it sends no `Origin`, puts `X-Homebase-Request: 1` on every
/// request, and hands back the session cookie's value itself. `URLSession` never stores or sends a
/// cookie of its own, so the token exists in exactly one place, wherever the app keeps it.
public struct UncloudClient: Sendable {
    public static let sessionCookie = "uncloud_session"

    public let address: HostAddress
    public let token: String?
    private let session: URLSession

    public init(address: HostAddress, token: String? = nil, session: URLSession = .uncloud) {
        self.address = address
        self.token = token
        self.session = session
    }

    /// The same host, asked as whoever this token signs in.
    public func signedIn(with token: String) -> UncloudClient {
        UncloudClient(address: address, token: token, session: session)
    }

    // MARK: Before signing in

    /// Whether the address is an Uncloud at all, and whether there is anybody on it to sign in as.
    public func checkHost() async throws -> SessionState {
        let (body, response) = try await send(request("GET", "/api/health"))
        // The host refuses a name it doesn't answer to before anything else, with no body at all.
        if response.statusCode == 403 && body.isEmpty {
            throw UncloudError.refused(status: 403, message:
                "Uncloud doesn’t answer to \(address.displayHost). Use the address it shows under Reach From Anywhere.")
        }
        struct Health: Decodable { let status: String }
        guard response.statusCode == 200,
              (try? JSONDecoder().decode(Health.self, from: body))?.status == "ok"
        else { throw UncloudError.notUncloud }
        return try await sessionState()
    }

    /// Signs in, returning who that is and the session's token, which only this call ever sees.
    public func signIn(username: String, password: String) async throws -> (user: User, token: String) {
        struct Credentials: Encodable { let username: String; let password: String }
        struct SignedIn: Decodable { let user: User }
        let body = try JSONEncoder().encode(Credentials(username: username, password: password))
        let request = request("POST", "/api/session", body: body)
        let (data, response) = try await send(request)
        let signedIn: SignedIn = try decode(data, response)
        let headers = response.allHeaderFields.reduce(into: [String: String]()) { headers, field in
            if let name = field.key as? String, let value = field.value as? String { headers[name] = value }
        }
        guard let url = request.url,
              let cookie = HTTPCookie.cookies(withResponseHeaderFields: headers, for: url)
                  .first(where: { $0.name == Self.sessionCookie }),
              !cookie.value.isEmpty
        else {
            throw UncloudError.refused(status: response.statusCode, message:
                "Uncloud signed you in but didn’t hand this app a session. Update Uncloud on its computer and try again.")
        }
        return (signedIn.user, cookie.value)
    }

    // MARK: Signed in

    /// Who this session is, or nobody, and whether the host is set up.
    public func sessionState() async throws -> SessionState {
        try await get("/api/session")
    }

    /// Ends this session on the host, so the token is no use to anybody afterwards.
    public func signOut() async throws {
        let (data, response) = try await send(request("DELETE", "/api/session"))
        guard (200..<300).contains(response.statusCode) else {
            throw UncloudError.from(status: response.statusCode, body: data)
        }
    }

    /// One folder of My files; "" is the top.
    public func listing(of path: String) async throws -> DirectoryListing {
        try await get("/api/files", query: ["path": path])
    }

    /// The request that downloads one file, for `Transfers` to carry out.
    public func downloadRequest(for path: String) -> URLRequest {
        var request = request("GET", "/api/files/download", query: ["path": path])
        // A download can be large, and Funnel is not quick: what counts is that bytes keep arriving.
        request.timeoutInterval = 120
        return request
    }

    // MARK: Requests

    func request(_ method: String, _ path: String, query: KeyValuePairs<String, String> = [:], body: Data? = nil) -> URLRequest {
        var url = address.url.absoluteString + path
        if !query.isEmpty {
            url += "?" + query.map { "\(Self.encode($0.key))=\(Self.encode($0.value))" }.joined(separator: "&")
        }
        var request = URLRequest(url: URL(string: url)!)
        request.httpMethod = method
        request.setValue("1", forHTTPHeaderField: "X-Homebase-Request")
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        if let token { request.setValue("\(Self.sessionCookie)=\(token)", forHTTPHeaderField: "Cookie") }
        if let body {
            request.httpBody = body
            request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        }
        return request
    }

    /// Everything but letters, digits and `-._~/` percent-encoded. `URLComponents` would leave `+`
    /// alone, and ASP.NET reads a `+` in a query string as a space, so `C++ notes.txt` would be
    /// asked for as `C   notes.txt` and not found.
    static func encode(_ text: String) -> String {
        var allowed = CharacterSet(charactersIn: "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789")
        allowed.insert(charactersIn: "-._~/")
        return text.addingPercentEncoding(withAllowedCharacters: allowed) ?? text
    }

    func send(_ request: URLRequest) async throws -> (Data, HTTPURLResponse) {
        let data: Data
        let response: URLResponse
        do {
            (data, response) = try await session.data(for: request)
        } catch {
            throw UncloudError.transport(error, host: address.displayHost)
        }
        guard let http = response as? HTTPURLResponse else { throw UncloudError.notUncloud }
        return (data, http)
    }

    private func get<Value: Decodable>(_ path: String, query: KeyValuePairs<String, String> = [:]) async throws -> Value {
        let (data, response) = try await send(request("GET", path, query: query))
        return try decode(data, response)
    }

    private func decode<Value: Decodable>(_ data: Data, _ response: HTTPURLResponse) throws -> Value {
        guard (200..<300).contains(response.statusCode) else {
            throw UncloudError.from(status: response.statusCode, body: data)
        }
        do {
            return try JSONDecoder.uncloud().decode(Value.self, from: data)
        } catch {
            throw UncloudError.refused(status: response.statusCode, message:
                "Uncloud answered with something this app doesn’t understand. Check for an update to the app.")
        }
    }
}

extension URLSessionConfiguration {
    /// No cookies, no caching: the token is sent by hand, and everything the host sends is private.
    public static var uncloud: URLSessionConfiguration {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.httpCookieStorage = nil
        configuration.httpShouldSetCookies = false
        configuration.httpCookieAcceptPolicy = .never
        configuration.urlCache = nil
        configuration.requestCachePolicy = .reloadIgnoringLocalCacheData
        configuration.timeoutIntervalForRequest = 30
        return configuration
    }
}

extension URLSession {
    public static let uncloud = URLSession(configuration: .uncloud)
}
