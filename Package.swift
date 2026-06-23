// swift-tools-version: 6.0

import PackageDescription

let package = Package(
    name: "LANWebTerminalManager",
    platforms: [
        .macOS(.v13)
    ],
    products: [
        .executable(name: "LANWebTerminalManager", targets: ["LANWebTerminalManager"])
    ],
    targets: [
        .executableTarget(
            name: "LANWebTerminalManager",
            path: "Sources/LANWebTerminalManager"
        )
    ]
)
