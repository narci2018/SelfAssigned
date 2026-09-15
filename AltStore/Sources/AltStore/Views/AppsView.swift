import SwiftUI

struct AppsView: View {
    @EnvironmentObject var appState: AppState
    @EnvironmentObject var serverManager: ServerManager
    @State private var showingRefreshAlert = false
    @State private var selectedApp: InstalledApp?

    var body: some View {
        NavigationStack {
            List {
                // Connection Status Section
                Section {
                    HStack {
                        Image(systemName: serverManager.isConnected ? "checkmark.circle.fill" : "xmark.circle.fill")
                            .foregroundColor(serverManager.isConnected ? .green : .red)

                        VStack(alignment: .leading) {
                            Text(serverManager.isConnected ? "Connected" : "Disconnected")
                                .font(.headline)
                            if let version = serverManager.serverVersion {
                                Text("AltServer v\(version)")
                                    .font(.caption)
                                    .foregroundColor(.secondary)
                            }
                        }

                        Spacer()

                        if serverManager.isConnected {
                            Button("Refresh All") {
                                showingRefreshAlert = true
                            }
                            .buttonStyle(.bordered)
                            .tint(.blue)
                        }
                    }
                } header: {
                    Text("Server Status")
                }

                // Installed Apps Section
                Section {
                    if appState.installedApps.isEmpty {
                        VStack(spacing: 12) {
                            Image(systemName: "square.and.arrow.down")
                                .font(.largeTitle)
                                .foregroundColor(.secondary)
                            Text("No apps installed")
                                .font(.headline)
                            Text("Install apps from the Sources tab")
                                .font(.caption)
                                .foregroundColor(.secondary)
                        }
                        .frame(maxWidth: .infinity)
                        .padding()
                    } else {
                        ForEach(appState.installedApps) { app in
                            InstalledAppRow(app: app)
                                .onTapGesture {
                                    selectedApp = app
                                }
                        }
                        .onDelete(perform: deleteApps)
                    }
                } header: {
                    Text("Installed Apps (\(appState.installedApps.count))")
                }

                // Expiration Info
                if let nextExpiring = appState.installedApps
                    .filter({ !$0.isExpired })
                    .min(by: { $0.daysRemaining < $1.daysRemaining }) {

                    Section {
                        HStack {
                            Image(systemName: "clock.badge.exclamationmark")
                                .foregroundColor(nextExpiring.statusColor)

                            VStack(alignment: .leading) {
                                Text("Next refresh needed")
                                    .font(.headline)
                                Text("\(nextExpiring.daysRemaining) days remaining for \(nextExpiring.name)")
                                    .font(.caption)
                                    .foregroundColor(.secondary)
                            }
                        }
                    } header: {
                        Text("Signing Status")
                    }
                }
            }
            .navigationTitle("My Apps")
            .refreshable {
                await refreshApps()
            }
            .alert("Refresh All Apps", isPresented: $showingRefreshAlert) {
                Button("Cancel", role: .cancel) {}
                Button("Refresh") {
                    Task {
                        await refreshApps()
                    }
                }
            } message: {
                Text("This will re-sign all installed apps. Make sure your computer is connected.")
            }
            .sheet(item: $selectedApp) { app in
                AppDetailView(app: app)
            }
        }
    }

    private func deleteApps(at offsets: IndexSet) {
        appState.installedApps.remove(atOffsets: offsets)
        appState.saveState()
    }

    private func refreshApps() async {
        appState.isRefreshing = true
        defer { appState.isRefreshing = false }

        await withCheckedContinuation { continuation in
            serverManager.refreshApps { _ in
                appState.lastRefreshDate = Date()
                continuation.resume()
            }
        }
    }
}

// MARK: - Installed App Row

struct InstalledAppRow: View {
    let app: InstalledApp

    var body: some View {
        HStack(spacing: 12) {
            // App Icon
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
                            .font(.title2)
                    }
            }
            .frame(width: 48, height: 48)

