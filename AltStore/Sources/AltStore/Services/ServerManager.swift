import SwiftUI
import Network
import Combine

/// Manages connection to AltServer on the host computer
class ServerManager: ObservableObject {
    @Published var isConnected = false
    @Published var serverVersion: String?
    @Published var connectionError: String?

    private var connection: NWConnection?
    private let queue = DispatchQueue(label: "com.altstore.server", qos: .userInitiated)
    private let host: NWEndpoint.Host = .init("127.0.0.1")
    private let port: NWEndpoint.Port = .init(rawValue: 27000)!

    // Connection state
    private var retryCount = 0
    private let maxRetries = 3
    private var retryTimer: Timer?

    // MARK: - Connection

    func connectToServer() {
        let params = NWParameters.tcp
        params.allowLocalEndpointReuse = true

        connection = NWConnection(host: host, port: port, using: params)

        connection?.stateUpdateHandler = { [weak self] state in
            DispatchQueue.main.async {
                switch state {
                case .ready:
                    self?.isConnected = true
                    self?.connectionError = nil
                    self?.retryCount = 0
                    self?.fetchServerStatus()
                case .failed(let error):
                    self?.isConnected = false
                    self?.connectionError = error.localizedDescription
                    self?.retryConnection()
                case .cancelled:
                    self?.isConnected = false
                default:
                    break
                }
            }
        }

        connection?.start(queue: queue)
    }

    func disconnect() {
        connection?.cancel()
        connection = nil
        isConnected = false
        retryTimer?.invalidate()
    }

    private func retryConnection() {
        guard retryCount < maxRetries else {
            connectionError = "Could not connect to AltServer. Make sure AltServer is running on your computer."
            return
        }

        retryCount += 1
        retryTimer?.invalidate()
        retryTimer = Timer.scheduledTimer(withTimeInterval: 2.0, repeats: false) { [weak self] _ in
            self?.connectToServer()
        }
    }

    // MARK: - API Communication

    func fetchServerStatus() {
        sendRequest(path: "/status") { [weak self] (result: Result<ServerStatus, Error>) in
            DispatchQueue.main.async {
                switch result {
                case .success(let status):
                    self?.serverVersion = status.version
                case .failure(let error):
                    self?.connectionError = error.localizedDescription
                }
            }
        }
    }

    func fetchInstalledApps(completion: @escaping ([RemoteInstalledApp]) -> Void) {
        sendRequest(path: "/apps") { result in
            DispatchQueue.main.async {
                switch result {
                case .success(let response):
                    completion(response.apps)
                case .failure:
                    completion([])
                }
            }
        }
    }

    func installApp(ipaURL: URL, completion: @escaping (Result<Void, Error>) -> Void) {
        let body: [String: Any] = [
            "ipaPath": ipaURL.path
        ]

        sendRequest(path: "/install", method: .post, body: body) { result in
            DispatchQueue.main.async {
                completion(result.map { _ in () })
            }
        }
    }

    func refreshApps(completion: @escaping (Result<Void, Error>) -> Void) {
        sendRequest(path: "/refresh", method: .post) { result in
            DispatchQueue.main.async {
                completion(result.map { _ in () })
            }
        }
    }

    // MARK: - Private Networking

    private func sendRequest<T: Decodable>(path: String, method: HTTPMethod = .get, body: [String: Any]? = nil, completion: @escaping (Result<T, Error>) -> Void) {
        guard let connection = connection else {
            completion(.failure(ServerError.notConnected))
            return
        }

        var request = HTTPRequestData(method: method, path: path, body: body)
        let data = request.serialize()

        connection.send(content: data, completion: .contentProcessed { error in
            if let error = error {
                completion(.failure(error))
                return
            }

            connection.receive(minimumIncompleteLength: 1, maximumLength: 65536) { data, _, _, error in
                if let error = error {
                    completion(.failure(error))
                    return
                }

                guard let data = data, let httpResponse = HTTPResponseData.parse(from: data) else {
                    completion(.failure(ServerError.invalidResponse))
                    return
                }

                guard httpResponse.statusCode == 200,
                      let body = httpResponse.body else {
                    completion(.failure(ServerError.requestFailed(httpResponse.statusCode)))
                    return
                }

                do {
                    let decoded = try JSONDecoder().decode(T.self, from: body)
                    completion(.success(decoded))
                } catch {
                    completion(.failure(error))
                }
            }
        })
    }
}

// MARK: - HTTP Method

enum HTTPMethod: String {
    case get = "GET"
    case post = "POST"
}

// MARK: - Request/Response Data

struct HTTPRequestData {
    let method: HTTPMethod
    let path: String
    let headers: [String: String]
    let body: [String: Any]?

    init(method: HTTPMethod = .get, path: String, headers: [String: String] = [:], body: [String: Any]? = nil) {
        self.method = method
        self.path = path
        self.headers = headers
        self.body = body
    }

    func serialize() -> Data {
        var request = "\(method.rawValue) \(path) HTTP/1.1\r\n"
        request += "Host: 127.0.0.1:27000\r\n"
        request += "Connection: keep-alive\r\n"

        if let body = body, let bodyData = try? JSONSerialization.data(withJSONObject: body) {
            request += "Content-Type: application/json\r\n"
            request += "Content-Length: \(bodyData.count)\r\n"
            request += "\r\n"

            var data = request.data(using: .utf8) ?? Data()
            data.append(bodyData)
            return data
        }

        request += "\r\n"
        return request.data(using: .utf8) ?? Data()
    }
}

struct HTTPResponseData {
    let statusCode: Int
    let headers: [String: String]
    let body: Data?

    static func parse(from data: Data) -> HTTPResponseData? {
        guard let rawString = String(data: data, encoding: .utf8) else { return nil }

        let lines = rawString.components(separatedBy: "\r\n")
        guard !lines.isEmpty else { return nil }

        // Parse status code
        let statusParts = lines[0].split(separator: " ")
        guard statusParts.count >= 2,
              let statusCode = Int(statusParts[1]) else {
            return nil
        }

        // Parse headers and body
        var headers: [String: String] = [:]
        var bodyStartIndex = lines.count

        for lineIndex in 1..<lines.count {
            if lines[lineIndex].isEmpty {
                bodyStartIndex = lineIndex + 1
                break
            }
            let headerParts = lines[lineIndex].split(separator: ":", maxSplits: 1)
            if headerParts.count == 2 {
                headers[String(headerParts[0]).lowercased()] = String(headerParts[1]).trimmingCharacters(in: .whitespaces)
            }
        }

        let bodyLines = Array(lines[bodyStartIndex...])
        let bodyString = bodyLines.joined(separator: "\r\n")
        let body = bodyString.isEmpty ? nil : bodyString.data(using: .utf8)

        return HTTPResponseData(statusCode: statusCode, headers: headers, body: body)
    }
}

// MARK: - Models

struct ServerStatus: Codable {
    let version: String
    let status: String
    let devices: Int
}

struct RemoteInstalledApp: Codable {
    let bundleIdentifier: String
    let name: String
    let version: String
    let installDate: String?
}

struct AppsResponse: Codable {
    let apps: [RemoteInstalledApp]
}

// MARK: - Errors

enum ServerError: LocalizedError {
    case notConnected
    case invalidResponse
    case requestFailed(Int)

    var errorDescription: String? {
        switch self {
        case .notConnected:
            return "Not connected to AltServer"
        case .invalidResponse:
            return "Invalid response from AltServer"
        case .requestFailed(let code):
            return "Request failed with status code \(code)"
        }
    }
}
