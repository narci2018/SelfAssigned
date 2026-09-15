import SwiftUI

struct SourcesView: View {
    @EnvironmentObject var appState: AppState
    @State private var sources: [StoredSource] = []
    @State private var showingAddSource = false
    @State private var searchText = ""
    @State private var availableApps: [AvailableApp] = []

    var body: some View {
        NavigationStack {
            List {
                // Search Bar
                Section {
                    HStack {
                        Image(systemName: "magnifyingglass")
                            .foregroundColor(.secondary)
                        TextField("Search apps...", text: $searchText)
                    }
                }

                // Sources Section
                Section {
                    ForEach(sources) { source in
                        SourceRow(source: source)
                    }
                    .onDelete(perform: deleteSources)

                    Button {
                        showingAddSource = true
                    } label: {
                        HStack {
                            Image(systemName: "plus.circle.fill")
                                .foregroundColor(.green)
                            Text("Add Source")
                        }
                    }
                } header: {
                    Text("Sources (\(sources.count))")
                }

                // Available Apps Section
                Section {
                    if filteredApps.isEmpty {
                        VStack(spacing: 12) {
                            Image(systemName: "app.badge")
                                .font(.largeTitle)
                                .foregroundColor(.secondary)
                            Text(searchText.isEmpty ? "No apps available" : "No matching apps")
                                .font(.headline)
                        }
                        .frame(maxWidth: .infinity)
                        .padding()
                    } else {
                        ForEach(filteredApps) { app in
                            AvailableAppRow(app: app)
                        }
                    }
                } header: {
                    Text("Available Apps (\(filteredApps.count))")
                }
            }
            .navigationTitle("Sources")
            .sheet(isPresented: $showingAddSource) {
                AddSourceView { name, url in
                    addSource(name: name, url: url)
                }
            }
            .refreshable {
                await refreshSources()
            }
            .task {
                await loadApps()
            }
        }
    }

    // MARK: - Computed Properties

    var filteredApps: [AvailableApp] {
        if searchText.isEmpty {
            return availableApps
        }
        return availableApps.filter { app in
            app.name.localizedCaseInsensitiveContains(searchText) ||
            app.bundleIdentifier.localizedCaseInsensitiveContains(searchText) ||
            app.description.localizedCaseInsensitiveContains(searchText)
        }
    }

    // MARK: - Actions

    private func addSource(name: String, url: URL) {
        let source = StoredSource(name: name, url: url)
        sources.append(source)
        saveSources()
        Task {
            await refreshSource(source)
        }
    }

    private func deleteSources(at offsets: IndexSet) {
        sources.remove(atOffsets: offsets)
        saveSources()
    }

    private func saveSources() {
        let storage = AppStateStorage()
        storage.saveSources(sources)
    }

    private func loadSources() {
        let storage = AppStateStorage()
        sources = storage.loadSources()
    }

    private func refreshSources() async {
        for source in sources {
            await refreshSource(source)
        }
    }

    private func refreshSource(_ source: StoredSource) async {
        // Fetch apps from source URL
        guard let (data, _) = try? await URLSession.shared.data(from: source.url),
              let sourceData = try? JSONDecoder().decode(SourceJSON.self, from: data) else {
            return
        }

        let apps = sourceData.apps.compactMap { app -> AvailableApp? in
            guard let downloadURL = URL(string: app.downloadURL) else { return nil }
            return AvailableApp(
                name: app.name,
                bundleIdentifier: app.bundleIdentifier,
                version: app.version,
                description: app.localizedDescription,
                iconURL: URL(string: app.iconURL ?? ""),
                downloadURL: downloadURL,
                sourceName: source.name
            )
        }

        availableApps.append(contentsOf: apps)
    }

    private func loadApps() async {
        loadSources()
        await refreshSources()
    }
}

// MARK: - Source Row

struct SourceRow: View {
    let source: StoredSource

    var body: some View {
        HStack {
            Image(systemName: "globe")
                .foregroundColor(.blue)
                .frame(width: 32, height: 32)

            VStack(alignment: .leading, spacing: 4) {
                Text(source.name)
                    .font(.headline)
                Text(source.url.absoluteString)
                    .font(.caption)
                    .foregroundColor(.secondary)
                    .lineLimit(1)
            }

            Spacer()

            if let lastUpdated = source.lastUpdated {
                Text(lastUpdated.formatted(.relative(presentation: .named)))
                    .font(.caption2)
                    .foregroundColor(.secondary)
            }
        }
    }
}

