import Foundation

/// Local server that runs on the iOS device to communicate with AltServer
public class LocalServer: @unchecked Sendable {
    private let port: UInt16
    private var serverSocket: Int32 = -1
    private var isRunning = false
    private let delegate: LocalServerDelegate

    public weak var delegateHandler: LocalServerDelegate?

    public init(port: UInt16 = 27000, delegate: LocalServerDelegate) {
        self.port = port
        self.delegate = delegate
    }

    deinit {
        stop()
    }

    // MARK: - Server Lifecycle

    public func start() throws {
        guard !isRunning else { return }

        serverSocket = socket(AF_INET, SOCK_STREAM, 0)
        guard serverSocket >= 0 else {
            throw ServerError.socketCreationFailed
        }

        var reuse: Int32 = 1
        setsockopt(serverSocket, SOL_SOCKET, SO_REUSEADDR, &reuse, socklen_t(MemoryLayout<Int32>.size))

        var addr = sockaddr_in()
        addr.sin_family = sa_family_t(AF_INET)
        addr.sin_port = port.bigEndian
        addr.sin_addr.s_addr = INADDR_ANY

        let bindResult = withUnsafePointer(to: &addr) { ptr in
            ptr.withMemoryRebound(to: sockaddr.self, capacity: 1) { sockPtr in
                bind(serverSocket, sockPtr, socklen_t(MemoryLayout<sockaddr_in>.size))
            }
        }

        guard bindResult == 0 else {
            close(serverSocket)
            throw ServerError.bindFailed
        }

        guard listen(serverSocket, 5) == 0 else {
            close(serverSocket)
            throw ServerError.listenFailed
        }

        isRunning = true

        DispatchQueue.global(qos: .userInitiated).async { [weak self] in
            self?.acceptConnections()
        }
    }

    public func stop() {
        isRunning = false
        if serverSocket >= 0 {
            close(serverSocket)
            serverSocket = -1
        }
    }

    // MARK: - Connection Handling

    private func acceptConnections() {
        while isRunning {
            var clientAddr = sockaddr_in()
            var clientAddrLen = socklen_t(MemoryLayout<sockaddr_in>.size)
            let clientSocket = withUnsafeMutablePointer(to: &clientAddr) { ptr in
                ptr.withMemoryRebound(to: sockaddr.self, capacity: 1) { sockPtr in
                    accept(serverSocket, sockPtr, &clientAddrLen)
                }
            }

            guard clientSocket >= 0 else { continue }

            DispatchQueue.global(qos: .userInitiated).async { [weak self] in
                self?.handleClient(clientSocket)
            }
        }
    }

    private func handleClient(_ socket: Int32) {
        defer { close(socket) }

        var buffer = [UInt8](repeating: 0, count: 4096)
        let bytesRead = read(socket, &buffer, buffer.count)

        guard bytesRead > 0 else { return }

        let requestData = Data(bytes: buffer, count: bytesRead)

        guard let request = HTTPRequest.parse(from: requestData) else { return }

        let response = delegate.handleRequest(request)

        let responseData = response.serialize()
        responseData.withUnsafeBytes { ptr in
            _ = write(socket, ptr.baseAddress!, responseData.count)
        }
    }
}

// MARK: - Server Errors

public enum ServerError: LocalizedError {
    case socketCreationFailed
    case bindFailed
    case listenFailed
    case connectionFailed

    public var errorDescription: String? {
        switch self {
        case .socketCreationFailed:
            return "Failed to create socket"
        case .bindFailed:
            return "Failed to bind to port"
        case .listenFailed:
            return "Failed to start listening"
        case .connectionFailed:
            return "Failed to connect"
        }
    }
}
