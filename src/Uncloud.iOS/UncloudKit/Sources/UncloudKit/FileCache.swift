import Foundation

/// Files this phone has already downloaded, for one account on one host, so opening one again
/// doesn't cross the tunnel again. A file is kept with the size and modification time the listing
/// gave it, and is only used while the listing still says the same.
///
/// Each file sits in a folder of its own named after its path, under its own name, because Quick Look
/// tells what a file is from its extension. It lives in Caches, so iOS can take it back when it needs
/// the room.
public struct FileCache: Sendable {
    public let root: URL

    public init(root: URL) {
        self.root = root
    }

    /// Where one account's downloads go, inside `caches` (the app's `Library/Caches`).
    public init(caches: URL, address: HostAddress, userID: String) {
        let host = address.displayHost.map { $0.isLetter || $0.isNumber || $0 == "." || $0 == "-" ? $0 : "_" }
        let user = userID.filter { $0.isLetter || $0.isNumber }
        self.init(root: caches
            .appendingPathComponent("Files", isDirectory: true)
            .appendingPathComponent(String(host), isDirectory: true)
            .appendingPathComponent(user.isEmpty ? "_" : user, isDirectory: true))
    }

    /// The file already downloaded for this entry, if it hasn't changed on the host since.
    public func file(for entry: LibraryEntry) -> URL? {
        let folder = folder(for: entry)
        guard let data = try? Data(contentsOf: folder.appendingPathComponent(Self.stampName)),
              let stamp = try? JSONDecoder().decode(Stamp.self, from: data),
              stamp == Stamp(entry)
        else { return nil }
        let file = folder.appendingPathComponent(Self.fileName(entry.name))
        return FileManager.default.fileExists(atPath: file.path) ? file : nil
    }

    /// Keeps a downloaded file for this entry, moving it from where the download left it, in place of
    /// any earlier copy. Returns where it is now.
    @discardableResult
    public func keep(_ downloaded: URL, for entry: LibraryEntry) throws -> URL {
        let folder = folder(for: entry)
        let manager = FileManager.default
        if manager.fileExists(atPath: folder.path) { try manager.removeItem(at: folder) }
        try manager.createDirectory(at: folder, withIntermediateDirectories: true)
        let file = folder.appendingPathComponent(Self.fileName(entry.name))
        try manager.moveItem(at: downloaded, to: file)
        #if os(iOS)
        // Unreadable while the phone is locked, but still finishable by a download that is under way.
        try? manager.setAttributes([.protectionKey: FileProtectionType.completeUnlessOpen], ofItemAtPath: file.path)
        #endif
        try JSONEncoder().encode(Stamp(entry)).write(to: folder.appendingPathComponent(Self.stampName))
        return file
    }

    /// Everything downloaded for this account, as on signing out.
    public func removeAll() throws {
        guard FileManager.default.fileExists(atPath: root.path) else { return }
        try FileManager.default.removeItem(at: root)
    }

    // MARK: Layout

    private static let stampName = ".entry.json"

    private struct Stamp: Codable, Equatable {
        let path: String
        let size: Int64?
        let modified: Double

        init(_ entry: LibraryEntry) {
            path = entry.path
            size = entry.size
            modified = entry.modifiedAt.timeIntervalSince1970
        }
    }

    /// A folder per path, named by a hash of it rather than the path itself: the path is the host's to
    /// choose, and nothing it sends should decide where on this phone a file is written. The stamp
    /// inside says which path it really is, so two paths that hash alike can't be mistaken.
    private func folder(for entry: LibraryEntry) -> URL {
        var hash: UInt64 = 0xcbf2_9ce4_8422_2325
        for byte in entry.path.utf8 {
            hash ^= UInt64(byte)
            hash = hash &* 0x0000_0100_0000_01b3
        }
        let name = String(hash, radix: 16)
        return root.appendingPathComponent(String(repeating: "0", count: 16 - name.count) + name, isDirectory: true)
    }

    /// The file's own name, as long as it is one: never a path, and never hidden.
    static func fileName(_ name: String) -> String {
        var safe = name.replacingOccurrences(of: "/", with: "_").replacingOccurrences(of: "\0", with: "")
        while safe.hasPrefix(".") { safe.removeFirst() }
        return safe.isEmpty ? "file" : safe
    }
}
