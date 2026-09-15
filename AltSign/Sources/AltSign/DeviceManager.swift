import Foundation

/// Device communication using libimobiledevice for USB/Wi-Fi connections
public class DeviceManager: @unchecked Sendable {
    public static let shared = DeviceManager()

    private var connectedDevices: [ConnectedDevice] = []

    public struct ConnectedDevice: Sendable {
        public let udid: String
        public let name: String
        public let model: String
        public let productType: String
        public let osVersion: String
        public let isConnected: Bool
        public let connectionType: ConnectionType

        public enum ConnectionType: String, Sendable {
            case usb
            case wifi
        }
    }

    // MARK: - Device Discovery

    /// List all connected iOS devices
    public func listDevices() throws -> [ConnectedDevice] {
        let output = try executeCommand("idevice_id", arguments: ["-l"])
        let udids = output.components(separatedBy: "\n").filter { !$0.isEmpty }

        var devices: [ConnectedDevice] = []
        for udid in udids {
            let trimmedUDID = udid.trimmingCharacters(in: .whitespaces)
            if let device = getDeviceInfo(for: trimmedUDID) {
                devices.append(device)
            }
        }

        self.connectedDevices = devices
        return devices
    }

    /// Get device info by UDID
    public func getDeviceInfo(for udid: String) -> ConnectedDevice? {
        guard let name = try? executeCommand("ideviceinfo", arguments: ["-u", udid, "-k", "DeviceName"]),
              let model = try? executeCommand("ideviceinfo", arguments: ["-u", udid, "-k", "ModelNumber"]),
              let productType = try? executeCommand("ideviceinfo", arguments: ["-u", udid, "-k", "ProductType"]),
              let osVersion = try? executeCommand("ideviceinfo", arguments: ["-u", udid, "-k", "ProductVersion"]) else {
            return nil
        }

        return ConnectedDevice(
            udid: udid,
            name: name.trimmingCharacters(in: .whitespacesAndNewlines),
            model: model.trimmingCharacters(in: .whitespacesAndNewlines),
            productType: productType.trimmingCharacters(in: .whitespacesAndNewlines),
            osVersion: osVersion.trimmingCharacters(in: .whitespacesAndNewlines),
            isConnected: true,
            connectionType: .usb
        )
    }

    // MARK: - App Installation

    /// Install an IPA file on a device
    public func installApp(ipaPath: String, to udid: String) throws {
        _ = try executeCommand("ideviceinstaller", arguments: ["-u", udid, "-i", ipaPath])
    }

    /// Uninstall an app from a device
    public func uninstallApp(bundleID: String, from udid: String) throws {
        _ = try executeCommand("ideviceinstaller", arguments: ["-u", udid, "-U", bundleID])
    }

    /// List installed apps on a device
    public func listInstalledApps(on udid: String) throws -> [String] {
        let output = try executeCommand("ideviceinstaller", arguments: ["-u", udid, "-l"])
        return output.components(separatedBy: "\n")
            .filter { !$0.isEmpty }
            .compactMap { line in
                let parts = line.components(separatedBy: ",")
                return parts.first?.trimmingCharacters(in: .whitespaces)
            }
    }

    // MARK: - Device Pairing

    /// Pair with a device (requires user to trust the computer)
    public func pairDevice(udid: String) throws {
        _ = try executeCommand("idevicepair", arguments: ["-u", udid, "pair"])
    }

    /// Check pairing status
    public func isDevicePaired(udid: String) -> Bool {
        (try? executeCommand("idevicepair", arguments: ["-u", udid, "validate"])) != nil
    }

    // MARK: - Screen Capture

    /// Capture device screenshot
    public func captureScreenshot(udid: String, saveTo path: String) throws {
        _ = try executeCommand("idevicescreenshot", arguments: ["-u", udid, path])
    }

    // MARK: - Diagnostics

    /// Get device diagnostics
    public func getDiagnostics(for udid: String) throws -> [String: Any] {
        let output = try executeCommand("idevicediagnostics", arguments: ["-u", udid, "diagnostics"])
        guard let data = output.data(using: .utf8),
              let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else {
            return [:]
        }
        return json
    }

    // MARK: - Private Helpers

    private func executeCommand(_ command: String, arguments: [String]) throws -> String {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/env")
        process.arguments = [command] + arguments

        let outputPipe = Pipe()
        let errorPipe = Pipe()
        process.standardOutput = outputPipe
        process.standardError = errorPipe

        try process.run()
        process.waitUntilExit()

        let outputData = outputPipe.fileHandleForReading.readDataToEndOfFile()
        let errorData = errorPipe.fileHandleForReading.readDataToEndOfFile()

        guard process.terminationStatus == 0 else {
            let errorString = String(data: errorData, encoding: .utf8) ?? "Unknown error"
            throw DeviceError.commandFailed(command: command, error: errorString)
        }

        return String(data: outputData, encoding: .utf8) ?? ""
    }
}

// MARK: - Device Errors

public enum DeviceError: LocalizedError {
    case commandFailed(command: String, error: String)
    case deviceNotFound
    case notPaired
    case installationFailed(String)

    public var errorDescription: String? {
        switch self {
        case .commandFailed(let command, let error):
            return "Command '\(command)' failed: \(error)"
        case .deviceNotFound:
            return "No iOS device found"
        case .notPaired:
            return "Device is not paired. Please trust this computer on your device."
        case .installationFailed(let details):
            return "App installation failed: \(details)"
        }
    }
}
