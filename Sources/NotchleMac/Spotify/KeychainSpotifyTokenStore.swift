import Foundation
import Security
import NotchleCore

/// Spotify tokens in the login Keychain: one generic password (service
/// "com.trenv.notchle.spotify", account "tokens") holding the JSON-encoded `SpotifyTokens`.
public struct KeychainSpotifyTokenStore: SpotifyTokenStore {
    public static let service = "com.trenv.notchle.spotify"
    let account: String

    public init(account: String = "tokens") { self.account = account }

    private var query: [String: Any] {
        [kSecClass as String: kSecClassGenericPassword,
         kSecAttrService as String: Self.service,
         kSecAttrAccount as String: account]
    }

    public func load() -> SpotifyTokens? {
        var q = query
        q[kSecReturnData as String] = true
        q[kSecMatchLimit as String] = kSecMatchLimitOne
        var item: CFTypeRef?
        guard SecItemCopyMatching(q as CFDictionary, &item) == errSecSuccess, let data = item as? Data else { return nil }
        return try? JSONDecoder().decode(SpotifyTokens.self, from: data)
    }

    public func save(_ tokens: SpotifyTokens?) {
        guard let tokens, let data = try? JSONEncoder().encode(tokens) else {
            SecItemDelete(query as CFDictionary)
            return
        }
        let update: [String: Any] = [kSecValueData as String: data]
        let status = SecItemUpdate(query as CFDictionary, update as CFDictionary)
        if status == errSecItemNotFound {
            var add = query
            add[kSecValueData as String] = data
            add[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlock
            let added = SecItemAdd(add as CFDictionary, nil)
            if added != errSecSuccess { NSLog("Notchle: saving Spotify tokens to the Keychain failed (\(added))") }
        } else if status != errSecSuccess {
            NSLog("Notchle: updating Spotify tokens in the Keychain failed (\(status))")
        }
    }
}
