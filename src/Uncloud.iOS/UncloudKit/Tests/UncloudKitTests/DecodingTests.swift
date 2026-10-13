import Foundation
import Testing
@testable import UncloudKit

struct DecodingTests {
    // 2026-10-13T22:58:00Z
    private let moment = 1_791_932_280.0

    @Test func readsSevenDigitsAfterTheSecondsAsDotNetWritesThem() throws {
        let date = try #require(Timestamp.parse("2026-10-13T22:58:00.1234567+00:00"))
        #expect(abs(date.timeIntervalSince1970 - (moment + 0.1234567)) < 0.000_001)
    }

    @Test(arguments: [
        ("2026-10-13T22:58:00+00:00", 0.0),
        ("2026-10-13T22:58:00Z", 0.0),
        ("2026-10-13T22:58:00", 0.0),
        ("2026-10-13T22:58:00.5Z", 0.5),
        ("2026-10-14T00:58:00+02:00", 0.0),
        ("2026-10-13T17:28:00.25-05:30", 0.25),
    ])
    func readsEveryShapeOfTheSameMoment(text: String, fraction: Double) throws {
        let date = try #require(Timestamp.parse(text))
        #expect(abs(date.timeIntervalSince1970 - (moment + fraction)) < 0.000_001)
    }

    @Test func countsLeapDaysAndCenturies() {
        #expect(Timestamp.parse("2024-02-29T12:00:00Z")?.timeIntervalSince1970 == 1_709_208_000)
        #expect(Timestamp.parse("1999-12-31T23:59:59Z")?.timeIntervalSince1970 == 946_684_799)
        #expect(Timestamp.parse("1970-01-01T00:00:00Z")?.timeIntervalSince1970 == 0)
    }

    @Test(arguments: [
        "", "yesterday", "2026-13-01T00:00:00Z", "2026-10-13 22:58:00Z", "2026-10-13T25:00:00Z",
        "2026-10-13T22:58:00.+00:00", "2026-10-13T22:58:00+0000", "2026-10-13T22:58:00Zjunk",
    ])
    func refusesWhatIsNoDate(text: String) {
        #expect(Timestamp.parse(text) == nil)
    }

    @Test func readsAFolderAsTheHostSendsIt() throws {
        // Real field names and shapes from GET /api/files, including ones the app doesn't use.
        let json = """
            {"path":"Photos","entries":[
              {"name":"2026","path":"Photos/2026","isDirectory":true,"size":null,"modifiedAt":"2026-10-13T22:58:00+00:00"},
              {"name":"C++ notes.txt","path":"Photos/C++ notes.txt","isDirectory":false,"size":1536,"modifiedAt":"2026-10-13T22:58:00.1234567+00:00"}
            ],"skippedCount":2,"indexedAt":"2026-10-13T23:00:00.0000001+00:00","somethingNewer":true}
            """
        let listing = try JSONDecoder.uncloud().decode(DirectoryListing.self, from: Data(json.utf8))
        #expect(listing.path == "Photos")
        #expect(listing.skippedCount == 2)
        #expect(listing.entries.map(\.name) == ["2026", "C++ notes.txt"])
        #expect(listing.entries[0].isDirectory && listing.entries[0].size == nil)
        #expect(listing.entries[1].size == 1536)
        #expect(listing.entries[1].fileExtension == "txt")
    }

    @Test func readsASessionWithNobodySignedIn() throws {
        let json = #"{"setupNeeded":false,"hostConfigured":true,"user":null}"#
        let state = try JSONDecoder.uncloud().decode(SessionState.self, from: Data(json.utf8))
        #expect(state == SessionState(setupNeeded: false, hostConfigured: true, user: nil))
    }

    @Test(arguments: [("photo.HEIC", "HEIC"), ("archive.tar.gz", "gz"), ("README", ""), (".hidden", ""), ("trailing.", "")])
    func findsTheExtension(name: String, expected: String) {
        let entry = LibraryEntry(name: name, path: name, isDirectory: false, size: 1, modifiedAt: .now)
        #expect(entry.fileExtension == expected)
    }
}

extension SessionState {
    init(setupNeeded: Bool, hostConfigured: Bool, user: User?) {
        let json = try! JSONEncoder().encode(Shape(setupNeeded: setupNeeded, hostConfigured: hostConfigured, user: user))
        self = try! JSONDecoder.uncloud().decode(SessionState.self, from: json)
    }

    private struct Shape: Encodable {
        let setupNeeded: Bool
        let hostConfigured: Bool
        let user: User?
    }
}
