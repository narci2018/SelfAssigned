import Foundation

/// Handles persistence of app state using UserDefaults and file storage
class AppStateStorage {
    private let installedAppsKey = "AltStore_InstalledApps"
    private let availableAppsKey = "AltStore_AvailableApps"
    private let sourcesKey = "AltStore_Sources"

    private let fileManager = FileManager.default
    private let storageDirectory: URL

    init() {
        let appSupport = fileManager.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        self.storageDirectory = appSupport.appendingPathComponent("AltStore")
        try? fileManager.createDirectory(at: storageDirectory, withIntermediateDirectories: true)
    }

    // MARK: - Installed Apps

    func loadInstalledApps() -> [InstalledApp] {
        let fileURL = storageDirectory.appendingPathComponent("installed_apps.json")
        guard fileManager.fileExists(atPath: fileURL.path),
              let data = try? Data(contentsOf: fileURL),
              let apps = try? JSONDecoder().decode([InstalledApp].self, from: data) else {
            return []
        }
        return apps
    }

    func saveInstalledApps(_ apps: [InstalledApp]) {
        let fileURL = storageDirectory.appendingPathComponent("installed_apps.json")
        guard let data = try? JSONEncoder().encode(apps) else { return }
        try? data.write(to: fileURL)
    }

    // MARK: - Available Apps

    func loadAvailableApps() -> [AvailableApp] {
        let fileURL = storageDirectory.appendingPathComponent("available_apps.json")
        guard fileManager.fileExists(atPath: fileURL.path),
              let data = try? Data(contentsOf: fileURL),
              let apps = try? JSONDecoder().decode([AvailableApp].self, from: data) else {
            return []
        }
        return apps
    }

    func saveAvailableApps(_ apps: [AvailableApp]) {
        let fileURL = storageDirectory.appendingPathComponent("available_apps.json")
        guard let data = try? JSONEncoder().encode(apps) else { return }
        try? data.write(to: fileURL)
    }

    // MARK: - Sources

    func loadSources() -> [StoredSource] {
        let fileURL = storageDirectory.appendingPathComponent("sources.json")
        guard fileManager.fileExists(atPath: fileURL.path),
              let data = try? Data(contentsOf: fileURL),
              let sources = try? JSONDecoder().decode([StoredSource].self, from: data) else {
            return []
        }
        return sources
    }

    func saveSources(_ sources: [StoredSource]) {
        let fileURL = storageDirectory.appendingPathComponent("sources.json")
        guard let data = try? JSONEncoder().encode(sources) else { return }
        try? data.write(to: fileURL)
    }

    // MARK: - Settings

    func loadSetting<T>(forKey key: String, defaultValue: T) -> T {
        return UserDefaults.standard.object(forKey: key) as? T ?? defaultValue
    }

    func saveSetting<T>(value: T, forKey key: String) {
        UserDefaults.standard.set(value, forKey: key)
    }
}

/// Stored source configuration
struct StoredSource: Codable, Identifiable {
    let id: UUID
    let name: String
    let url: URL
    let isEnabled: Bool
    let lastUpdated: Date?

    init(id: UUID = UUID(), name: String, url: URL, isEnabled: Bool = true, lastUpdated: Date? = nil) {
        self.id = id
        self.name = name
        self.url = url
        self.isEnabled = isEnabled
        self.lastUpdated = lastUpdated
    }
}
