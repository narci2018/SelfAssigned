import Foundation
import AltSign

/// Manages certificates stored in the system keychain
public class CertificateManager {
    private let keychainService = "com.altstore.certificates"

    // MARK: - Certificate Storage

    /// Store a certificate in the keychain
    public func storeCertificate(_ certificate: Certificate) throws {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: keychainService,
            kSecAttrAccount as String: certificate.serialNumber,
            kSecValueData as String: certificate.certificateData,
            kSecAttrAccessible as String: kSecAttrAccessibleWhenUnlocked
        ]

        // Delete existing if present
        SecItemDelete(query as CFDictionary)

        let status = SecItemAdd(query as CFDictionary, nil)
        guard status == errSecSuccess else {
            throw CertificateError.storageFailed(status)
        }

        // Store private key separately
        try storePrivateKey(certificate.privateKeyData, forSerial: certificate.serialNumber)
    }

    /// Retrieve a certificate from the keychain
    public func getCertificate(serialNumber: String) throws -> Data? {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: keychainService,
            kSecAttrAccount as String: serialNumber,
            kSecReturnData as String: true,
            kSecMatchLimit as String: kSecMatchLimitOne
        ]

        var result: AnyObject?
        let status = SecItemCopyMatching(query as CFDictionary, &result)

        guard status == errSecSuccess, let data = result as? Data else {
            return nil
        }

        return data
    }

    /// List all stored certificates
    public func listCertificates() -> [Certificate] {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: keychainService,
            kSecReturnData as String: true,
            kSecReturnAttributes as String: true,
            kSecMatchLimit as String: kSecMatchLimitAll
        ]

        var result: AnyObject?
        let status = SecItemCopyMatching(query as CFDictionary, &result)

        guard status == errSecSuccess,
              let items = result as? [[String: Any]] else {
            return []
        }

        return items.compactMap { item in
            guard let data = item[kSecValueData as String] as? Data,
                  let account = item[kSecAttrAccount as String] as? String else {
                return nil
            }

            return Certificate(
                id: UUID(),
                commonName: account,
                teamName: "",
                teamIdentifier: "",
                serialNumber: account,
                notBefore: Date(),
                notAfter: Date().addingTimeInterval(86400 * 365),
                certificateData: data,
                privateKeyData: Data(),
                machineId: nil
            )
        }
    }

    /// Delete a certificate from the keychain
    public func deleteCertificate(serialNumber: String) throws {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: keychainService,
            kSecAttrAccount as String: serialNumber
        ]

        let status = SecItemDelete(query as CFDictionary)
        guard status == errSecSuccess || status == errSecItemNotFound else {
            throw CertificateError.deletionFailed(status)
        }
    }

    // MARK: - Local Certificates (from Keychain Access)

    /// Get certificates from the system keychain (macOS only)
    public func getLocalCertificates() throws -> [Certificate] {
        #if os(macOS)
        let query: [String: Any] = [
            kSecClass as String: kSecClassCertificate,
            kSecReturnRef as String: true,
            kSecMatchLimit as String: kSecMatchLimitAll
        ]

        var result: AnyObject?
        let status = SecItemCopyMatching(query as CFDictionary, &result)

        guard status == errSecSuccess, let items = result as? [SecCertificate] else {
            return []
        }

        var certificates: [Certificate] = []
        for item in items {
            if let cert = parseSecCertificate(item) {
                certificates.append(cert)
            }
        }
        return certificates
        #else
        return []
        #endif
    }

    // MARK: - Private Helpers

    private func storePrivateKey(_ keyData: Data, forSerial serial: String) throws {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: "\(keychainService).privatekeys",
            kSecAttrAccount as String: serial,
            kSecValueData as String: keyData,
            kSecAttrAccessible as String: kSecAttrAccessibleWhenUnlocked
        ]

        SecItemDelete(query as CFDictionary)
        let status = SecItemAdd(query as CFDictionary, nil)
        guard status == errSecSuccess else {
            throw CertificateError.storageFailed(status)
        }
    }

    #if os(macOS)
    private func parseSecCertificate(_ secCert: SecCertificate) -> Certificate? {
        guard let commonName = SecCertificateCopySubjectSummary(secCert) as String? else {
            return nil
        }

        guard let certData = SecCertificateCopyData(secCert) as Data? else {
            return nil
        }

        return Certificate(
            id: UUID(),
            commonName: commonName,
            teamName: "",
            teamIdentifier: "",
            serialNumber: certData.sha256Hash.hexString,
            notBefore: Date(),
            notAfter: Date().addingTimeInterval(86400 * 365),
            certificateData: certData,
            privateKeyData: Data(),
            machineId: nil
        )
    }
    #endif
}

// MARK: - Data Extension

extension Data {
    var sha256Hash: Data {
        // Placeholder - use CryptoKit in real implementation
        return self
    }

    var hexString: String {
        return map { String(format: "%02x", $0) }.joined()
    }
}

// MARK: - Certificate Errors

public enum CertificateError: LocalizedError {
    case storageFailed(OSStatus)
    case deletionFailed(OSStatus)
    case notFound
    case invalidCertificate

    public var errorDescription: String? {
        switch self {
        case .storageFailed(let status):
            return "Failed to store certificate (OSStatus: \(status))"
        case .deletionFailed(let status):
            return "Failed to delete certificate (OSStatus: \(status))"
        case .notFound:
            return "Certificate not found"
        case .invalidCertificate:
            return "Invalid certificate format"
        }
    }
}
