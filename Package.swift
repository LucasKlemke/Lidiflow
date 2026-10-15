// swift-tools-version:5.10
import PackageDescription

let package = Package(
    name: "Lidiflow",
    platforms: [.macOS(.v14)],
    targets: [
        .executableTarget(
            name: "Lidiflow",
            dependencies: ["CTranscribe"],
            path: "Sources/Lidiflow",
            linkerSettings: [
                // build.sh copies CTranscribe.framework into Contents/Frameworks.
                .unsafeFlags(["-Xlinker", "-rpath", "-Xlinker", "@executable_path/../Frameworks"]),
            ]
        ),
        // transcribe.cpp (ggml, Metal): runs the GGUF models such as Nemotron and Parakeet.
        .binaryTarget(
            name: "CTranscribe",
            url: "https://github.com/handy-computer/transcribe.cpp/releases/download/v0.4.0/TranscribeCpp.xcframework.zip",
            checksum: "3da069d288b79475adb257e4425c7c00f7026a90d0ef06af2214b7355dcd0453"
        ),
    ]
)
