import Foundation
import Synchronization
#if canImport(FoundationNetworking)
import FoundationNetworking
#endif

/// Files coming from the host, with progress and a way to stop. Uploads will come through here too,
/// which is why it is one thing rather than a function on the client.
public final class Transfers: Sendable {
    private let delegate: TransferDelegate
    private let session: URLSession

    public init(configuration: URLSessionConfiguration = .uncloud) {
        let delegate = TransferDelegate()
        self.delegate = delegate
        session = URLSession(configuration: configuration, delegate: delegate, delegateQueue: nil)
    }

    /// Downloads into a temporary file of its own and returns where, once the whole file is there.
    /// The caller moves it somewhere to keep. Cancelling the task stops the download.
    ///
    /// - Parameters:
    ///   - expectedBytes: How big the listing said it is, for progress when the host doesn't say.
    ///   - progress: Bytes so far and the total if known, a few times a second at most.
    public func download(
        _ request: URLRequest,
        expectedBytes: Int64? = nil,
        progress: @escaping @Sendable (_ received: Int64, _ expected: Int64?) -> Void = { _, _ in }
    ) async throws -> URL {
        let host = request.url?.host ?? "Uncloud"
        let task = session.downloadTask(with: request)
        return try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { continuation in
                delegate.begin(task.taskIdentifier, Pending(
                    host: host, expected: expectedBytes, progress: progress, continuation: continuation))
                if Task.isCancelled { task.cancel() }
                task.resume()
            }
        } onCancel: {
            task.cancel()
        }
    }
}

private struct Pending: Sendable {
    let host: String
    let expected: Int64?
    let progress: @Sendable (Int64, Int64?) -> Void
    let continuation: CheckedContinuation<URL, any Error>
    var file: URL?
    var failure: (any Error)?
    var reportedAt: ContinuousClock.Instant?
}

private final class TransferDelegate: NSObject, URLSessionDownloadDelegate, Sendable {
    private let pending = Mutex<[Int: Pending]>([:])

    func begin(_ id: Int, _ transfer: Pending) {
        pending.withLock { $0[id] = transfer }
    }

    func urlSession(
        _ session: URLSession, downloadTask: URLSessionDownloadTask,
        didWriteData bytesWritten: Int64, totalBytesWritten: Int64, totalBytesExpectedToWrite: Int64
    ) {
        let now = ContinuousClock.now
        let report: (@Sendable (Int64, Int64?) -> Void, Int64?)? = pending.withLock { transfers in
            guard var transfer = transfers[downloadTask.taskIdentifier] else { return nil }
            if let last = transfer.reportedAt, now - last < .milliseconds(100) { return nil }
            transfer.reportedAt = now
            transfers[downloadTask.taskIdentifier] = transfer
            let expected = totalBytesExpectedToWrite > 0 ? totalBytesExpectedToWrite : transfer.expected
            return (transfer.progress, expected)
        }
        if let (progress, expected) = report { progress(totalBytesWritten, expected) }
    }

    func urlSession(_ session: URLSession, downloadTask: URLSessionDownloadTask, didFinishDownloadingTo location: URL) {
        // The file is deleted as soon as this returns, so it is moved somewhere of this app's first.
        // A refusal arrives as a file too, holding the host's problem response.
        let status = (downloadTask.response as? HTTPURLResponse)?.statusCode ?? 0
        var file: URL?
        var failure: (any Error)?
        if (200..<300).contains(status) {
            let destination = FileManager.default.temporaryDirectory
                .appendingPathComponent("download-\(UUID().uuidString)")
            do {
                try FileManager.default.moveItem(at: location, to: destination)
                file = destination
            } catch {
                failure = error
            }
        } else {
            let body = (try? Data(contentsOf: location)) ?? Data()
            failure = UncloudError.from(status: status, body: body.prefix(64 * 1024))
        }
        pending.withLock { transfers in
            transfers[downloadTask.taskIdentifier]?.file = file
            transfers[downloadTask.taskIdentifier]?.failure = failure
        }
    }

    func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: (any Error)?) {
        guard let transfer = pending.withLock({ $0.removeValue(forKey: task.taskIdentifier) }) else { return }
        if let error {
            if let file = transfer.file { try? FileManager.default.removeItem(at: file) }
            transfer.continuation.resume(throwing: UncloudError.transport(error, host: transfer.host))
        } else if let failure = transfer.failure {
            transfer.continuation.resume(throwing: failure)
        } else if let file = transfer.file {
            if let expected = transfer.expected { transfer.progress(expected, expected) }
            transfer.continuation.resume(returning: file)
        } else {
            transfer.continuation.resume(throwing: UncloudError.notUncloud)
        }
    }
}
