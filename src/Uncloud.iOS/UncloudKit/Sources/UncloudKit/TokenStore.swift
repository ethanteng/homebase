import Foundation
import Synchronization
#if canImport(Security)
import Security
#endif

/// Where a session's token is kept between launches: one per host.
public protocol TokenStore: Sendable {
    func token(for address: HostAddress) -> String?
    /// Keeps a token for this host, in place of any other; nil forgets it.
    func setToken(_ token: String?, for address: HostAddress) throws
}

/// Tokens kept only while the app runs, for tests and previews.
public final class MemoryTokenStore: TokenStore {
    private let tokens = Mutex<[HostAddress: String]>([:])

    public init() {}

    public func token(for address: HostAddress) -> String? {
        tokens.withLock { $0[address] }
    }

    public func setToken(_ token: String?, for address: HostAddress) throws {
        tokens.withLock { $0[address] = token }
    }
}

#if canImport(Security)
/// Tokens in the Keychain, readable on this device alone and only once it has been unlocked since
/// it started, so a background transfer can still use one while the phone is locked. Never synced to
/// another device, or restored onto one.
public struct KeychainTokenStore: TokenStore {
    public let service: String
    /// Shared with the app's extensions once there are some; nil keeps it to the app alone.
    public let accessGroup: String?

    public init(service: String = "life.uncloud.session", accessGroup: String? = nil) {
        self.service = service
        self.accessGroup = accessGroup
    }

    public func token(for address: HostAddress) -> String? {
        var query = query(for: address)
        query[kSecReturnData as String] = true
        query[kSecMatchLimit as String] = kSecMatchLimitOne
        var result: CFTypeRef?
        guard SecItemCopyMatching(query as CFDictionary, &result) == errSecSuccess,
              let data = result as? Data
        else { return nil }
        return String(data: data, encoding: .utf8)
    }

    public func setToken(_ token: String?, for address: HostAddress) throws {
        let query = query(for: address)
        let deleted = SecItemDelete(query as CFDictionary)
        guard deleted == errSecSuccess || deleted == errSecItemNotFound else { throw KeychainError(status: deleted) }
        guard let token else { return }
        var item = query
        item[kSecValueData as String] = Data(token.utf8)
        item[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly
        let added = SecItemAdd(item as CFDictionary, nil)
        guard added == errSecSuccess else { throw KeychainError(status: added) }
    }

    private func query(for address: HostAddress) -> [String: Any] {
        var query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: address.url.absoluteString,
        ]
        if let accessGroup { query[kSecAttrAccessGroup as String] = accessGroup }
        return query
    }
}

public struct KeychainError: Error, LocalizedError {
    public let status: OSStatus

    public var errorDescription: String? {
        "This device couldn’t keep you signed in (Keychain error \(status)). Try signing in again."
    }
}
#endif
