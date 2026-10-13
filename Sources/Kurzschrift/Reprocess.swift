import AVFoundation
import Foundation

/// Rebuilds a transcript from kept recordings, without the menu bar app or any permissions:
///
///     Kurzschrift --reprocess --mode transcribe|translate --system "meeting audio.wav" \
///         [--mic "microphone.wav"] [--out folder] [--verbose]
///
/// Uses the current settings (language, speakers, layout, your name, and minutes if they're on for the mode).
enum Reprocess {
  static let flag = "--reprocess"

  @MainActor
  static func runIfRequested() {
    let args = CommandLine.arguments
    guard args.count >= 2, args[1] == flag else { return }
    func value(_ name: String) -> String? {
      args.firstIndex(of: name).flatMap { $0 + 1 < args.count ? args[$0 + 1] : nil }
    }
    guard let mode = value("--mode").flatMap(MeetingMode.init(rawValue:)), let systemPath = value("--system") else {
      FileHandle.standardError.write(
        Data("usage: Kurzschrift --reprocess --mode transcribe|translate --system a.wav [--mic b.wav] [--out folder]\n".utf8))
      exit(2)
    }
    var files: [(AudioSourceKind, URL)] = [(.system, URL(fileURLWithPath: systemPath))]
    if let mic = value("--mic") { files.append((.microphone, URL(fileURLWithPath: mic))) }

    var config = AppSettings.shared.snapshot(for: mode)
    config.includeMic = files.count > 1
    config.keepAudio = false
    if let out = value("--out") { config.saveFolder = URL(fileURLWithPath: out) }

    Task { @MainActor in
      do {
        let url = try await run(files: files, config: config)
        print(url.path)
        exit(0)
      } catch {
        FileHandle.standardError.write(Data("reprocess failed: \(error)\n".utf8))
        exit(1)
      }
    }
    dispatchMain()
  }

  @MainActor
  private static func run(files: [(AudioSourceKind, URL)], config: SessionConfig) async throws -> URL {
    let locale = try await SpeechPipeline.prepareModel(for: config.spokenLocale)
    let collector = SegmentCollector()
    for (source, url) in files {
      let pipeline = try await SpeechPipeline.make(source: source, locale: locale)
      pipeline.start { event in
        if case .final(let segment) = event { await collector.add(segment) }
      }
      let file = try AVAudioFile(forReading: url, commonFormat: .pcmFormatInt16, interleaved: true)
      while file.framePosition < file.length {
        let frames = AVAudioFrameCount(min(16_000, file.length - file.framePosition))
        guard let buffer = AVAudioPCMBuffer(pcmFormat: file.processingFormat, frameCapacity: frames) else { break }
        try file.read(into: buffer, frameCount: frames)
        pipeline.append(buffer)
      }
      await pipeline.finish()
    }
    let segments = await collector.segments.sorted { $0.start < $1.start }
    if CommandLine.arguments.contains("--verbose") {
      let system = segments.filter { $0.source == .system }
      for s in segments {
        let echo = s.source == .microphone && TranscriptBuilder.isEcho(s, among: system) ? " [echo]" : ""
        FileHandle.standardError.write(
          Data(String(format: "%@ %6.2f-%6.2f%@ %@\n", s.source.rawValue, s.start, s.end, echo, s.text).utf8))
      }
    }

    var speakers: [AudioSourceKind: [SpeakerSpan]] = [:]
    var notes: [String] = []
    if config.separateSpeakers {
      for (source, url) in files where source == .system || config.micShared {
        do {
          speakers[source] = try await Diarization.speakers(in: url)
        } catch {
          notes.append("Speakers couldn't be identified: \(error.localizedDescription)")
        }
      }
    }

    var turns = TranscriptBuilder.turns(segments: segments, speakers: speakers, config: config)
    if config.mode == .translate, let target = config.targetLanguage {
      let translator = Translator(from: locale.language, to: target)
      for i in turns.indices {
        var parts: [String] = []
        for piece in turns[i].pieces { parts.append(await translator.translate(piece) ?? "") }
        turns[i].translation = parts.joined(separator: " ")
      }
    }

    let duration = files.compactMap { try? AVAudioFile(forReading: $0.1) }
      .map { Double($0.length) / $0.fileFormat.sampleRate }.max() ?? 0
    let end = Date()
    let document = try TranscriptDocument(config: config, startedAt: end.addingTimeInterval(-duration))
    var minutes: String?
    if let minutesConfig = config.minutes {
      let transcript = turns.map { "[\(TranscriptDocument.clock($0.start))] \($0.speaker ?? "Speaker"): \($0.text)" }
        .joined(separator: "\n")
      let client = MinutesClient(provider: minutesConfig.provider, model: minutesConfig.model, apiKey: minutesConfig.apiKey)
      do {
        let result = try await client.minutes(
          transcriptChunks: [transcript], previousMinutes: nil, config: minutesConfig, final: true, longCache: false)
        minutes = result.text
        FileHandle.standardError.write(Data("minutes: \(minutesConfig.model), in=\(result.usage.input) out=\(result.usage.output)\n".utf8))
      } catch {
        notes.append("Minutes couldn't be written: \(error.localizedDescription)")
      }
    }
    document.writeFinal(turns: turns, endedAt: end, notes: notes, audioFiles: [], minutes: minutes)
    return document.url
  }
}

private actor SegmentCollector {
  private(set) var segments: [FinalSegment] = []
  func add(_ segment: FinalSegment) { segments.append(segment) }
}
