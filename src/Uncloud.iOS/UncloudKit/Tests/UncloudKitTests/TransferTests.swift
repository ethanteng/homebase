import Foundation
import Synchronization
import Testing
#if canImport(FoundationNetworking)
import FoundationNetworking
#endif
@testable import UncloudKit

/// Linux's Foundation traps finishing a download task that a custom `URLProtocol` fed, so the tests
/// that finish one run where the app does, on Apple's platforms, and are skipped on Linux.
private let finishedDownloadsCanBeStubbed: Bool = {
    #if canImport(FoundationNetworking)
    false
    #else
    true
    #endif
}()

struct TransferTests {
    @Test(.enabled(if: finishedDownloadsCanBeStubbed))
    func downloadsTheWholeFile() async throws {
        let contents = Data((0..<200_000).map { UInt8($0 % 251) })
        let host = StubHost { _ in
            StubHost.Reply(headers: ["Content-Type": "application/octet-stream", "Content-Length": "\(contents.count)"], body: contents)
        }
        let finished = Mutex<(Int64, Int64?)?>(nil)
        let file = try await host.transfers().download(
            host.client(token: "t").downloadRequest(for: "Photos/big.bin"), expectedBytes: Int64(contents.count)
        ) { received, expected in
            finished.withLock { $0 = (received, expected) }
        }
        defer { try? FileManager.default.removeItem(at: file) }
        #expect(try Data(contentsOf: file) == contents)
        #expect(finished.withLock { $0?.0 } == Int64(contents.count))
        let request = try #require(host.requests.first)
        #expect(request.url?.absoluteString == "\(host.address)/api/files/download?path=Photos/big.bin")
        #expect(request.value(forHTTPHeaderField: "Cookie") == "uncloud_session=t")
    }

    @Test(.enabled(if: finishedDownloadsCanBeStubbed))
    func aRefusedDownloadIsSaidInTheHostsWords() async {
        let host = StubHost { _ in .json(#"{"status":404,"detail":"This file is no longer available."}"#, status: 404) }
        await #expect(throws: UncloudError.refused(status: 404, message: "This file is no longer available.")) {
            try await host.transfers().download(host.client(token: "t").downloadRequest(for: "gone.txt"))
        }
    }

    @Test(.enabled(if: finishedDownloadsCanBeStubbed))
    func aSessionThatHasGoneMidwayIsSaidSo() async {
        let host = StubHost { _ in .json(#"{"detail":"Sign in to Uncloud to continue."}"#, status: 401) }
        await #expect(throws: UncloudError.signedOut("Sign in to Uncloud to continue.")) {
            try await host.transfers().download(host.client(token: "old").downloadRequest(for: "a.txt"))
        }
    }

    @Test func stopsWhenCancelled() async throws {
        let host = StubHost { _ in StubHost.Reply(hang: true) }
        let transfers = host.transfers()
        let request = host.client(token: "t").downloadRequest(for: "slow.mov")
        let download = Task { try await transfers.download(request) }
        // Give the request time to reach the host before giving up on it.
        while host.requests.isEmpty { try await Task.sleep(for: .milliseconds(10)) }
        download.cancel()
        await #expect(throws: CancellationError.self) { try await download.value }
    }
}
