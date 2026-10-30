import FluidAudio
import Foundation

/// Who spoke when, in seconds on the recording's timeline.
struct SpeakerSpan: Codable {
  let speaker: String
  let start: Double
  let end: Double
}

/// Speaker diarization runs in a child process (this same executable with `--diarize`), so a
/// Core ML crash can never take the app, and the transcript it is about to save, down with it.
enum Diarization {
  static let diarizeFlag = "--diarize"
  static let prepareFlag = "--prepare-diarization"

  // MARK: Parent side

  /// Downloads the speaker models ahead of time so the end of a meeting doesn't wait on the network.
  static func prepareModelsInBackground() {
    Task.detached(priority: .utility) {
      _ = try? await runHelper([prepareFlag])
    }
  }

  struct Result {
    let spans: [SpeakerSpan]
    /// Voice embedding per speaker id, when the diarizer provided them.
    let voices: [String: [Float]]
  }

  static func speakers(in wav: URL) async throws -> Result {
    let output = wav.deletingPathExtension().appendingPathExtension("speakers.json")
    let voicesFile = voicesURL(for: output)
    try await runHelper([diarizeFlag, wav.path, output.path])
    defer {
      try? FileManager.default.removeItem(at: output)
      try? FileManager.default.removeItem(at: voicesFile)
    }
    let raw = try JSONDecoder().decode([SpeakerSpan].self, from: Data(contentsOf: output))
    let spans = foldingSnippetSpeakers(mergingEdgeFragments(raw))
    let kept = Set(spans.map(\.speaker))
    let voices = ((try? JSONDecoder().decode([String: [Float]].self, from: Data(contentsOf: voicesFile))) ?? [:])
      .filter { kept.contains($0.key) }
    return Result(spans: spans, voices: voices)
  }

  /// A "speaker" who only ever speaks in snippets ("yeah", "mm", a laugh) and has a small share of
  /// the talking is almost always someone else's reactions: a second or two of audio makes a poor
  /// voice fingerprint, so the diarizer splits them off. In a 65-minute call with two remote people
  /// that made four extra speakers. Their snippets go to the nearest real speaker in time.
  /// Real participants talk for more than 8 s at least once: the quietest of the four people in
  /// AMI ES2004a has 10% of the talking and turns of up to 18 s, and is kept.
  static func foldingSnippetSpeakers(_ spans: [SpeakerSpan]) -> [SpeakerSpan] {
    var total: [String: Double] = [:]
    var longest: [String: Double] = [:]
    for span in spans {
      total[span.speaker, default: 0] += span.end - span.start
      longest[span.speaker] = max(longest[span.speaker] ?? 0, span.end - span.start)
    }
    let speech = total.values.reduce(0, +)
    let snippets = Set(total.keys.filter { longest[$0]! < 8 && total[$0]! < 0.1 * speech })
    guard !snippets.isEmpty, snippets.count < total.count else { return spans }
    let real = spans.filter { !snippets.contains($0.speaker) }
    return spans.map { span in
      guard snippets.contains(span.speaker) else { return span }
      func gap(_ other: SpeakerSpan) -> Double { max(0, span.start - other.end, other.start - span.end) }
      let nearest = real.min { gap($0) < gap($1) }!
      return SpeakerSpan(speaker: nearest.speaker, start: span.start, end: span.end)
    }
  }

  /// With loudspeakers the microphone hears the people in the call too, so diarizing it finds them
  /// a second time (two of three microphone "speakers" in one meeting, at 0.89 and 0.93 similarity
  /// to their meeting-audio selves). A microphone speaker whose voice matches someone in the
  /// meeting audio, by the same measure as remembered voices, is that person.
  /// Keys are "source:speakerId"; returns microphone label → meeting-audio label.
  @MainActor
  static func sameVoices(_ voices: [String: [Float]]) -> [String: String] {
    func unit(_ v: [Float]) -> [Float] {
      let norm = sqrt(v.reduce(0) { $0 + $1 * $1 })
      return norm > 0 ? v.map { $0 / norm } : v
    }
    let prefix = AudioSourceKind.system.rawValue + ":"
    let meeting = voices.filter { $0.key.hasPrefix(prefix) }.mapValues(unit)
    var result: [String: String] = [:]
    for (label, voice) in voices where label.hasPrefix(AudioSourceKind.microphone.rawValue + ":") {
      let v = unit(voice)
      let best = meeting.map { (label: $0.key, score: zip(v, $0.value).reduce(0) { $0 + $1.0 * $1.1 }) }
        .max { $0.score < $1.score }
      if let best, best.score >= VoiceLibrary.threshold { result[label] = best.label }
    }
    return result
  }

