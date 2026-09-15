import SwiftUI
import Combine

/// Global app state management
class AppState: ObservableObject {
    @Published var installedApps: [InstalledApp] = []
    @Published var availableApps: [AvailableApp] = []
    @Published var isRefreshing = false
    @Published var lastRefreshDate: Date?
    @Published var currentTab: Tab = .apps

    let storage = AppStateStorage()

    enum Tab: String, CaseIterable {
        case apps = "Apps"
        case sources = "Sources"
        case settings = "Settings"
    }

    init() {
        loadState()
    }

    func loadState() {
        installedApps = storage.loadInstalledApps()
        availableApps = storage.loadAvailableApps()
    }

    func saveState() {
        storage.saveInstalledApps(installedApps)
        storage.saveAvailableApps(availableApps)
    }

    func addInstalledApp(_ app: InstalledApp) {
        installedApps.append(app)
        saveState()
    }

    func removeInstalledApp(_ app: InstalledApp) {
        installedApps.removeAll { $0.bundleIdentifier == app.bundleIdentifier }
        saveState()
    }

    func updateApp(_ app: InstalledApp) {
        if let index = installedApps.firstIndex(where: { $0.bundleIdentifier == app.bundleIdentifier }) {
            installedApps[index] = app
            saveState()
        }
    }
}

/// Represents an installed app
struct InstalledApp: Identifiable, Codable {
    let id: UUID
    let name: String
    let bundleIdentifier: String
    let version: String
    let buildNumber: String
    let iconURL: URL?
    let installDate: Date
    var expirationDate: Date
    var isRefreshing: Bool

    var isExpired: Bool {
        Date() > expirationDate
    }

    var daysRemaining: Int {
        let calendar = Calendar.current
        let components = calendar.dateComponents([.day], from: Date(), to: expirationDate)
        return max(0, components.day ?? 0)
    }

    var statusColor: Color {
        if isExpired {
            return .red
        } else if daysRemaining <= 2 {
            return .orange
        } else {
            return .green
        }
    }

    init(id: UUID = UUID(), name: String, bundleIdentifier: String, version: String, buildNumber: String, iconURL: URL? = nil, installDate: Date = Date(), expirationDate: Date, isRefreshing: Bool = false) {
        self.id = id
        self.name = name
        self.bundleIdentifier = bundleIdentifier
        self.version = version
        self.buildNumber = buildNumber
        self.iconURL = iconURL
        self.installDate = installDate
        self.expirationDate = expirationDate
        self.isRefreshing = isRefreshing
    }
}

/// Represents an available app from sources
struct AvailableApp: Identifiable, Codable {
    let id: UUID
    let name: String
    let bundleIdentifier: String
    let version: String
    let description: String
    let iconURL: URL?
    let downloadURL: URL
    let sourceName: String
    let size: Int64?

    init(id: UUID = UUID(), name: String, bundleIdentifier: String, version: String, description: String, iconURL: URL? = nil, downloadURL: URL, sourceName: String, size: Int64? = nil) {
        self.id = id
        self.name = name
        self.bundleIdentifier = bundleIdentifier
        self.version = version
        self.description = description
        self.iconURL = iconURL
        self.downloadURL = downloadURL
        self.sourceName = sourceName
        self.size = size
    }
}
