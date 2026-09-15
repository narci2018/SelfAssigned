import Foundation

/// Represents an Apple Developer certificate
public struct Certificate: Codable, Identifiable, Sendable {
    public let id: UUID
    public let commonName: String
    public let teamName: String
    public let teamIdentifier: String
    public let serialNumber: String
    public let notBefore: Date
    public let notAfter: Date
    public let certificateData: Data
    public let privateKeyData: Data
    public let machineId: String?

    public init(
        id: UUID,
        commonName: String,
        teamName: String,
        teamIdentifier: String,
        serialNumber: String,
        notBefore: Date,
        notAfter: Date,
        certificateData: Data,
        privateKeyData: Data,
        machineId: String?
    ) {
        self.id = id
        self.commonName = commonName
        self.teamName = teamName
        self.teamIdentifier = teamIdentifier
        self.serialNumber = serialNumber
        self.notBefore = notBefore
        self.notAfter = notAfter
        self.certificateData = certificateData
        self.privateKeyData = privateKeyData
        self.machineId = machineId
    }

    public var isExpired: Bool {
        Date() > notAfter
    }

    public var isValid: Bool {
        let now = Date()
        return now >= notBefore && now <= notAfter
    }

    public var remainingDays: Int {
        let calendar = Calendar.current
        let components = calendar.dateComponents([.day], from: Date(), to: notAfter)
        return max(0, components.day ?? 0)
    }

    public enum CertificateType: String, Codable {
        case development
        case distribution
        case appleDistribution
        case appleDevelopment

        public var appleName: String {
            switch self {
            case .development: return "iOS App Development"
            case .distribution: return "iOS Distribution"
            case .appleDistribution: return "Apple Distribution"
            case .appleDevelopment: return "Apple Development"
            }
        }
    }

    public var type: CertificateType {
        if commonName.contains("Apple Distribution") {
            return .appleDistribution
        } else if commonName.contains("Apple Development") {
            return .appleDevelopment
        } else if commonName.contains("iPhone Distribution") {
            return .distribution
        } else {
            return .development
        }
    }
}
