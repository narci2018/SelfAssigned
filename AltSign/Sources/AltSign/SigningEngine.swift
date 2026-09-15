import Foundation
import CryptoKit

/// Core IPA signing engine
public final class SigningEngine: Sendable {
    private let certificate: Certificate
    private let provisioningProfile: ProvisioningProfile

    public init(certificate: Certificate, provisioningProfile: ProvisioningProfile) {
        self.certificate = certificate
        self.provisioningProfile = provisioningProfile
    }

    /// Sign an IPA file with the provided certificate and provisioning profile
    public func signIPA(at ipaURL: URL, outputURL: URL) async throws -> URL {
        let tempDir = FileManager.default.temporaryDirectory
            .appendingPathComponent("altsign_\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: tempDir) }

        try FileManager.default.createDirectory(at: tempDir, withIntermediateDirectories: true)

        // Step 1: Extract IPA
        try IPAExtractor.extract(ipaAt: ipaURL, to: tempDir)

        // Step 2: Find .app bundle
        let appBundle = try IPAExtractor.findAppBundle(in: tempDir)

        // Step 3: Remove existing code signature
        try removeExistingSignature(from: appBundle)

        // Step 4: Replace provisioning profile
        try replaceProvisioningProfile(in: appBundle)

        // Step 5: Update entitlements in Info.plist
        try updateEntitlements(in: appBundle)

        // Step 6: Sign the app bundle
        try signAppBundle(at: appBundle)

        // Step 7: Re-package IPA
        let signedIPAURL = try repackageIPA(from: tempDir, to: outputURL)

        return signedIPAURL
    }

    // MARK: - Private Methods

    private func removeExistingSignature(from appBundle: URL) throws {
        let fileManager = FileManager.default
        let signatureDir = appBundle.appendingPathComponent("_CodeSignature")
        if fileManager.fileExists(atPath: signatureDir.path) {
            try fileManager.removeItem(at: signatureDir)
        }

        // Remove embedded.mobileprovision
        let provisionPath = appBundle.appendingPathComponent("embedded.mobileprovision")
        if fileManager.fileExists(atPath: provisionPath.path) {
            try fileManager.removeItem(at: provisionPath)
        }
    }

    private func replaceProvisioningProfile(in appBundle: URL) throws {
        let provisionPath = appBundle.appendingPathComponent("embedded.mobileprovision")
        try provisioningProfile.profileData.write(to: provisionPath)
    }

    private func updateEntitlements(in appBundle: URL) throws {
        let plistPath = appBundle.appendingPathComponent("Info.plist")
        let plistData = try Data(contentsOf: plistPath)
        guard var plist = try PropertyListSerialization.propertyList(from: plistData, format: nil) as? [String: Any] else {
            throw SigningError.invalidPlist
        }

        // Update bundle identifier if needed
        if let existingBundleId = plist["CFBundleIdentifier"] as? String,
           existingBundleId != provisioningProfile.bundleIdentifier {
            plist["CFBundleIdentifier"] = provisioningProfile.bundleIdentifier
        }

        let updatedData = try PropertyListSerialization.data(fromPropertyList: plist, format: .xml, options: 0)
        try updatedData.write(to: plistPath)
    }

    private func signAppBundle(at appBundle: URL) throws {
        // Generate entitlements plist
        let entitlementsPlist = generateEntitlementsPlist()
        let entitlementsData = try PropertyListSerialization.data(fromPropertyList: entitlementsPlist, format: .xml, options: 0)
        let entitlementsPath = FileManager.default.temporaryDirectory
            .appendingPathComponent("entitlements.plist")
        try entitlementsData.write(to: entitlementsPath)
        defer { try? FileManager.default.removeItem(at: entitlementsPath) }

        // Sign with codesign
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/codesign")
        process.arguments = [
            "--force",
            "--sign", "-",
            "--entitlements", entitlementsPath.path,
            "--timestamp",
            "--generate-pre-entitlements-hash",
            appBundle.path
        ]

        // Set CODESIGNING_ALLOWED environment variable
        var env = ProcessInfo.processInfo.environment
        env["CODESIGNING_ALLOWED"] = "YES"
        process.environment = env

        let errorPipe = Pipe()
        process.standardError = errorPipe
        try process.run()
        process.waitUntilExit()

        guard process.terminationStatus == 0 else {
            let errorData = errorPipe.fileHandleForReading.readDataToEndOfFile()
            let errorString = String(data: errorData, encoding: .utf8) ?? "Unknown error"
            throw SigningError.codesignFailed(errorString)
        }
    }

    private func generateEntitlementsPlist() -> [String: Any] {
        var entitlements: [String: Any] = [:]

        // Application identifier
        entitlements["application-identifier"] = provisioningProfile.teamIdentifier + "." + provisioningProfile.bundleIdentifier

        // Get task allow
        entitlements["get-task-allow"] = provisioningProfile.entitlements.getTaskAllowance ?? true

        // Keychain access groups
        if let keychainGroups = provisioningProfile.entitlements.keychainAccessGroups {
            entitlements["keychain-access-groups"] = keychainGroups
        }

        // Associated domains
        if let associatedDomains = provisioningProfile.entitlements.associatedDomains {
            entitlements["com.apple.developer.associated-domains"] = associatedDomains
        }

        // App groups
        if let appGroups = provisioningProfile.entitlements.comAppleDeveloperAppGroups {
            entitlements["com.apple.security.application-groups"] = appGroups
        }

        // APS environment
        if let apsEnvironment = provisioningProfile.entitlements.apsEnvironment {
            entitlements["aps-environment"] = apsEnvironment
        }

        // Inter-app audio
        if let interAppAudio = provisioningProfile.entitlements.interAppAudio {
            entitlements["inter-app-audio"] = interAppAudio
        }

        return entitlements
    }

    private func repackageIPA(from sourceDir: URL, to outputURL: URL) throws -> URL {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/zip")
        process.arguments = ["-r", "-q", outputURL.path, "Payload"]
        process.currentDirectoryURL = sourceDir

        let errorPipe = Pipe()
        process.standardError = errorPipe
        try process.run()
        process.waitUntilExit()

        guard process.terminationStatus == 0 else {
            throw SigningError.repackageFailed
        }

        return outputURL
    }
}

// MARK: - Signing Errors

public enum SigningError: LocalizedError {
    case invalidPlist
    case codesignFailed(String)
    case repackageFailed
    case invalidCertificate
    case provisioningProfileMismatch

    public var errorDescription: String? {
        switch self {
        case .invalidPlist:
            return "Invalid plist format"
        case .codesignFailed(let details):
            return "Code signing failed: \(details)"
        case .repackageFailed:
            return "Failed to repackage IPA"
        case .invalidCertificate:
            return "Invalid or expired certificate"
        case .provisioningProfileMismatch:
            return "Provisioning profile does not match the app"
        }
    }
}
