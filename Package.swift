// swift-tools-version: 6.2

import PackageDescription

let package = Package(
  name: "Kurzschrift",
  platforms: [.macOS("26.0")],
  dependencies: [
    // Core Audio process tap (system audio capture).
    .package(path: "Vendor/audiotee"),
    // On-device speaker diarization. `traits: []` drops the NeMo text-normalization binary,
    // which diarization doesn't use (and which would be downloaded from GitHub at build time).
    .package(path: "Vendor/FluidAudio", traits: []),
  ],
  targets: [
    .executableTarget(
      name: "Kurzschrift",
      dependencies: [
        .product(name: "AudioTeeCore", package: "audiotee"),
        .product(name: "FluidAudio", package: "FluidAudio"),
      ],
      swiftSettings: [.swiftLanguageMode(.v5)]
    )
  ]
)
