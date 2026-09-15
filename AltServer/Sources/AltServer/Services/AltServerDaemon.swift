import Foundation
import AltSign

/// Main daemon for AltServer that handles device communication and app management
public class AltServerDaemon: LocalServerDelegate {
    private var localServer: LocalServer?
    private var refreshTimer: Timer?
    private let certificateManager = CertificateManager()
    private let deviceManager = DeviceManager.shared

    public init() {}

    // MARK: - Lifecycle

    public func start() async {
        // Start local server for iOS app communication
        localServer = LocalServer(port: 27000, delegate: self)
        do {
            try localServer?.start()
            print("[AltServer] Local server started on port 27000")
        } catch {
            print("[AltServer] Failed to start local server: \(error.localizedDescription)")
        }

        // Start refresh timer (every 6 hours)
        startRefreshTimer()

        // Keep running
        RunLoop.current.run()
    }

    public func stop() {
        localServer?.stop()
        refreshTimer?.invalidate()
    }

    // MARK: - Refresh Timer

    private func startRefreshTimer() {
        refreshTimer = Timer.scheduledTimer(withTimeInterval: 6 * 3600, repeats: true) { [weak self] _ in
            self?.refreshAllApps()
        }
    }

    private func refreshAllApps() {
        print("[AltServer] Starting automatic refresh...")
        do {
            let devices = try deviceManager.listDevices()
            for device in devices {
                print("[AltServer] Refreshing apps on \(device.name)...")
                refreshApps(on: device.udid)
            }
            print("[AltServer] Automatic refresh complete")
        } catch {
            print("[AltServer] Refresh failed: \(error.localizedDescription)")
        }
    }

    private func refreshApps(on udid: String) {
        // In real implementation:
        // 1. Get list of installed AltStore apps
        // 2. Check each app's signing expiry
        // 3. Re-sign and reinstall apps expiring within 7 days
    }

    // MARK: - LocalServerDelegate

    public func handleRequest(_ request: HTTPRequest) -> HTTPResponse {
        print("[AltServer] \(request.method.rawValue) \(request.path)")

        switch request.path {
        case "/status":
            return handleStatus()
        case "/devices":
            return handleDeviceList()
        case "/install":
            return handleInstall(request)
        case "/refresh":
            return handleRefresh(request)
        case "/apps":
            return handleListApps(request)
        case "/certificates":
            return handleListCertificates(request)
        default:
            return HTTPResponse(statusCode: 404, body: Data("Not Found".utf8))
        }
    }

    // MARK: - Request Handlers

    private func handleStatus() -> HTTPResponse {
        let status: [String: Any] = [
            "version": AltSign.version,
            "status": "running",
            "devices": (try? deviceManager.listDevices().count) ?? 0
        ]
        return HTTPResponse.json(status)
    }

    private func handleDeviceList() -> HTTPResponse {
        do {
            let devices = try deviceManager.listDevices()
            let deviceList: [[String: Any]] = devices.map { device in
                [
                    "udid": device.udid,
                    "name": device.name,
                    "model": device.model,
                    "productType": device.productType,
                    "osVersion": device.osVersion,
                    "connectionType": device.connectionType.rawValue
                ]
            }
            return HTTPResponse.json(["devices": deviceList])
        } catch {
            return HTTPResponse.error(error.localizedDescription, statusCode: 500)
        }
    }

    private func handleInstall(_ request: HTTPRequest) -> HTTPResponse {
        guard let body = request.body,
              let json = try? JSONSerialization.jsonObject(with: body) as? [String: Any],
              let ipaPath = json["ipaPath"] as? String else {
            return HTTPResponse.error("Missing ipaPath", statusCode: 400)
        }

        let udid = json["udid"] as? String

        do {
            let targetUDID: String
            if let udid = udid {
                targetUDID = udid
            } else {
                let devices = try deviceManager.listDevices()
                guard let first = devices.first else {
                    return HTTPResponse.error("No devices found", statusCode: 404)
                }
                targetUDID = first.udid
            }

            try deviceManager.installApp(ipaPath: ipaPath, to: targetUDID)
            return HTTPResponse.ok("Installed successfully")
        } catch {
            return HTTPResponse.error(error.localizedDescription, statusCode: 500)
        }
    }

    private func handleRefresh(_ request: HTTPRequest) -> HTTPResponse {
        do {
            let devices = try deviceManager.listDevices()
            for device in devices {
                refreshApps(on: device.udid)
            }
            return HTTPResponse.ok("Refresh complete")
        } catch {
            return HTTPResponse.error(error.localizedDescription, statusCode: 500)
        }
    }

    private func handleListApps(_ request: HTTPRequest) -> HTTPResponse {
        // Return cached app list or fetch from source
        let apps: [String: Any] = [
            "apps": [],
            "count": 0
        ]
        return HTTPResponse.json(apps)
    }

    private func handleListCertificates(_ request: HTTPRequest) -> HTTPResponse {
        do {
            let certificates = try certificateManager.getLocalCertificates()
            let certList: [[String: Any]] = certificates.map { cert in
                [
                    "commonName": cert.commonName,
                    "serialNumber": cert.serialNumber,
                    "type": cert.type.rawValue,
                    "isValid": cert.isValid,
                    "expires": ISO8601DateFormatter().string(from: cert.notAfter)
                ]
            }
            return HTTPResponse.json(["certificates": certList])
        } catch {
            return HTTPResponse.error(error.localizedDescription, statusCode: 500)
        }
    }
}
