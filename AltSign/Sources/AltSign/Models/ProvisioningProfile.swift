import Foundation

/// Represents an Apple Provisioning Profile
public struct ProvisioningProfile: Codable, Identifiable, Sendable {
    public let id: UUID
    public let name: String
    public let uuid: String
    public let teamIdentifier: String
    public let teamName: String
    public let appIdName: String
    public let bundleIdentifier: String
    public let entitlements: Entitlements
    public let certificates: [Certificate]
    public let devices: [Device]
    public let expirationDate: Date
    public let profileData: Data
    public let createdDate: Date
    public let isWildCard: Bool

    public var isExpired: Bool {
        Date() > expirationDate
    }

    public var isValid: Bool {
        !isExpired && !certificates.allSatisfy { $0.isExpired }
    }

    public struct Entitlements: Codable, Sendable {
        public let applicationIdentifier: String?
        public let apsEnvironment: String?
        public let keychainAccessGroups: [String]?
        public let associatedDomains: [String]?
        public let comAppleDeveloperAppGroups: [String]?
        public let getTaskAllowance: Bool?
        public let betaReportsActive: Bool?
        public let interAppAudio: Bool?

        public init() {
            self.applicationIdentifier = nil
            self.apsEnvironment = nil
            self.keychainAccessGroups = nil
            self.associatedDomains = nil
            self.comAppleDeveloperAppGroups = nil
            self.getTaskAllowance = nil
            self.betaReportsActive = nil
            self.interAppAudio = nil
        }
    }
}
