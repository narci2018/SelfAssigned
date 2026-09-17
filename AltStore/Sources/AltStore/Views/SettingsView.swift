import SwiftUI

struct SettingsView: View {
    @EnvironmentObject var appState: AppState
    @EnvironmentObject var serverManager: ServerManager
    @AppStorage("autoRefresh") private var autoRefresh = true
    @AppStorage("refreshInterval") private var refreshInterval = 6
    @AppStorage("notificationsEnabled") private var notificationsEnabled = true
    @State private var showingAbout = false
    @State private var showingExportLogs = false
    @State private var certificates: [CertificateInfo] = []

    var body: some View {
        NavigationStack {
            List {
                // Server Section
                Section("Server") {
                    LabeledContent("Status", value: serverManager.isConnected ? "Connected" : "Disconnected")
                    if let version = serverManager.serverVersion {
                        LabeledContent("Version", value: "v\(version)")
                    }

                    Button("Reconnect") {
                        serverManager.connectToServer()
                    }
                    .disabled(serverManager.isConnected)
                }

                // Auto Refresh Section
                Section("Auto Refresh") {
                    Toggle("Enable Auto Refresh", isOn: $autoRefresh)

                    if autoRefresh {
                        Picker("Refresh Interval", selection: $refreshInterval) {
                            Text("3 hours").tag(3)
                            Text("6 hours").tag(6)
                            Text("12 hours").tag(12)
                            Text("24 hours").tag(24)
                        }
                    }

                    if let lastRefresh = appState.lastRefreshDate {
                        LabeledContent("Last Refresh", value: lastRefresh.formatted(.relative(presentation: .named)))
                    }
                }

                // Notifications Section
                Section("Notifications") {
                    Toggle("Expiration Reminders", isOn: $notificationsEnabled)

                    if notificationsEnabled {
                        LabeledContent("Warning", value: "7 days before expiry")
                    }
                }

                // Certificates Section
                Section("Certificates") {
                    if certificates.isEmpty {
                        Text("No certificates found")
                            .foregroundColor(.secondary)
                    } else {
                        ForEach(certificates) { cert in
                            CertificateRow(certificate: cert)
                        }
                    }
                }

                // Data Management Section
                Section("Data") {
                    Button("Export Logs") {
                        showingExportLogs = true
                    }

                    Button("Clear Cache") {
                        clearCache()
                    }

                    Button("Reset All Data", role: .destructive) {
                        resetData()
                    }
                }

                // About Section
                Section("About") {
                    LabeledContent("AltStore", value: "v\(altStoreVersion)")
                    LabeledContent("Build", value: altStoreBuild)

                    Button("About AltStore") {
                        showingAbout = true
                    }

                    Link("GitHub Repository", destination: URL(string: "https://github.com/your-org/altstore")!)
                    Link("Documentation", destination: URL(string: "https://altstore.io")!)
                }
            }
            .navigationTitle("Settings")
            .sheet(isPresented: $showingAbout) {
                AboutView()
            }
        }
    }

    // MARK: - Actions

    private func clearCache() {
        let cacheDir = FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask)[0]
        try? FileManager.default.removeItem(at: cacheDir.appendingPathComponent("AltStore_Downloads"))
    }

    private func resetData() {
        let storage = AppStateStorage()
        storage.saveInstalledApps([])
        storage.saveAvailableApps([])
        storage.saveSources([])
        appState.installedApps = []
        appState.availableApps = []
    }
}

// MARK: - Certificate Row

struct CertificateRow: View {
    let certificate: CertificateInfo

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            HStack {
                Text(certificate.commonName)
                    .font(.headline)
                Spacer()
                Image(systemName: certificate.isValid ? "checkmark.circle.fill" : "xmark.circle.fill")
                    .foregroundColor(certificate.isValid ? .green : .red)
            }

            Text("Team: \(certificate.teamName)")
                .font(.caption)
                .foregroundColor(.secondary)

            HStack {
                Text("Expires: \(certificate.expirationDate.formatted(.dateTime.month().day().year()))")
                    .font(.caption)
                    .foregroundColor(.secondary)

                Spacer()

                if certificate.isValid {
                    Text("\(certificate.daysRemaining) days left")
                        .font(.caption)
                        .foregroundColor(.green)
                } else {
                    Text("Expired")
                        .font(.caption)
                        .foregroundColor(.red)
                }
            }
        }
    }
}

// MARK: - Certificate Info

struct CertificateInfo: Identifiable {
    let id = UUID()
    let commonName: String
    let teamName: String
    let expirationDate: Date
    let isValid: Bool

    var daysRemaining: Int {
        let calendar = Calendar.current
        return calendar.dateComponents([.day], from: Date(), to: expirationDate).day ?? 0
    }
}

// MARK: - About View

struct AboutView: View {
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        NavigationStack {
            VStack(spacing: 24) {
                Image(systemName: "app.fill")
                    .font(.system(size: 80))
                    .foregroundColor(.blue)

                Text("AltStore")
                    .font(.largeTitle)
                    .fontWeight(.bold)

                Text("Version \(altStoreVersion)")
                    .font(.subheadline)
                    .foregroundColor(.secondary)

                Text("AltStore is an alternative app store that lets you install apps on your iOS device without jailbreaking.")
                    .font(.body)
                    .multilineTextAlignment(.center)
                    .padding(.horizontal)

                VStack(alignment: .leading, spacing: 8) {
                    Text("Features:")
                        .font(.headline)

                    FeatureRow(icon: "shield.checkered", text: "Secure sideloading")
                    FeatureRow(icon: "arrow.clockwise", text: "Automatic app refreshing")
                    FeatureRow(icon: "globe", text: "Third-party source support")
                    FeatureRow(icon: "lock.shield", text: "No jailbreak required")
                }
                .padding()
                .background(Color(.systemGray6))
                .cornerRadius(12)
                .padding(.horizontal)

                Spacer()
            }
            .padding(.top, 40)
            .navigationTitle("About")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .navigationBarTrailing) {
                    Button("Done") {
                        dismiss()
                    }
                }
            }
        }
    }
}

// MARK: - Feature Row

struct FeatureRow: View {
    let icon: String
    let text: String

    var body: some View {
        HStack(spacing: 12) {
            Image(systemName: icon)
                .foregroundColor(.blue)
                .frame(width: 24)
            Text(text)
        }
    }
}

// MARK: - Constants

private let altStoreVersion = "1.0.39"
private let altStoreBuild = "8"

// MARK: - Preview

#Preview {
    SettingsView()
        .environmentObject(AppState())
        .environmentObject(ServerManager())
}
