import XCTest
@testable import AltSign

final class AltSignTests: XCTestCase {

    func testCertificateValidation() {
        let certificate = Certificate(
            id: UUID(),
            commonName: "Apple Development: Test User (ABCDE)",
            teamName: "Test Team",
            teamIdentifier: "ABCDE",
            serialNumber: "123456",
            notBefore: Date().addingTimeInterval(-86400),
            notAfter: Date().addingTimeInterval(86400 * 30),
            certificateData: Data(),
            privateKeyData: Data(),
            machineId: nil
        )

        XCTAssertTrue(certificate.isValid)
        XCTAssertFalse(certificate.isExpired)
        XCTAssertEqual(certificate.type, .appleDevelopment)
    }

    func testExpiredCertificate() {
        let certificate = Certificate(
            id: UUID(),
            commonName: "iPhone Distribution: Test",
            teamName: "Test Team",
            teamIdentifier: "ABCDE",
            serialNumber: "1234567",
            notBefore: Date().addingTimeInterval(-86400 * 400),
            notAfter: Date().addingTimeInterval(-86400 * 30),
            certificateData: Data(),
            privateKeyData: Data(),
            machineId: nil
        )

        XCTAssertTrue(certificate.isExpired)
        XCTAssertFalse(certificate.isValid)
        XCTAssertEqual(certificate.type, .distribution)
    }

    func testDeviceClassParsing() {
        XCTAssertEqual(Device.DeviceClass(rawValue: "iPhone"), .iPhone)
        XCTAssertEqual(Device.DeviceClass(rawValue: "ipad"), .iPad)
        XCTAssertEqual(Device.DeviceClass(rawValue: "AppleWatch"), .appleWatch)
        XCTAssertEqual(Device.DeviceClass(rawValue: "unknown-thing"), .unknown)
    }

    func testHTTPRequestParsing() {
        let requestString = "GET /status HTTP/1.1\r\nHost: 127.0.0.1\r\nContent-Type: application/json\r\n\r\n"
        let data = requestString.data(using: .utf8)!

        guard let request = HTTPRequest.parse(from: data) else {
            XCTFail("Failed to parse HTTP request")
            return
        }

        XCTAssertEqual(request.method, .get)
        XCTAssertEqual(request.path, "/status")
        XCTAssertEqual(request.header("content-type"), "application/json")
    }

    func testHTTPResponseSerialization() {
        let response = HTTPResponse.json(["status": "ok"])
        let data = response.serialize()

        let string = String(data: data, encoding: .utf8)
        XCTAssertTrue(string?.contains("HTTP/1.1 200 OK") ?? false)
        XCTAssertTrue(string?.contains("status") ?? false)
    }
}
