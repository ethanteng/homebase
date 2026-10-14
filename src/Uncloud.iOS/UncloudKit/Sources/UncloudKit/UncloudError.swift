import Foundation
#if canImport(FoundationNetworking)
import FoundationNetworking
#endif

/// Why something the host was asked didn't happen, worded for the person holding the device.
public enum UncloudError: Error, Sendable, Equatable {
    /// The session has gone: signed out, run out, or its password changed elsewhere. A wrong password
    /// at sign-in arrives this way too, since the host refuses it the same way.
    case signedOut(String)
    /// The host said no, in its own words. Its messages are written for people, so they are shown as
    /// they are, the way the web app shows them.
    case refused(status: Int, message: String)
    /// Something answered at the address, but not an Uncloud.
    case notUncloud
    /// Nothing answered.
    case unreachable(host: String, why: Unreachable)

    public enum Unreachable: Sendable, Equatable {
        case offline, timedOut, untrusted, other
    }
}

extension UncloudError: LocalizedError {
    public var errorDescription: String? {
        switch self {
        case .signedOut(let message), .refused(_, let message):
            message
        case .notUncloud:
            "That address doesn’t look like an Uncloud. Check it with whoever looks after yours."
        case .unreachable(_, .offline):
            "This device isn’t online. Connect to Wi-Fi or mobile data and try again."
        case .unreachable(let host, .timedOut):
            "\(host) took too long to answer. Check that the computer Uncloud runs on is on and awake."
        case .unreachable(let host, .untrusted):
            "This device doesn’t trust the certificate at \(host), so it won’t send anything there. "
                + "Check the address."
        case .unreachable(let host, .other):
            "Uncloud can’t be reached at \(host). Check that the computer it runs on is on and awake, "
                + "and that Reach From Anywhere is turned on."
        }
    }
}

extension UncloudError {
    /// What the host's own message was, where it sent one: a problem response's `detail`.
    private struct Problem: Decodable {
        let detail: String?
    }

    /// The host's refusal, from its status and the problem response it sent with it.
    static func from(status: Int, body: Data) -> UncloudError {
        let detail = (try? JSONDecoder().decode(Problem.self, from: body))?.detail
        switch status {
        case 401:
            return .signedOut(detail ?? "Sign in to Uncloud to continue.")
        // What the host answers for an endpoint it doesn't have: a host older than this app.
        case 404 where detail == "This endpoint doesn’t exist.":
            return .refused(status: 404, message:
                "This Uncloud needs updating to do that. Ask whoever looks after it to update the Uncloud app on it.")
        default:
            return .refused(status: status, message: detail ?? "Uncloud couldn’t complete this request (\(status)).")
        }
    }

    /// What went wrong on the way, or a cancellation, which is nobody's error.
    static func transport(_ error: any Error, host: String) -> any Error {
        guard let error = error as? URLError else { return error }
        switch error.code {
        case .cancelled:
            return CancellationError()
        case .notConnectedToInternet, .dataNotAllowed, .internationalRoamingOff:
            return UncloudError.unreachable(host: host, why: .offline)
        case .timedOut:
            return UncloudError.unreachable(host: host, why: .timedOut)
        case .secureConnectionFailed, .serverCertificateUntrusted, .serverCertificateHasBadDate,
             .serverCertificateHasUnknownRoot, .serverCertificateNotYetValid,
             .clientCertificateRejected, .clientCertificateRequired:
            return UncloudError.unreachable(host: host, why: .untrusted)
        default:
            return UncloudError.unreachable(host: host, why: .other)
        }
    }
}
