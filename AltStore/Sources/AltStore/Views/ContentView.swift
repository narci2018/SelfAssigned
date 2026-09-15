import SwiftUI

struct ContentView: View {
    @EnvironmentObject var appState: AppState
    @EnvironmentObject var serverManager: ServerManager

    var body: some View {
        TabView(selection: $appState.currentTab) {
            AppsView()
                .tabItem {
                    Label("Apps", systemImage: "square.grid.2x2")
                }
                .tag(AppState.Tab.apps)

            SourcesView()
                .tabItem {
                    Label("Sources", systemImage: "globe")
                }
                .tag(AppState.Tab.sources)

            SettingsView()
                .tabItem {
                    Label("Settings", systemImage: "gear")
                }
                .tag(AppState.Tab.settings)
        }
        .overlay {
            if !serverManager.isConnected {
                ConnectionBanner()
            }
        }
    }
}

// MARK: - Connection Banner

struct ConnectionBanner: View {
    @EnvironmentObject var serverManager: ServerManager

    var body: some View {
        VStack {
            Spacer()

            HStack {
                Image(systemName: "wifi.slash")
                    .foregroundColor(.white)

                Text("Not connected to AltServer")
                    .font(.subheadline)
                    .foregroundColor(.white)

                Spacer()

                Button("Retry") {
                    serverManager.connectToServer()
                }
                .font(.subheadline)
                .foregroundColor(.white)
                .padding(.horizontal, 12)
                .padding(.vertical, 6)
                .background(Color.white.opacity(0.2))
                .cornerRadius(8)
            }
            .padding()
            .background(Color.red)
            .cornerRadius(12)
            .padding()
        }
    }
}

// MARK: - Preview

#Preview {
    ContentView()
        .environmentObject(AppState())
        .environmentObject(ServerManager())
}
