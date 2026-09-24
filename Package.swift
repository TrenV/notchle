// swift-tools-version:6.0
import PackageDescription

// NotchleCore is the portable heart of the game: Foundation-only, no AppKit/SwiftUI/
// Combine/AVFoundation, so it can be compiled for Windows later (see ARCHITECTURE.md).
// NotchleMac holds every macOS-specific adapter (Spotify via AppleScript, AVPlayer, notch UI).
// Notchle is the thin app entry point that wires the two together.
let package = Package(
    name: "Notchle",
    platforms: [.macOS(.v14)],
    products: [
        .library(name: "NotchleCore", targets: ["NotchleCore"]),
        .executable(name: "Notchle", targets: ["Notchle"]),
    ],
    targets: [
        .target(name: "NotchleCore"),
        .target(name: "NotchleMac", dependencies: ["NotchleCore"]),
        .executableTarget(name: "Notchle", dependencies: ["NotchleCore", "NotchleMac"]),
        .testTarget(
            name: "NotchleCoreTests",
            dependencies: ["NotchleCore"],
            resources: [.copy("Fixtures")]
        ),
        .testTarget(name: "NotchleMacTests", dependencies: ["NotchleMac", "NotchleCore"]),
    ]
)
