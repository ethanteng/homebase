import Foundation
import Testing
#if canImport(FoundationNetworking)
import FoundationNetworking
#endif
@testable import UncloudKit

struct ClientTests {
    private static let health = StubHost.Reply.json(#"{"status":"ok"}"#)
    private static let signedOut = #"{"setupNeeded":false,"hostConfigured":true,"user":null}"#
    private static let owner = #"{"id":"0123456789abcdef0123456789abcdef","username":"owner","displayName":"Owner","isAdmin":true,"createdAt":"2026-10-13T22:58:00+00:00","disabledAt":null}"#

    // MARK: What every request carries

    @Test func asksTheWayTheHostsChecksLetThrough() async throws {
        let host = StubHost { _ in .json(#"{"path":"","entries":[],"skippedCount":0,"indexedAt":"2026-10-13T22:58:00+00:00"}"#) }
        _ = try await host.client(token: "the-token").listing(of: "")
        let request = try #require(host.requests.first)
        #expect(request.value(forHTTPHeaderField: "X-Homebase-Request") == "1")
        #expect(request.value(forHTTPHeaderField: "Cookie") == "uncloud_session=the-token")
        #expect(request.value(forHTTPHeaderField: "Origin") == nil)
    }

    @Test func sendsNoCookieBeforeSigningIn() async throws {
        let host = StubHost { _ in .json(Self.signedOut) }
        _ = try await host.client().sessionState()
        #expect(host.requests.first?.value(forHTTPHeaderField: "Cookie") == nil)
    }

    @Test(arguments: [
        ("C++ notes.txt", "C%2B%2B%20notes.txt"),
        ("Photos/2026 & more/a=b#1.jpg", "Photos/2026%20%26%20more/a%3Db%231.jpg"),
        ("Café/naïve.pdf", "Caf%C3%A9/na%C3%AFve.pdf"),
        ("100%.txt", "100%25.txt"),
        ("", ""),
    ])
    func encodesAPathSoTheHostReadsItBackExactly(path: String, expected: String) {
        #expect(UncloudClient.encode(path) == expected)
    }

    @Test func asksForTheFolderItWasGiven() async throws {
        let host = StubHost { _ in .json(#"{"path":"C++ notes","entries":[],"skippedCount":0,"indexedAt":"2026-10-13T22:58:00+00:00"}"#) }
        let listing = try await host.client(token: "t").listing(of: "C++ notes")
        #expect(listing.path == "C++ notes")
        #expect(host.requests.first?.url?.absoluteString == "\(host.address)/api/files?path=C%2B%2B%20notes")
    }

    // MARK: Is it an Uncloud

    @Test func findsAnUncloudReadyToSignInTo() async throws {
        let host = StubHost { request in
            request.url?.path == "/api/health" ? Self.health : .json(Self.signedOut)
        }
        let state = try await host.client().checkHost()
        #expect(!state.setupNeeded)
        #expect(host.requests.map { $0.url?.path } == ["/api/health", "/api/session"])
    }

    @Test func saysWhenSomethingElseAnswers() async {
        let host = StubHost { _ in StubHost.Reply(status: 200, headers: ["Content-Type": "text/html"], body: Data("<html></html>".utf8)) }
        await #expect(throws: UncloudError.notUncloud) { try await host.client().checkHost() }
    }

    @Test func saysWhenTheHostDoesntAnswerToThisName() async throws {
        let host = StubHost { _ in StubHost.Reply(status: 403) }
        let error = try await #require(throws: UncloudError.self) { try await host.client().checkHost() }
        guard case .refused(403, let message) = error else { Issue.record("\(error)"); return }
        #expect(message.contains("Reach From Anywhere"))
    }

    // MARK: Signing in

    @Test func keepsTheSessionTheHostHandsOver() async throws {
        let host = StubHost { _ in
            .json(#"{"user":\#(Self.owner)}"#, headers: [
                "Set-Cookie": "uncloud_session=AbC-123_xyz; expires=Thu, 12 Nov 2026 23:00:00 GMT; path=/; secure; samesite=lax; httponly"
            ])
        }
        let (user, token) = try await host.client().signIn(username: "owner", password: "correct horse")
        #expect(user.username == "owner" && user.isAdmin)
        #expect(token == "AbC-123_xyz")
        let request = try #require(host.requests.first)
        #expect(request.httpMethod == "POST")
        #expect(request.value(forHTTPHeaderField: "Content-Type") == "application/json")
        let sent = try JSONSerialization.jsonObject(with: try #require(request.httpBody)) as? [String: String]
        #expect(sent == ["username": "owner", "password": "correct horse"])
    }

    @Test func passesOnTheHostsOwnWordsForAWrongPassword() async {
        let host = StubHost { _ in
            .json(#"{"title":"Unauthorized","status":401,"detail":"That username and password don’t match."}"#, status: 401)
        }
        await #expect(throws: UncloudError.signedOut("That username and password don’t match.")) {
            try await host.client().signIn(username: "owner", password: "wrong")
        }
    }

    @Test func refusesASignInThatHandsOverNoSession() async {
        let host = StubHost { _ in .json(#"{"user":\#(Self.owner)}"#) }
        await #expect(throws: UncloudError.self) { try await host.client().signIn(username: "owner", password: "pw") }
    }

    @Test func signsOutOnTheHost() async throws {
        let host = StubHost { _ in .json(#"{"signedOut":true}"#) }
        try await host.client(token: "t").signOut()
        #expect(host.requests.first?.httpMethod == "DELETE")
        #expect(host.requests.first?.url?.path == "/api/session")
        #expect(host.requests.first?.value(forHTTPHeaderField: "Cookie") == "uncloud_session=t")
    }

    // MARK: Refusals

    @Test func aSessionThatHasGoneIsSaidSo() async {
        let host = StubHost { _ in .json(#"{"status":401,"detail":"Sign in to Uncloud to continue."}"#, status: 401) }
        await #expect(throws: UncloudError.signedOut("Sign in to Uncloud to continue.")) {
            try await host.client(token: "old").listing(of: "")
        }
    }

    @Test func anOlderHostIsSaidToNeedUpdating() async throws {
        let host = StubHost { _ in .json(#"{"status":404,"detail":"This endpoint doesn’t exist."}"#, status: 404) }
        let error = try await #require(throws: UncloudError.self) { try await host.client(token: "t").listing(of: "") }
        #expect(error.localizedDescription.contains("needs updating"))
    }

    @Test func aMissingFolderIsSaidInTheHostsWords() async {
        let host = StubHost { _ in .json(#"{"status":404,"detail":"This folder is no longer available."}"#, status: 404) }
        await #expect(throws: UncloudError.refused(status: 404, message: "This folder is no longer available.")) {
            try await host.client(token: "t").listing(of: "Gone")
        }
    }

    @Test func aRefusalWithNoWordsStillSaysSomething() async {
        let host = StubHost { _ in StubHost.Reply(status: 500) }
        await #expect(throws: UncloudError.refused(status: 500, message: "Uncloud couldn’t complete this request (500).")) {
            try await host.client(token: "t").listing(of: "")
        }
    }

    @Test func networkTroubleIsWordedForThePhone() {
        func reason(_ code: URLError.Code) -> UncloudError? {
            UncloudError.transport(URLError(code), host: "uncloud.example.ts.net") as? UncloudError
        }
        #expect(reason(.notConnectedToInternet) == .unreachable(host: "uncloud.example.ts.net", why: .offline))
        #expect(reason(.timedOut) == .unreachable(host: "uncloud.example.ts.net", why: .timedOut))
        #expect(reason(.serverCertificateUntrusted) == .unreachable(host: "uncloud.example.ts.net", why: .untrusted))
        #expect(reason(.cannotFindHost) == .unreachable(host: "uncloud.example.ts.net", why: .other))
        #expect(UncloudError.transport(URLError(.cancelled), host: "x") is CancellationError)
    }
}
