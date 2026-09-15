import Foundation

/// Simple HTTP request parser for local server communication
public struct HTTPRequest: Sendable {
    public let method: HTTPMethod
    public let path: String
    public let headers: [String: String]
    public let body: Data?

    public enum HTTPMethod: String, Sendable {
        case get = "GET"
        case post = "POST"
        case put = "PUT"
        case delete = "DELETE"
    }

    public func header(_ name: String) -> String? {
        headers[name.lowercased()]
    }

    public static func parse(from data: Data) -> HTTPRequest? {
        guard let rawString = String(data: data, encoding: .utf8) else { return nil }

        let lines = rawString.components(separatedBy: "\r\n")
        guard !lines.isEmpty else { return nil }

        // Parse request line
        let requestParts = lines[0].split(separator: " ")
        guard requestParts.count >= 2 else { return nil }

        let method = HTTPMethod(rawValue: String(requestParts[0])) ?? .get
        let path = String(requestParts[1])

        // Parse headers
        var headers: [String: String] = [:]
        var bodyStartIndex = lines.count

        for i in 1..<lines.count {
            if lines[i].isEmpty {
                bodyStartIndex = i + 1
                break
            }
            let headerParts = lines[i].split(separator: ":", maxSplits: 1)
            if headerParts.count == 2 {
                headers[String(headerParts[0]).lowercased()] = String(headerParts[1]).trimmingCharacters(in: .whitespaces)
            }
        }

        // Parse body
        let bodyLines = Array(lines[bodyStartIndex...])
        let bodyString = bodyLines.joined(separator: "\r\n")
        let body = bodyString.isEmpty ? nil : bodyString.data(using: .utf8)

        return HTTPRequest(method: method, path: path, headers: headers, body: body)
    }
}

/// Simple HTTP response for local server communication
public struct HTTPResponse: Sendable {
    public let statusCode: Int
    public let headers: [String: String]
    public let body: Data?

    public init(statusCode: Int, headers: [String: String] = [:], body: Data? = nil) {
        self.statusCode = statusCode
        self.headers = headers
        self.body = body
    }

    public func serialize() -> Data {
        var response = "HTTP/1.1 \(statusCode) \(statusText)\r\n"

        var allHeaders = headers
        allHeaders["content-type"] = headers["content-type"] ?? "application/json"
        if let body = body {
            allHeaders["content-length"] = "\(body.count)"
        }

        for (key, value) in allHeaders {
            response += "\(key): \(value)\r\n"
        }
        response += "\r\n"

        var data = response.data(using: .utf8) ?? Data()
        if let body = body {
            data.append(body)
        }

        return data
    }

    private var statusText: String {
        switch statusCode {
        case 200: return "OK"
        case 201: return "Created"
        case 204: return "No Content"
        case 400: return "Bad Request"
        case 401: return "Unauthorized"
        case 404: return "Not Found"
        case 500: return "Internal Server Error"
        default: return "Unknown"
        }
    }
}

// MARK: - Protocol

/// Delegate protocol for handling local server requests
public protocol LocalServerDelegate: AnyObject {
    func handleRequest(_ request: HTTPRequest) -> HTTPResponse
}

// MARK: - Convenience Initializers

extension HTTPResponse {
    public static func json(_ object: Any, statusCode: Int = 200) -> HTTPResponse {
        let data = try? JSONSerialization.data(withJSONObject: object)
        return HTTPResponse(
            statusCode: statusCode,
            headers: ["content-type": "application/json"],
            body: data
        )
    }

    public static func ok(_ message: String = "OK") -> HTTPResponse {
        HTTPResponse(statusCode: 200, body: message.data(using: .utf8))
    }

    public static func error(_ message: String, statusCode: Int = 400) -> HTTPResponse {
        let body = ["error": message]
        let data = try? JSONSerialization.data(withJSONObject: body)
        return HTTPResponse(
            statusCode: statusCode,
            headers: ["content-type": "application/json"],
            body: data
        )
    }
}
