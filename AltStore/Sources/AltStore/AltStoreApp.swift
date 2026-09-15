import SwiftUI

@main
struct AltStoreApp: App {
    @StateObject private var appState = AppState()
    @StateObject private var serverManager = ServerManager()

    var body: some Scene {
        WindowGroup {
            ContentView()
                .environmentObject(appState)
                .environmentObject(serverManager)
                .onAppear {
                    serverManager.connectToServer()
                }
        }
    }
}