            // App Info
            VStack(alignment: .leading, spacing: 4) {
                HStack {
                    Text(app.name)
                        .font(.headline)
                    if app.isExpired {
                        Text("Expired")
                            .font(.caption2)
                            .padding(.horizontal, 6)
                            .padding(.vertical, 2)
                            .background(Color.red)
                            .foregroundColor(.white)
                            .cornerRadius(4)
                    }
                }

                Text(app.bundleIdentifier)
                    .font(.caption)
                    .foregroundColor(.secondary)

                HStack {
                    Text("v\(app.version)")
                        .font(.caption2)
                        .foregroundColor(.secondary)

                    Text("(\(app.buildNumber))")
                        .font(.caption2)
                        .foregroundColor(.secondary)
                }
            }

            Spacer()

            // Days Remaining
            VStack(alignment: .trailing, spacing: 4) {
                if app.isExpired {
                    Text("EXPIRED")
                        .font(.caption)
                        .fontWeight(.bold)
                        .foregroundColor(.red)
                } else {
                    Text("\(app.daysRemaining)d")
                        .font(.headline)
                        .foregroundColor(app.statusColor)
                }

                Circle()
                    .fill(app.statusColor)
                    .frame(width: 8, height: 8)
            }
        }
        .padding(.vertical, 4)
    }
}

// MARK: - App Detail View

struct AppDetailView: View {
    let app: InstalledApp
    @Environment(\.dismiss) private var dismiss
    @EnvironmentObject var appState: AppState
    @EnvironmentObject var serverManager: ServerManager
    @State private var showingDeleteConfirmation = false

    var body: some View {
        NavigationStack {
            List {
                Section {
                    HStack(spacing: 16) {
                        AsyncImage(url: app.iconURL) { image in
                            image
                                .resizable()
                                .scaledToFit()
                        } placeholder: {
                            RoundedRectangle(cornerRadius: 16)
                                .fill(Color.blue.gradient)
                                .overlay {
                                    Image(systemName: "app.fill")
                                        .foregroundColor(.white)
                                        .font(.largeTitle)
                                }
                        }
                        .frame(width: 80, height: 80)

                        VStack(alignment: .leading, spacing: 8) {
                            Text(app.name)
                                .font(.title2)
                                .fontWeight(.bold)

                            Text(app.bundleIdentifier)
                                .font(.subheadline)
                                .foregroundColor(.secondary)

                            Text("Version \(app.version) (\(app.buildNumber))")
                                .font(.caption)
                                .foregroundColor(.secondary)
                        }
                    }
                    .padding(.vertical, 8)
                }

                Section("Signing Status") {
                    LabeledContent("Status", value: app.isExpired ? "Expired" : "Valid")
                    LabeledContent("Expires", value: app.expirationDate.formatted())
                    LabeledContent("Days Remaining", value: "\(app.daysRemaining) days")
                    LabeledContent("Installed", value: app.installDate.formatted())
                }

                Section {
                    Button("Refresh Signing") {
                        Task {
                            await refreshApp()
                        }
                    }
                    .disabled(!serverManager.isConnected)

                    Button("Delete App", role: .destructive) {
                        showingDeleteConfirmation = true
                    }
                }
            }
            .navigationTitle("App Details")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .navigationBarTrailing) {
                    Button("Done") {
                        dismiss()
                    }
                }
            }
            .alert("Delete App", isPresented: $showingDeleteConfirmation) {
                Button("Cancel", role: .cancel) {}
                Button("Delete", role: .destructive) {
                    appState.removeInstalledApp(app)
                    dismiss()
                }
            } message: {
                Text("Are you sure you want to delete \(app.name)?")
            }
        }
    }

    private func refreshApp() async {
        await withCheckedContinuation { continuation in
            serverManager.refreshApps { _ in
                continuation.resume()
            }
        }
    }
}

// MARK: - Preview

#Preview {
    AppsView()
        .environmentObject(AppState())
        .environmentObject(ServerManager())
}
