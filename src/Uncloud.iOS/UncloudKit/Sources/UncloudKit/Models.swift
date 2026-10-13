import Foundation

// What the host answers with, as far as the app uses it. Anything else a host sends is ignored, so a
// newer host that says more still decodes; anything a later host adds is optional here.

/// Somebody with an account on the host.
public struct User: Codable, Sendable, Hashable {
    public let id: String
    public let username: String
    public let displayName: String
    public let isAdmin: Bool

    public init(id: String, username: String, displayName: String, isAdmin: Bool) {
        self.id = id
        self.username = username
        self.displayName = displayName
        self.isAdmin = isAdmin
    }
}

/// `GET /api/session`: whether the host has been set up at all, and who this session is, if anybody.
public struct SessionState: Decodable, Sendable, Equatable {
    public let setupNeeded: Bool
    public let hostConfigured: Bool
    public let user: User?
}

/// One file or folder in My files.
public struct LibraryEntry: Decodable, Sendable, Hashable, Identifiable {
    public let name: String
    /// Where it is inside the account's own folder, `/`-separated, which is how the host is asked for it.
    public let path: String
    public let isDirectory: Bool
    /// The file's size; nil for a folder, which the host doesn't measure in a listing.
    public let size: Int64?
    public let modifiedAt: Date

    public var id: String { path }

    /// What comes after the last dot in the name, without it, or "" when there is none.
    public var fileExtension: String {
        guard let dot = name.lastIndex(of: "."), dot != name.startIndex else { return "" }
        return String(name[name.index(after: dot)...])
    }

    public init(name: String, path: String, isDirectory: Bool, size: Int64?, modifiedAt: Date) {
        self.name = name
        self.path = path
        self.isDirectory = isDirectory
        self.size = size
        self.modifiedAt = modifiedAt
    }
}

/// `GET /api/files`: one folder, folders first and then by name, as the host sorts it.
public struct DirectoryListing: Decodable, Sendable, Equatable {
    public let path: String
    public let entries: [LibraryEntry]
    /// Things in the folder the host left out: links, and anything it couldn't read.
    public let skippedCount: Int

    public init(path: String, entries: [LibraryEntry], skippedCount: Int) {
        self.path = path
        self.entries = entries
        self.skippedCount = skippedCount
    }
}
