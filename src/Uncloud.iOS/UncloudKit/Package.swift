// swift-tools-version: 6.0
import PackageDescription

// Everything the iPhone app does to talk to an Uncloud host, with none of UIKit, so the extensions
// that come later can use it as it is and its tests run with `swift test`, on Linux as well.
let package = Package(
    name: "UncloudKit",
    platforms: [.iOS(.v18), .macOS(.v15)],
    products: [
        .library(name: "UncloudKit", targets: ["UncloudKit"])
    ],
    targets: [
        .target(name: "UncloudKit"),
        .testTarget(name: "UncloudKitTests", dependencies: ["UncloudKit"]),
    ]
)
