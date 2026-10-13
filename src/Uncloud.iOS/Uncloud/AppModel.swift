import Foundation
import Observation
import UncloudKit

/// Where the app is: which host, who is signed in there, and the file on its way to being opened.
/// Every screen reads from here, and everything that talks to the host goes through here.
@MainActor
@Observable
final class AppModel {
    enum Phase: Equatable {
        case connect
        case signIn(HostAddress)
        case files(Account)
    }

    struct Account: Equatable {
        let address: HostAddress
        let user: User
    }

    /// A file on its way down, opened as soon as it arrives.
    struct Opening: Equatable {
        let entry: LibraryEntry
        var received: Int64 = 0
        var expected: Int64?
    }

    private(set) var phase: Phase
    /// Said on the sign-in screen when the host ended the session, rather than the person here.
    private(set) var signInNotice: String?
    private(set) var opening: Opening?
    /// The downloaded file Quick Look is showing.
    var preview: URL?
    /// Why the last file couldn't be opened.
    var openingFailure: String?

    var showingOpeningFailure: Bool {
        get { openingFailure != nil }
        set { if !newValue { openingFailure = nil } }
    }

    @ObservationIgnored private var client: UncloudClient?
    @ObservationIgnored private var listings: [String: DirectoryListing] = [:]
    @ObservationIgnored private var openTask: Task<Void, Never>?
    private let transfers = Transfers()
    private let tokens: any TokenStore
    private let defaults: UserDefaults

    private enum Key {
        static let address = "host.address"
        static let username = "host.username"
        static let user = "host.user"
    }

    init(tokens: any TokenStore = KeychainTokenStore(), defaults: UserDefaults = .standard) {
        self.tokens = tokens
        self.defaults = defaults
        let address = defaults.string(forKey: Key.address).flatMap { try? HostAddress(parsing: $0) }
        let user = defaults.data(forKey: Key.user).flatMap { try? JSONDecoder().decode(User.self, from: $0) }
        // Straight to the files when there's a session to use. If it has run out since, the first
        // folder asked for says so, and that brings sign-in back.
        if let address, let user, let token = tokens.token(for: address) {
            client = UncloudClient(address: address, token: token)
            phase = .files(Account(address: address, user: user))
        } else if let address {
            phase = .signIn(address)
        } else {
            phase = .connect
        }
    }

    /// Who signed in here last, so they needn't type it again.
    var rememberedUsername: String { defaults.string(forKey: Key.username) ?? "" }

    // MARK: Connecting and signing in

    func connect(to text: String) async throws {
        let address = try HostAddress(parsing: text)
        let state = try await UncloudClient(address: address).checkHost()
        guard phase == .connect else { return }
        if state.setupNeeded {
            throw Refusal("Nobody has set up this Uncloud yet. Set it up on the computer it runs on, then come back.")
        }
        if defaults.string(forKey: Key.address) != address.description {
            defaults.removeObject(forKey: Key.username)
        }
        defaults.set(address.description, forKey: Key.address)
        signInNotice = nil
        phase = .signIn(address)
    }

    func signIn(username: String, password: String) async throws {
        guard case .signIn(let address) = phase else { return }
        let (user, token) = try await UncloudClient(address: address).signIn(username: username, password: password)
        guard phase == .signIn(address) else { return }
        try tokens.setToken(token, for: address)
        defaults.set(user.username, forKey: Key.username)
        defaults.set(try? JSONEncoder().encode(user), forKey: Key.user)
        client = UncloudClient(address: address, token: token)
        listings = [:]
        signInNotice = nil
        phase = .files(Account(address: address, user: user))
    }

    func useAnotherHost() {
        defaults.removeObject(forKey: Key.address)
        defaults.removeObject(forKey: Key.username)
        signInNotice = nil
        phase = .connect
    }

    func signOut() async {
        guard case .files(let account) = phase else { return }
        // Ended on the host too where it can be, so the token is no use to anybody afterwards. Not
        // being able to reach the host to say so doesn't keep anybody signed in here.
        try? await client?.signOut()
        end(account, notice: nil)
    }

    /// The host ended this session: it ran out, was signed out elsewhere, or the password changed.
    private func sessionEnded(_ message: String) {
        guard case .files(let account) = phase else { return }
        end(account, notice: message)
    }

    private func end(_ account: Account, notice: String?) {
        guard phase == .files(account) else { return }
        cancelOpening()
        preview = nil
        openingFailure = nil
        try? tokens.setToken(nil, for: account.address)
        try? cache(for: account).removeAll()
        defaults.removeObject(forKey: Key.user)
        client = nil
        listings = [:]
        signInNotice = notice
        phase = .signIn(account.address)
    }

    // MARK: Files

    /// The folder as it was last time it was asked for, to show while it's asked for again.
    func lastListing(of path: String) -> DirectoryListing? { listings[path] }

    func listing(of path: String) async throws -> DirectoryListing {
        guard let client else { throw CancellationError() }
        do {
            let listing = try await client.listing(of: path)
            // Signed out, or in as somebody else, while it was on its way.
            guard self.client?.token == client.token else { throw CancellationError() }
            listings[path] = listing
            return listing
        } catch UncloudError.signedOut(let message) {
            if self.client?.token == client.token { sessionEnded(message) }
            throw CancellationError()
        }
    }

    /// Opens a file in Quick Look, downloading it first unless it's here already and unchanged.
    func open(_ entry: LibraryEntry) {
        guard case .files(let account) = phase, let client else { return }
        cancelOpening()
        openingFailure = nil
        let cache = self.cache(for: account)
        if let file = cache.file(for: entry) {
            preview = file
            return
        }
        opening = Opening(entry: entry, expected: entry.size)
        let transfers = self.transfers
        openTask = Task {
            do {
                let downloaded = try await transfers.download(
                    client.downloadRequest(for: entry.path), expectedBytes: entry.size
                ) { received, expected in
                    Task { @MainActor in self.progress(of: entry, received: received, expected: expected) }
                }
                let file = try cache.keep(downloaded, for: entry)
                guard !Task.isCancelled, opening?.entry == entry else { return }
                opening = nil
                preview = file
            } catch is CancellationError {
            } catch UncloudError.signedOut(let message) {
                sessionEnded(message)
            } catch {
                guard opening?.entry == entry else { return }
                opening = nil
                openingFailure = error.localizedDescription
            }
        }
    }

    func cancelOpening() {
        openTask?.cancel()
        openTask = nil
        opening = nil
    }

    private func progress(of entry: LibraryEntry, received: Int64, expected: Int64?) {
        guard opening?.entry == entry else { return }
        opening?.received = received
        if let expected { opening?.expected = expected }
    }

    private func cache(for account: Account) -> FileCache {
        let caches = FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask)[0]
        return FileCache(caches: caches, address: account.address, userID: account.user.id)
    }
}

/// Something the app itself won't do, in its own words.
struct Refusal: LocalizedError {
    let errorDescription: String?

    init(_ message: String) {
        errorDescription = message
    }
}
