import Foundation

/// Manages app sources (repositories) for discovering and downloading apps
public class SourceManager: @unchecked Sendable {
    public static let shared = SourceManager()

    private var sources: [Source] = []
    private let sourcesKey = "AltStoreSources"
    private let cacheDir: URL

    public struct Source: Codable, Identifiable, Sendable {
        public let id: UUID
        public var name: String
        public var url: URL
        public var iconURL: URL?
        public var tintColor: String?
        public var lastUpdated: Date?
        public var apps: [SourceApp]

        public init(name: String, url: URL) {
            self.id = UUID()
            self.name = name
            self.url = url
            self.iconURL = nil
            self.tintColor = nil
            self.lastUpdated = nil
            self.apps = []
        }
    }

    public struct SourceApp: Codable, Identifiable, Sendable {
        public let id: UUID
        public let name: String
        public let bundleIdentifier: String
        public let version: String
        public let versionDate: String?
        public let downloadURL: URL
        public let iconURL: URL?
        public let tintColor: String?
        public let description: String
        public let size: Int64?
        public let screenshotURLs: [URL]
        public let permissions: AppPermissions?

        public struct AppPermissions: Codable, Sendable {
            public let entitlements: [String]?
            public let privacy: [PrivacyPermission]?

            public struct PrivacyPermission: Codable, Sendable {
                public let usageDescription: String
                public let type: String
            }
        }
    }

    // MARK: - Initialization

    public init() {
        self.cacheDir = FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("AltStoreSources")
        loadSources()
    }

    // MARK: - Source Management

    /// Add a new source
    public func addSource(name: String, url: URL) async throws -> Source {
        var source = Source(name: name, url: url)
        source = try await refreshSource(source)
        sources.append(source)
        saveSources()
        return source
    }

    /// Remove a source
    public func removeSource(_ source: Source) {
        sources.removeAll { $0.id == source.id }
        saveSources()
    }

    /// Get all sources
    public func getSources() -> [Source] {
        return sources
    }

    /// Refresh a specific source
    public func refreshSource(_ source: Source) async throws -> Source {
        var updatedSource = source

        let (data, _) = try await URLSession.shared.data(from: source.url)

        let sourceData = try JSONDecoder().decode(SourceData.self, from: data)

        updatedSource.apps = sourceData.apps.map { app in
            SourceApp(
                id: UUID(),
                name: app.name,
                bundleIdentifier: app.bundleIdentifier,
                version: app.version,
                versionDate: app.versionDate,
                downloadURL: app.downloadURL,
                iconURL: app.iconURL,
                tintColor: app.tintColor,
                description: app.localizedDescription,
                size: nil,
                screenshotURLs: app.screenshots ?? [],
                permissions: nil
            )
        }
        updatedSource.lastUpdated = Date()
        updatedSource.tintColor = sourceData.tintColor

        return updatedSource
    }

    /// Refresh all sources
    public func refreshAllSources() async {
        for i in sources.indices {
            do {
                sources[i] = try await refreshSource(sources[i])
            } catch {
                print("[SourceManager] Failed to refresh \(sources[i].name): \(error)")
            }
        }
        saveSources()
    }

    /// Search for apps across all sources
    public func searchApps(query: String) -> [SourceApp] {
        let lowercasedQuery = query.lowercased()
        return sources.flatMap { $0.apps }
            .filter {
                $0.name.lowercased().contains(lowercasedQuery) ||
                $0.bundleIdentifier.lowercased().contains(lowercasedQuery) ||
                $0.description.lowercased().contains(lowercasedQuery)
            }
    }

    /// Get all apps from all sources
    public func getAllApps() -> [SourceApp] {
        return sources.flatMap { $0.apps }
    }

    /// Download an app
    public func downloadApp(_ app: SourceApp) async throws -> URL {
        let cachePath = cacheDir
            .appendingPathComponent(app.bundleIdentifier)
            .appendingPathComponent("\(app.version).ipa")

        // Check cache
        if FileManager.default.fileExists(atPath: cachePath.path) {
            return cachePath
        }

        // Download
        let (tempURL, _) = try await URLSession.shared.download(from: app.downloadURL)

        // Move to cache
        try FileManager.default.createDirectory(at: cachePath.deletingLastPathComponent(), withIntermediateDirectories: true)
        try FileManager.default.moveItem(at: tempURL, to: cachePath)

        return cachePath
    }

    // MARK: - Default Sources

    public static let defaultSources: [(name: String, url: String)] = [
        // Users can add their preferred sources here
    ]

    // MARK: - Persistence

    private func saveSources() {
        if let data = try? JSONEncoder().encode(sources) {
            UserDefaults.standard.set(data, forKey: sourcesKey)
        }
    }

    private func loadSources() {
        guard let data = UserDefaults.standard.data(forKey: sourcesKey),
              let decoded = try? JSONDecoder().decode([Source].self, from: data) else {
            return
        }
        sources = decoded
    }
}

// MARK: - Source Data Models

private struct SourceData: Codable {
    let apps: [SourceAppData]
    let news: [NewsData]?

    struct SourceAppData: Codable {
        let name: String
        let bundleIdentifier: String
        let version: String
        let versionDate: String?
        let downloadURL: URL
        let iconURL: URL?
        let tintColor: String?
        let localizedDescription: String
        let screenshots: [URL]?
        let appPermissions: SourceApp.AppPermissions?
    }

    struct NewsData: Codable {
        let title: String
        let identifier: String
        let timestamp: String?
        let imageURL: URL?
        let url: URL?
        let tintColor: String?
    }
}
