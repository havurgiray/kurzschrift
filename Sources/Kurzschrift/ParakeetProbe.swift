import FluidAudio
import Foundation

/// Developer tool for comparing speech recognizers: transcribes a whole recording with NVIDIA
/// Parakeet (through FluidAudio, on this Mac) and writes the text with token timings as JSON.
///
///     Kurzschrift --parakeet "meeting audio.m4a" out.json [v2|v3] [segments.json]
///
/// With segments.json (a list of [start, end] in seconds), each stretch is recognized on its own,
/// the way the live transcript would use it, instead of the whole recording at once.
/// v2 is English only; v3 covers 25 European languages. The model downloads once into
/// ~/Library/Application Support/FluidAudio/Models.
enum ParakeetProbe {
  static func runIfRequested() {
    let args = CommandLine.arguments
    guard args.count >= 4, args[1] == "--parakeet" else { return }
    Task {
      do {
        let version: AsrModelVersion = args.count >= 5 && args[4] == "v3" ? .v3 : .v2
        let started = Date()
        let models = try await AsrModels.downloadAndLoad(version: version)
        // In memory, one chunk at a time: the default disk-streamed, parallel path silently lost
        // 1 to 3 minute stretches of a 65-minute meeting (Core ML "zero shape" errors).
        let manager = AsrManager(config: ASRConfig(parallelChunkConcurrency: 1, streamingEnabled: false))
        try await manager.loadModels(models)
        var state = TdtDecoderState.make(decoderLayers: await manager.decoderLayerCount)
        let loaded = Date()
        if args.count >= 6 {
          let samples = try AudioConverter().resampleAudioFile(URL(fileURLWithPath: args[2]))
          let ranges = try JSONDecoder().decode([[Double]].self, from: Data(contentsOf: URL(fileURLWithPath: args[5])))
          var out: [[String: Any]] = []
          for range in ranges {
            let from = max(0, Int((range[0] - 0.25) * 16000)), to = min(samples.count, Int((range[1] + 0.25) * 16000))
            guard to - from > 1600 else { continue }
            var fresh = TdtDecoderState.make(decoderLayers: await manager.decoderLayerCount)
            let r = try await manager.transcribe(Array(samples[from..<to]), decoderState: &fresh)
            out.append(["start": range[0], "end": range[1], "text": r.text])
          }
          try JSONSerialization.data(withJSONObject: ["segments": out]).write(to: URL(fileURLWithPath: args[3]))
          FileHandle.standardError.write(Data(String(
            format: "parakeet %@: %d segments in %.1f s\n", args[4], out.count, Date().timeIntervalSince(loaded)).utf8))
          exit(0)
        }
        let result = try await manager.transcribe(URL(fileURLWithPath: args[2]), decoderState: &state)
        let tokens = (result.tokenTimings ?? []).map {
          ["t": $0.token, "s": $0.startTime, "e": $0.endTime, "c": Double($0.confidence)] as [String: Any]
        }
        let json: [String: Any] = ["text": result.text, "tokens": tokens]
        try JSONSerialization.data(withJSONObject: json).write(to: URL(fileURLWithPath: args[3]))
        FileHandle.standardError.write(Data(String(
          format: "parakeet %@: %.0f s audio in %.0f s (models %.0f s)\n", args.count >= 5 ? args[4] : "v2",
          result.duration, Date().timeIntervalSince(loaded), loaded.timeIntervalSince(started)).utf8))
        exit(0)
      } catch {
        FileHandle.standardError.write(Data("parakeet failed: \(error)\n".utf8))
        exit(1)
      }
    }
    dispatchMain()
  }
}
