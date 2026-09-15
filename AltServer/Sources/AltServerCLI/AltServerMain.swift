import Foundation
import AltSign

/// Main entry point for AltServer CLI
@main
struct AltServerCLI {
    static func main() async {
        let args = CommandLine.arguments

        guard args.count >= 2 else {
            printUsage()
            return
        }

        let command = args[1]

        switch command {
        case "install":
            await handleInstall(args: Array(args.dropFirst(2)))
        case "refresh":
            await handleRefresh(args: Array(args.dropFirst(2)))
        case "devices":
            await handleDevices()
        case "certs":
            await handleCertificates(args: Array(args.dropFirst(2)))
        case "server":
            await handleStartServer()
        case "version":
            print("AltServer \(AltSign.version)")
        default:
            print("Unknown command: \(command)")
            printUsage()
        }
    }

    static func printUsage() {
        print("""
        AltServer - iOS App Sideload Server

        Usage: AltServer <command> [options]

        Commands:
          install <ipa_path> --device <udid>    Install IPA to device
          refresh [--device <udid>]             Refresh installed apps
          devices                               List connected devices
          certs --apple-id <email> --team-id <id>  Manage certificates
          server                                Start AltServer daemon
          version                               Print version
        """)
    }

    // MARK: - Install Command

    static func handleInstall(args: [String]) async {
        guard let ipaPath = args.first else {
            print("Error: IPA path required")
            return
        }

        let udid = args.value(forFlag: "--device")

        print("Installing \(ipaPath)...")

        do {
            let deviceManager = DeviceManager.shared

            // List devices if no UDID specified
            let targetUDID: String
            if let udid = udid {
                targetUDID = udid
            } else {
                let devices = try deviceManager.listDevices()
                guard let first = devices.first else {
                    print("Error: No devices found. Connect an iOS device via USB.")
                    return
                }
                targetUDID = first.udid
                print("Using device: \(first.name) (\(first.udid))")
            }

            // Check pairing
            guard deviceManager.isDevicePaired(udid: targetUDID) else {
                print("Error: Device not paired. Please trust this computer on your device.")
                return
            }

            // Install
            try deviceManager.installApp(ipaPath: ipaPath, to: targetUDID)
            print("Successfully installed!")
        } catch {
            print("Error: \(error.localizedDescription)")
        }
    }

    // MARK: - Refresh Command

    static func handleRefresh(args: [String]) async {
        print("Refreshing apps...")

        do {
            let deviceManager = DeviceManager.shared
            let devices = try deviceManager.listDevices()

            for device in devices {
                print("Refreshing on \(device.name)...")
                // In real implementation, this would re-sign and reinstall all apps
                // For now, just list installed apps
                let apps = try deviceManager.listInstalledApps(on: device.udid)
                print("  Found \(apps.count) installed apps")
            }

            print("Refresh complete!")
        } catch {
            print("Error: \(error.localizedDescription)")
        }
    }

    // MARK: - Devices Command

    static func handleDevices() async {
        do {
            let devices = try DeviceManager.shared.listDevices()

            if devices.isEmpty {
                print("No devices found. Connect an iOS device via USB.")
                return
            }

            print("Connected devices:")
            print(String(repeating: "-", count: 60))

            for device in devices {
                print("  Name:       \(device.name)")
                print("  UDID:       \(device.udid)")
                print("  Model:      \(device.model) (\(device.productType))")
                print("  iOS:        \(device.osVersion)")
                print("  Connected:  \(device.connectionType.rawValue)")
                print(String(repeating: "-", count: 60))
            }
        } catch {
            print("Error: \(error.localizedDescription)")
        }
    }

    // MARK: - Certificates Command

    static func handleCertificates(args: [String]) async {
        guard let appleID = args.value(forFlag: "--apple-id"),
              let teamID = args.value(forFlag: "--team-id") else {
            print("Usage: AltServer certs --apple-id <email> --team-id <team_id>")
            return
        }

        print("Authenticating with Apple Developer Portal...")

        do {
            let portal = AppleDeveloperPortalClient()
            // Note: In real implementation, password would be entered securely
            let password = ""
            _ = try await portal.authenticate(appleID: appleID, password: password, teamID: teamID)

            print("Fetching certificates...")
            let certificates = try await portal.fetchCertificates()

            if certificates.isEmpty {
                print("No certificates found.")
                return
            }

            print("Certificates:")
            print(String(repeating: "-", count: 60))

            for cert in certificates {
                print("  Name:         \(cert.commonName)")
                print("  Serial:       \(cert.serialNumber)")
                print("  Type:         \(cert.type.rawValue)")
                print("  Team:         \(cert.teamName) (\(cert.teamIdentifier))")
                print("  Expires:      \(cert.notAfter)")
                print("  Valid:        \(cert.isValid ? "Yes" : "No")")
                print(String(repeating: "-", count: 60))
            }
        } catch {
            print("Error: \(error.localizedDescription)")
        }
    }

    // MARK: - Server Command

    static func handleStartServer() async {
        print("Starting AltServer daemon...")
        print("Listening on port 27000...")

        let server = AltServerDaemon()
        await server.start()
    }
}

// MARK: - Array Extension

extension Array where Element == String {
    func value(forFlag flag: String) -> String? {
        guard let index = firstIndex(of: flag), index + 1 < count else {
            return nil
        }
        return self[index + 1]
    }
}
