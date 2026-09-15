import Foundation

/// Client for Apple Developer Portal API
public class AppleDeveloperPortalClient: @unchecked Sendable {
    private let session: URLSession
    private let baseURL = "https://developer.apple.com/services-account/QH65B2/account"
    private var authTokens: AuthTokens?

    public struct AuthTokens: Sendable {
        public let dsid: String
        public let provider: String
        public let sessionToken: String
        public let csrfToken: String
    }

    public init() {
        let config = URLSessionConfiguration.default
        config.httpCookieStorage = HTTPCookieStorage.shared
        self.session = URLSession(configuration: config)
    }

    // MARK: - Authentication

    /// Authenticate with Apple Developer Portal using Apple ID
    public func authenticate(appleID: String, password: String, teamID: String) async throws -> AuthTokens {
        let anisetteData = try await fetchAnisetteData()

        // Step 1: Initiate auth
        let authInitURL = URL(string: "https://idmsa.apple.com/appleauth/auth/signin")!
        var authInitRequest = URLRequest(url: authInitURL)
        authInitRequest.httpMethod = "POST"
        authInitRequest.addValue("application/json", forHTTPHeaderField: "Content-Type")
        authInitRequest.addValue(anisetteData.adid, forHTTPHeaderField: "X-Apple-ADID")
        authInitRequest.addValue(anisetteData.xAppleLODToken, forHTTPHeaderField: "X-Apple-LOD-Token")
        authInitRequest.addValue(anisetteData.xAppleDeviceUDID, forHTTPHeaderField: "X-Apple-Device-UDID")

        let authBody: [String: String] = [
            "accountName": appleID,
            "password": password,
            "saveItem": "false"
        ]
        authInitRequest.httpBody = try JSONSerialization.data(withJSONObject: authBody)

        let (authData, authResponse) = try await session.data(for: authInitRequest)
        guard let httpResponse = authResponse as? HTTPURLResponse else {
            throw PortalError.invalidResponse
        }

        // Handle 2FA if needed
        if httpResponse.statusCode == 409 {
            throw PortalError.twoFactorRequired
        }

        guard httpResponse.statusCode == 200 else {
            throw PortalError.authenticationFailed
        }

        // Parse session tokens
        guard let responseDict = try JSONSerialization.jsonObject(with: authData) as? [String: Any],
              let data = responseDict["data"] as? [String: Any],
              let authToken = data["auth"] as? String else {
            throw PortalError.invalidResponse
        }

        // Step 2: Get portal session
        let portalSession = try await getPortalSession(authToken: authToken, teamID: teamID)

        self.authTokens = portalSession
        return portalSession
    }

    // MARK: - Certificate Management

    /// Fetch all certificates for the authenticated team
    public func fetchCertificates() async throws -> [Certificate] {
        guard let tokens = authTokens else {
            throw PortalError.notAuthenticated
        }

        let url = URL(string: "\(baseURL)/resources/listCertificates")!
        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.addValue("application/x-www-form-urlencoded", forHTTPHeaderField: "Content-Type")
        request.addValue("true", forHTTPHeaderField: "X-Requested-With")
        request.addValue(tokens.csrfToken, forHTTPHeaderField: "X-Csrf-Token")

        let body = "teamId=\(tokens.provider)"
        request.httpBody = body.data(using: .utf8)

        let (data, _) = try await session.data(for: request)
        guard let responseDict = try JSONSerialization.jsonObject(with: data) as? [String: Any],
              let certificates = responseDict["certificates"] as? [[String: Any]] else {
            return []
        }

        return certificates.compactMap { dict in
            parseCertificate(from: dict)
        }
    }

    // MARK: - Device Management

