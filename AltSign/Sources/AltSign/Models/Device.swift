import Foundation

/// Represents an iOS device registered to a developer account
public struct Device: Codable, Identifiable, Sendable {
    public let id: UUID
    public let name: String
    public let identifier: String  // UDID
    public let deviceClass: DeviceClass
    public let model: String?
    public let platform: Platform

    public enum DeviceClass: String, Codable, Sendable {
        case iPhone
        case iPad
        case iPod
        case appleWatch
        case appleTV
        case mac
        case unknown

        public init(rawValue: String) {
            switch rawValue.lowercased() {
            case "iphone": self = .iPhone
            case "ipad": self = .iPad
            case "ipod": self = .iPod
            case "watch", "applewatch": self = .appleWatch
            case "tv", "appletv": self = .appleTV
            case "mac": self = .mac
            default: self = .unknown
            }
        }
    }

    public enum Platform: String, Codable, Sendable {
        case ios
        case watchOS
        case tvOS
        case mac
    }
}
