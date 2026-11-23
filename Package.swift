// swift-tools-version:5.9
import PackageDescription

let package = Package(
    name: "TricksterQuestSender",
    platforms: [.macOS(.v14)],
    targets: [
        .executableTarget(name: "TricksterQuestSender", path: "Sources/TricksterQuestSender")
    ]
)