    /// Register a new device
    public func registerDevice(name: String, udid: String) async throws -> Device {
        guard let tokens = authTokens else {
            throw PortalError.notAuthenticated
        }

        let url = URL(string: "\(baseURL)/resources/addDevice")!
        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.addValue("application/x-www-form-urlencoded", forHTTPHeaderField: "Content-Type")
        request.addValue("true", forHTTPHeaderField: "X-Requested-With")
        request.addValue(tokens.csrfToken, forHTTPHeaderField: "X-Csrf-Token")

        let body = "teamId=\(tokens.provider)&name=\(name)&deviceNumber=\(udid)&platform=ios"
        request.httpBody = body.data(using: .utf8)

        let (data, _) = try await session.data(for: request)
        guard let responseDict = try JSONSerialization.jsonObject(with: data) as? [String: Any],
              let device = responseDict["device"] as? [String: Any] else {
            throw PortalError.deviceRegistrationFailed
        }

        return Device(
            id: UUID(),
            name: device["name"] as? String ?? name,
            identifier: device["deviceNumber"] as? String ?? udid,
            deviceClass: Device.DeviceClass(rawValue: device["deviceClass"] as? String ?? "iPhone"),
            model: device["model"] as? String,
            platform: .ios
        )
    }

    /// Fetch all registered devices
    public func fetchDevices() async throws -> [Device] {
        guard let tokens = authTokens else {
            throw PortalError.notAuthenticated
        }

        let url = URL(string: "\(baseURL)/resources/listDevices")!
        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.addValue("application/x-www-form-urlencoded", forHTTPHeaderField: "Content-Type")
        request.addValue("true", forHTTPHeaderField: "X-Requested-With")
        request.addValue(tokens.csrfToken, forHTTPHeaderField: "X-Csrf-Token")

        let body = "teamId=\(tokens.provider)"
        request.httpBody = body.data(using: .utf8)

        let (data, _) = try await session.data(for: request)
        guard let responseDict = try JSONSerialization.jsonObject(with: data) as? [String: Any],
              let devices = responseDict["devices"] as? [[String: Any]] else {
            return []
        }

        return devices.compactMap { dict in
            Device(
                id: UUID(),
                name: dict["name"] as? String ?? "",
                identifier: dict["deviceNumber"] as? String ?? "",
                deviceClass: Device.DeviceClass(rawValue: dict["deviceClass"] as? String ?? "iPhone"),
                model: dict["model"] as? String,
                platform: .ios
            )
        }
    }

    // MARK: - App ID Management

    /// Register a new App ID
    public func registerAppID(name: String, bundleID: String, isWildcard: Bool = false) async throws -> AppID {
        guard let tokens = authTokens else {
            throw PortalError.notAuthenticated
        }

        let url = URL(string: "\(baseURL)/resources/registerAppId")!
        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.addValue("application/x-www-form-urlencoded", forHTTPHeaderField: "Content-Type")
        request.addValue("true", forHTTPHeaderField: "X-Requested-With")
        request.addValue(tokens.csrfToken, forHTTPHeaderField: "X-Csrf-Token")

        let bundleIDValue = isWildcard ? bundleID + ".*" : bundleID
        let body = "teamId=\(tokens.provider)&identifier=\(bundleIDValue)&name=\(name)&type=explicit&capabilities=1"
        request.httpBody = body.data(using: .utf8)

        let (data, _) = try await session.data(for: request)
        guard let responseDict = try JSONSerialization.jsonObject(with: data) as? [String: Any],
              let appId = responseDict["appId"] as? [String: Any] else {
            throw PortalError.appIDRegistrationFailed
        }

        return AppID(
            id: UUID(),
            name: appId["name"] as? String ?? name,
            bundleIdentifier: appId["identifier"] as? String ?? bundleID,
            appIdId: appId["appIdId"] as? String ?? "",
            isWildcard: isWildcard,
            platform: .ios
        )
    }

    // MARK: - Provisioning Profile Management

