import Foundation

/// Represents an IPA (iOS App Archive) file
public struct IPA: Sendable {
    public let url: URL
    public let bundleIdentifier: String
    public let version: String
    public let buildNumber: String
    public let appVersionDate: Date?
    public let size: Int64

    public init(url: URL) throws {
        self.url = url

        let fileManager = FileManager.default
        let attributes = try fileManager.attributesOfItem(atPath: url.path)
        self.size = (attributes[.size] as? Int64) ?? 0

        // Parse Info.plist from IPA
        let tempDir = FileManager.default.temporaryDirectory
            .appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: tempDir) }

        try IPAExtractor.extract(ipaAt: url, to: tempDir)

        let appBundle = try IPAExtractor.findAppBundle(in: tempDir)
        let plistPath = appBundle.appendingPathComponent("Info.plist")
        let plistData = try Data(contentsOf: plistPath)
        guard let plist = try PropertyListSerialization.propertyList(from: plistData, format: nil) as? [String: Any] else {
            throw IPAError.invalidPlist
        }

        self.bundleIdentifier = plist["CFBundleIdentifier"] as? String ?? "unknown"
        self.version = plist["CFBundleShortVersionString"] as? String ?? "1.0"
        self.buildNumber = plist["CFBundleVersion"] as? String ?? "1"
        self.appVersionDate = plist["MinimumOSVersion"] as? Date
    }
}

// MARK: - IPA Extraction

public enum IPAExtractor {
    public static func extract(ipaAt url: URL, to destination: URL) throws {
        let fileManager = FileManager.default
        try fileManager.createDirectory(at: destination, withIntermediateDirectories: true)

        // Use /usr/bin/unzip to extract IPA
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/unzip")
        process.arguments = ["-o", url.path, "-d", destination.path]
        process.standardOutput = nil
        process.standardError = nil
        try process.run()
        process.waitUntilExit()

        guard process.terminationStatus == 0 else {
            throw IPAError.extractionFailed
        }
    }

    public static func findAppBundle(in directory: URL) throws -> URL {
        let fileManager = FileManager.default
        let contents = try fileManager.contentsOfDirectory(at: directory, includingPropertiesForKeys: nil)

        // Look for .app directory
        guard let appDir = contents.first(where: { $0.pathExtension == "app" }) else {
            // Check Payload directory
            let payloadDir = directory.appendingPathComponent("Payload")
            if fileManager.fileExists(atPath: payloadDir.path) {
                let payloadContents = try fileManager.contentsOfDirectory(at: payloadDir, includingPropertiesForKeys: nil)
                if let appDir = payloadContents.first(where: { $0.pathExtension == "app" }) {
                    return appDir
                }
            }
            throw IPAError.noAppBundle
        }

        return appDir
    }
}

// MARK: - IPA Errors

public enum IPAError: LocalizedError {
    case invalidPlist
    case extractionFailed
    case noAppBundle
    case fileNotFound

    public var errorDescription: String? {
        switch self {
        case .invalidPlist:
            return "Invalid Info.plist format"
        case .extractionFailed:
            return "Failed to extract IPA archive"
        case .noAppBundle:
            return "No .app bundle found in IPA"
        case .fileNotFound:
            return "File not found"
        }
    }
}
