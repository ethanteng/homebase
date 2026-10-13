import Foundation
import Testing
@testable import UncloudKit

struct FileCacheTests {
    private let cache = FileCache(root: FileManager.default.temporaryDirectory
        .appendingPathComponent("FileCacheTests-\(UUID().uuidString)", isDirectory: true))
    private let modified = Date(timeIntervalSince1970: 1_791_932_280.123)

    private func entry(_ path: String = "Photos/Beach.HEIC", size: Int64 = 5, modified: Date? = nil) -> LibraryEntry {
        LibraryEntry(name: String(path.split(separator: "/").last ?? ""), path: path, isDirectory: false,
                     size: size, modifiedAt: modified ?? self.modified)
    }

    private func downloaded(_ text: String = "hello") throws -> URL {
        let file = FileManager.default.temporaryDirectory.appendingPathComponent("download-\(UUID().uuidString)")
        try Data(text.utf8).write(to: file)
        return file
    }

    @Test func opensAFileAgainUnderItsOwnName() throws {
        defer { try? cache.removeAll() }
        let kept = try cache.keep(try downloaded(), for: entry())
        #expect(kept.lastPathComponent == "Beach.HEIC")
        let found = try #require(cache.file(for: entry()))
        #expect(found == kept)
        #expect(try String(contentsOf: found, encoding: .utf8) == "hello")
    }

    @Test func forgetsAFileThatHasChangedOnTheHost() throws {
        defer { try? cache.removeAll() }
        try cache.keep(try downloaded(), for: entry())
        #expect(cache.file(for: entry(size: 6)) == nil)
        #expect(cache.file(for: entry(modified: modified.addingTimeInterval(1))) == nil)
        #expect(cache.file(for: entry("Photos/Other.HEIC")) == nil)
    }

    @Test func replacesAnEarlierCopy() throws {
        defer { try? cache.removeAll() }
        try cache.keep(try downloaded("old"), for: entry())
        let changed = entry(size: 3, modified: modified.addingTimeInterval(60))
        let kept = try cache.keep(try downloaded("new"), for: changed)
        #expect(try String(contentsOf: kept, encoding: .utf8) == "new")
        #expect(cache.file(for: entry()) == nil)
    }

    @Test func forgetsEverythingAtOnce() throws {
        try cache.keep(try downloaded(), for: entry())
        try cache.removeAll()
        #expect(cache.file(for: entry()) == nil)
        #expect(!FileManager.default.fileExists(atPath: cache.root.path))
        try cache.removeAll()
    }

    @Test(arguments: [("Beach.HEIC", "Beach.HEIC"), (".hidden", "hidden"), ("a/b.txt", "a_b.txt"), ("", "file"), ("...", "file")])
    func neverWritesAPathOrAHiddenFile(name: String, expected: String) {
        #expect(FileCache.fileName(name) == expected)
    }

    @Test func keepsEachAccountOnEachHostApart() throws {
        let caches = URL(fileURLWithPath: "/caches", isDirectory: true)
        let address = try HostAddress(parsing: "http://127.0.0.1:5210")
        let cache = FileCache(caches: caches, address: address, userID: "0123abcd/../x")
        #expect(cache.root.path == "/caches/Files/127.0.0.1_5210/0123abcdx")
    }
}