    /// Generate a new development provisioning profile
    public func generateDevelopmentProfile(
        name: String,
        appID: AppID,
        certificate: Certificate,
        devices: [Device]
    ) async throws -> ProvisioningProfile {
        guard let tokens = authTokens else {
            throw PortalError.notAuthenticated
        }

        let url = URL(string: "\(baseURL)/resources/generateDevelopmentProvisioningProfile")!
        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.addValue("application/x-www-form-urlencoded", forHTTPHeaderField: "Content-Type")
        request.addValue("true", forHTTPHeaderField: "X-Requested-With")
        request.addValue(tokens.csrfToken, forHTTPHeaderField: "X-Csrf-Token")

        let deviceIDs = devices.map { $0.identifier }.joined(separator: ",")
        let body = """
            teamId=\(tokens.provider)
            &profileName=\(name)
            &appIdId=\(appID.appIdId)
            &certificateId=\(certificate.serialNumber)
            &deviceIds=\(deviceIDs)
            """.replacingOccurrences(of: "\n", with: "")
        request.httpBody = body.data(using: .utf8)

        let (data, _) = try await session.data(for: request)
        guard let responseDict = try JSONSerialization.jsonObject(with: data) as? [String: Any],
              let profile = responseDict["provisioningProfile"] as? [String: Any],
              let profileData = profile["encodedProfile"] as? String,
              let decodedData = Data(base64Encoded: profileData) else {
            throw PortalError.profileGenerationFailed
        }

        return ProvisioningProfile(
            id: UUID(),
            name: profile["name"] as? String ?? name,
            uuid: profile["uuid"] as? String ?? UUID().uuidString,
            teamIdentifier: profile["teamIdentifier"] as? String ?? tokens.provider,
            teamName: profile["teamName"] as? String ?? "",
            appIdName: appID.name,
            bundleIdentifier: appID.bundleIdentifier,
            entitlements: ProvisioningProfile.Entitlements(),
            certificates: [certificate],
            devices: devices,
            expirationDate: ISO8601DateFormatter().date(from: profile["dateExpire"] as? String ?? "") ?? Date().addingTimeInterval(86400 * 365),
            profileData: decodedData,
            createdDate: Date(),
            isWildCard: appID.isWildcard
        )
    }

    // MARK: - Private Helpers

    private func fetchAnisetteData() async throws -> AnisetteData {
        // This is a simplified placeholder
        // Real implementation would use a local Anisette server
        // or libraries like anisette-v3-server
        return AnisetteData(
            adid: UUID().uuidString,
            xAppleLODToken: "",
            xAppleDeviceUDID: UUID().uuidString,
            localUserID: UUID().uuidString
        )
    }

    private func getPortalSession(authToken: String, teamID: String) async throws -> AuthTokens {
        // Simplified - real implementation fetches portal tokens
        return AuthTokens(
            dsid: "",
            provider: teamID,
            sessionToken: authToken,
            csrfToken: ""
        )
    }

    private func parseCertificate(from dict: [String: Any]) -> Certificate? {
        guard let serialNumber = dict["serialNumber"] as? String,
              let displayName = dict["displayName"] as? String else {
            return nil
        }

        return Certificate(
            id: UUID(),
            commonName: displayName,
            teamName: dict["teamName"] as? String ?? "",
            teamIdentifier: dict["teamIdentifier"] as? String ?? "",
            serialNumber: serialNumber,
            notBefore: ISO8601DateFormatter().date(from: dict["certificateStatusCode"] as? String ?? "") ?? Date(),
            notAfter: ISO8601DateFormatter().date(from: dict["expirationDate"] as? String ?? "") ?? Date(),
            certificateData: Data(),
            privateKeyData: Data(),
            machineId: dict["machineId"] as? String
        )
    }
}

// MARK: - Anisette Data

public struct AnisetteData: Sendable {
    public let adid: String
    public let xAppleLODToken: String
    public let xAppleDeviceUDID: String
    public let localUserID: String
}

// MARK: - Portal Errors

public enum PortalError: LocalizedError {
    case invalidResponse
    case authenticationFailed
    case twoFactorRequired
    case notAuthenticated
    case deviceRegistrationFailed
    case appIDRegistrationFailed
    case profileGenerationFailed

    public var errorDescription: String? {
        switch self {
        case .invalidResponse:
            return "Invalid response from Apple Developer Portal"
        case .authenticationFailed:
            return "Authentication failed. Check your Apple ID credentials."
        case .twoFactorRequired:
            return "Two-factor authentication required"
        case .notAuthenticated:
            return "Not authenticated. Please sign in first."
        case .deviceRegistrationFailed:
            return "Failed to register device"
        case .appIDRegistrationFailed:
            return "Failed to register App ID"
        case .profileGenerationFailed:
            return "Failed to generate provisioning profile"
        }
    }
}
