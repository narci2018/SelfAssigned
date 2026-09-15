// swift-tools-version:5.9

import PackageDescription

let package = Package(
    name: "AltStoreSuite",
    platforms: [
        .macOS(.v13),
        .iOS(.v16)
    ],
    products: [
        .library(name: "AltSign", targets: ["AltSign"]),
        .library(name: "AltServerCore", targets: ["AltServerCore"]),
        .executable(name: "AltServer", targets: ["AltServerCLI"]),
    ],
    dependencies: [],
    targets: [
        .target(
            name: "AltSign",
            dependencies: [],
            path: "AltSign/Sources/AltSign"
        ),
        .target(
            name: "AltServerCore",
            dependencies: ["AltSign"],
            path: "AltServer/Sources/AltServer"
        ),
        .executableTarget(
            name: "AltServerCLI",
            dependencies: ["AltServerCore"],
            path: "AltServer/Sources/AltServerCLI"
        ),
        .testTarget(
            name: "AltSignTests",
            dependencies: ["AltSign"],
            path: "AltSign/Tests/AltSignTests"
        ),
    ]
)
