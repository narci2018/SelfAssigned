import Foundation

/// Represents an App ID (Bundle Identifier) registered to a developer account
public struct AppID: Codable, Identifiable, Sendable {
    public let id: UUID
    public let name: String
    public let bundleIdentifier: String
    public let appIdId: String
    public let isWildcard: Bool
    public let platform: Platform

    public enum Platform: String, Codable {
        case ios
        case mac
    }
}