  static func voicesURL(for spansFile: URL) -> URL {
    spansFile.deletingPathExtension().appendingPathExtension("voices.json")
  }

  /// The segmentation model works in fixed 10 s windows, and speech that starts or ends right on a
  /// window edge can come back as an extra "speaker" made of slivers glued to real turns. A speaker
  /// with under 4 s in total whose every span touches another speaker's span is merged into that
  /// neighbour. Short interjections separated by any pause are kept.
  static func mergingEdgeFragments(_ spans: [SpeakerSpan]) -> [SpeakerSpan] {
    let sorted = spans.sorted { $0.start < $1.start }
    let totals = sorted.reduce(into: [String: Double]()) { $0[$1.speaker, default: 0] += $1.end - $1.start }
    guard totals.count > 1 else { return sorted }
    func touches(_ a: SpeakerSpan, _ b: SpeakerSpan) -> Bool { abs(a.end - b.start) <= 0.05 || abs(b.end - a.start) <= 0.05 }

    var result = sorted
    for (speaker, total) in totals where total < 4 {
      let indices = result.indices.filter { result[$0].speaker == speaker }
      let neighbours = indices.map { i in
        result.indices
          .filter { result[$0].speaker != speaker && touches(result[i], result[$0]) }
          .max { (result[$0].end - result[$0].start) < (result[$1].end - result[$1].start) }
      }
      guard neighbours.allSatisfy({ $0 != nil }) else { continue }
      for (i, n) in zip(indices, neighbours) {
        result[i] = SpeakerSpan(speaker: result[n!].speaker, start: result[i].start, end: result[i].end)
      }
    }
    return result
  }

  private static func runHelper(_ arguments: [String]) async throws {
    guard let executable = Bundle.main.executableURL else { throw DiarizationError.noExecutable }
    let process = Process()
    process.executableURL = executable
    process.arguments = arguments
    process.standardOutput = FileHandle.nullDevice
    // A log file rather than a pipe: a chatty child could fill a pipe and block forever.
    let logURL = FileManager.default.temporaryDirectory.appendingPathComponent("Kurzschrift-diarization-\(UUID().uuidString).log")
    FileManager.default.createFile(atPath: logURL.path, contents: nil)
    let log = try FileHandle(forWritingTo: logURL)
    process.standardError = log
    defer {
      try? log.close()
      try? FileManager.default.removeItem(at: logURL)
    }
    try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
      process.terminationHandler = { p in
        if p.terminationStatus == 0 {
          continuation.resume()
        } else {
          let text = (try? String(contentsOf: logURL, encoding: .utf8)) ?? ""
          continuation.resume(throwing: DiarizationError.helperFailed(p.terminationStatus, String(text.suffix(500))))
        }
      }
      do { try process.run() } catch { continuation.resume(throwing: error) }
    }
  }

  // MARK: Child side

  static func runHelperIfRequested() {
    let args = CommandLine.arguments
    guard args.count >= 2, args[1] == diarizeFlag || args[1] == prepareFlag else { return }
    Task {
      do {
        let manager = OfflineDiarizerManager(config: .default)
        try await manager.prepareModels()
        if args[1] == diarizeFlag, args.count >= 4 {
          let result = try await manager.process(URL(fileURLWithPath: args[2]))
          let spans = result.segments.map {
            SpeakerSpan(speaker: $0.speakerId, start: Double($0.startTimeSeconds), end: Double($0.endTimeSeconds))
          }
          try JSONEncoder().encode(spans).write(to: URL(fileURLWithPath: args[3]))
          // One voice embedding per speaker, for recognizing people again in later meetings.
          if let voices = result.speakerDatabase {
            try JSONEncoder().encode(voices).write(to: Self.voicesURL(for: URL(fileURLWithPath: args[3])))
          }
        }
        exit(0)
      } catch {
        FileHandle.standardError.write(Data("diarization failed: \(error)\n".utf8))
        exit(1)
      }
    }
    dispatchMain()
  }
}

enum DiarizationError: LocalizedError {
  case noExecutable
  case helperFailed(Int32, String)

  var errorDescription: String? {
    switch self {
    case .noExecutable: "Couldn't locate the app executable."
    case .helperFailed(let status, let log): "Speaker detection failed (exit \(status)). \(log)"
    }
  }
}
