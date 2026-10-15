import Foundation
import Security

/// API keys live in the login Keychain, never in UserDefaults.
///
/// All keys share one Keychain item and are read once per launch: macOS asks for permission per item
/// and per read, and an ad-hoc signed build counts as a new app after every update.
///
/// A read that fails (access denied, Keychain locked, unreadable data) is never cached and never
/// overwritten, so declining the prompt can't wipe the saved keys.
enum Keychain {
    private static let service = "com.lidiflow.app"
    private static let account = "api-keys"
    private static let lock = NSLock()
    private static var cache: [String: String]?

    private enum ReadResult {
        case found(Data), notFound, failed
    }

    static func key(for provider: LLMProvider) -> String? {
        lock.withLock { load()?[provider.rawValue.lowercased()] }
    }

    /// Saves (or with nil, removes) a key. Returns false when the Keychain couldn't be read or written,
    /// in which case nothing changed.
    @discardableResult
    static func setKey(_ value: String?, for provider: LLMProvider) -> Bool {
        lock.withLock {
            guard var keys = load() else { return false }
            keys[provider.rawValue.lowercased()] = value?.isEmpty == false ? value : nil
            guard save(keys) else { return false }
            cache = keys
            return true
        }
    }

    /// The saved keys, or nil when the Keychain couldn't be read (retried on the next call).
    private static func load() -> [String: String]? {
        if let cache { return cache }
        switch read(account: account) {
        case .found(let data):
            guard let keys = try? JSONDecoder().decode([String: String].self, from: data) else { return nil }
            cache = keys
            return keys
        case .failed:
            return nil
        case .notFound:
            break
        }

        // Earlier versions kept one item per provider: move them into the shared item.
        var keys: [String: String] = [:]
        for provider in LLMProvider.allCases where provider.needsKey {
            let legacy = provider.rawValue.lowercased()
            switch read(account: legacy) {
            case .found(let data): keys[legacy] = String(data: data, encoding: .utf8)
            case .notFound: continue
            case .failed: return nil
            }
        }
        if !keys.isEmpty {
            // The old items go only once the shared one is safely written.
            guard save(keys) else { return nil }
            for legacy in keys.keys { delete(account: legacy) }
        }
        cache = keys
        return keys
    }

    private static func save(_ keys: [String: String]) -> Bool {
        guard !keys.isEmpty else { return delete(account: account) }
        guard let data = try? JSONEncoder().encode(keys) else { return false }
        return write(data, account: account)
    }

    private static func baseQuery(_ account: String) -> [String: Any] {
        [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
        ]
    }

    private static func read(account: String) -> ReadResult {
        var query = baseQuery(account)
        query[kSecReturnData as String] = true
        query[kSecMatchLimit as String] = kSecMatchLimitOne
        var result: CFTypeRef?
        switch SecItemCopyMatching(query as CFDictionary, &result) {
        case errSecSuccess: return (result as? Data).map(ReadResult.found) ?? .failed
        case errSecItemNotFound: return .notFound
        default: return .failed
        }
    }

    /// Updates the item in place, so a failed write leaves the old value intact.
    private static func write(_ data: Data, account: String) -> Bool {
        let query = baseQuery(account)
        let status = SecItemUpdate(query as CFDictionary, [kSecValueData as String: data] as CFDictionary)
        guard status == errSecItemNotFound else { return status == errSecSuccess }
        var item = query
        item[kSecValueData as String] = data
        return SecItemAdd(item as CFDictionary, nil) == errSecSuccess
    }

    @discardableResult
    private static func delete(account: String) -> Bool {
        let status = SecItemDelete(baseQuery(account) as CFDictionary)
        return status == errSecSuccess || status == errSecItemNotFound
    }
}