// MARK: - Available App Row

struct AvailableAppRow: View {
    let app: AvailableApp
    @State private var isInstalling = false
    @State private var showInstallConfirmation = false

    var body: some View {
        HStack(spacing: 12) {
            AsyncImage(url: app.iconURL) { image in
                image
                    .resizable()
                    .scaledToFit()
            } placeholder: {
                RoundedRectangle(cornerRadius: 8)
                    .fill(Color.blue.gradient)
                    .overlay {
                        Image(systemName: "app.fill")
                            .foregroundColor(.white)
                    }
            }
            .frame(width: 48, height: 48)

            VStack(alignment: .leading, spacing: 4) {
                Text(app.name)
                    .font(.headline)

                Text(app.description)
                    .font(.caption)
                    .foregroundColor(.secondary)
                    .lineLimit(2)

                HStack {
                    Text("v\(app.version)")
                        .font(.caption2)
                        .foregroundColor(.secondary)

                    Text("•")
                        .font(.caption2)
                        .foregroundColor(.secondary)

                    Text(app.sourceName)
                        .font(.caption2)
                        .foregroundColor(.secondary)
                }
            }

            Spacer()

            Button {
                showInstallConfirmation = true
            } label: {
                if isInstalling {
                    ProgressView()
                        .scaleEffect(0.8)
                } else {
                    Text("GET")
                        .font(.caption)
                        .fontWeight(.bold)
                        .padding(.horizontal, 12)
                        .padding(.vertical, 6)
                        .background(Color.blue)
                        .foregroundColor(.white)
                        .cornerRadius(8)
                }
            }
            .disabled(isInstalling)
        }
        .padding(.vertical, 4)
        .alert("Install \(app.name)?", isPresented: $showInstallConfirmation) {
            Button("Cancel", role: .cancel) {}
            Button("Install") {
                installApp()
            }
        } message: {
            Text("This will download and install \(app.name) v\(app.version).")
        }
    }

    private func installApp() {
        isInstalling = true

        Task {
            do {
                let tempDir = FileManager.default.temporaryDirectory
                    .appendingPathComponent("AltStore_Downloads")
                try FileManager.default.createDirectory(at: tempDir, withIntermediateDirectories: true)

                let ipaURL = tempDir.appendingPathComponent("\(app.bundleIdentifier).ipa")
                let (tempFileURL, _) = try await URLSession.shared.download(from: app.downloadURL)
                try FileManager.default.moveItem(at: tempFileURL, to: ipaURL)

                // In real implementation, send to AltServer for installation
                // For now, just mark as done
                isInstalling = false
            } catch {
                isInstalling = false
            }
        }
    }
}

// MARK: - Add Source View

struct AddSourceView: View {
    @Environment(\.dismiss) private var dismiss
    @State private var sourceName = ""
    @State private var sourceURL = ""
    let onAdd: (String, URL) -> Void

    var body: some View {
        NavigationStack {
            Form {
                Section {
                    TextField("Source Name", text: $sourceName)
                    TextField("Source URL", text: $sourceURL)
                        .keyboardType(.URL)
                        .autocapitalization(.none)
                } header: {
                    Text("Add Source")
                } footer: {
                    Text("Enter the name and URL of the source you want to add.")
                }

                Section {
                    Button("Add Source") {
                        if let url = URL(string: sourceURL), !sourceName.isEmpty {
                            onAdd(sourceName, url)
                            dismiss()
                        }
                    }
                    .disabled(sourceName.isEmpty || sourceURL.isEmpty)
                }
            }
            .navigationTitle("New Source")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .navigationBarLeading) {
                    Button("Cancel") {
                        dismiss()
                    }
                }
            }
        }
    }
}

// MARK: - Source JSON Model

private struct SourceJSON: Codable {
    let apps: [AppJSON]
    let tintColor: String?

    struct AppJSON: Codable {
        let name: String
        let bundleIdentifier: String
        let version: String
        let downloadURL: String
        let iconURL: String?
        let localizedDescription: String
        let screenshots: [String]?
    }
}

// MARK: - Preview

#Preview {
    SourcesView()
        .environmentObject(AppState())
}
