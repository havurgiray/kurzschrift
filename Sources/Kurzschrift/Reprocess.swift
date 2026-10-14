import AVFoundation
import Foundation

/// Rebuilds a transcript from kept recordings (.m4a or .wav), without the menu bar app or any permissions:
///
///     Kurzschrift --reprocess --mode transcribe|translate --system "meeting audio.m4a" \
///         [--mic "microphone.m4a"] [--out folder] [--language de-DE] [--also en-US|none]
///         [--start 2026-09-30T10:20:00+02:00] [--verbose]
///
/// For tuning language decisions: `--save-segments raw.json` keeps every recognizer's results
/// before the decision, and `--load-segments raw.json` reuses them instead of recognizing again.
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
        Data("usage: Kurzschrift --reprocess --mode transcribe|translate --system a.m4a [--mic b.m4a] [--out folder]\n".utf8))
      exit(2)
    }
    var files: [(AudioSourceKind, URL)] = [(.system, URL(fileURLWithPath: systemPath))]
    if let mic = value("--mic") { files.append((.microphone, URL(fileURLWithPath: mic))) }

    var config = AppSettings.shared.snapshot(for: mode)
    config.includeMic = files.count > 1
    config.keepAudio = false
    if let out = value("--out") { config.saveFolder = URL(fileURLWithPath: out) }
    if let language = value("--language") { config.spokenLocale = Locale(identifier: language) }
    if let also = value("--also") { config.secondLocale = also == "none" ? nil : Locale(identifier: also) }
    let startedAt = value("--start").flatMap { ISO8601DateFormatter().date(from: $0) }

    Task { @MainActor in
      do {
        let url = try await run(files: files, config: config, startedAt: startedAt)
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
  private static func run(files: [(AudioSourceKind, URL)], config: SessionConfig, startedAt: Date?) async throws -> URL {
    let locale = try await SpeechPipeline.prepareModel(for: config.spokenLocale)
    var secondLocale: Locale?
    if let wanted = config.secondLocale, !LanguageArbiter.sameLanguage(wanted.identifier, locale.identifier) {
      secondLocale = try await SpeechPipeline.prepareModel(for: wanted)
    }
    let collector = SegmentCollector()
    let args = CommandLine.arguments
    func value(_ name: String) -> String? {
      args.firstIndex(of: name).flatMap { $0 + 1 < args.count ? args[$0 + 1] : nil }
    }
    if let path = value("--load-segments") {
      for segment in try JSONDecoder().decode([FinalSegment].self, from: Data(contentsOf: URL(fileURLWithPath: path))) {
        await collector.add(segment)
      }
    }
    for (source, url) in files where value("--load-segments") == nil {
      let pipeline = try await SpeechPipeline.make(source: source, locale: locale, vocabulary: config.vocabulary)
      pipeline.start { event in
        if case .final(let segment) = event { await collector.add(segment) }
      }
      // Mixed-language meetings: a second recognizer on the same audio; the arbiter picks per sentence.
      var second: SpeechPipeline?
      if let secondLocale {
        let extra = try await SpeechPipeline.make(source: source, locale: secondLocale, vocabulary: config.vocabulary)
        extra.start { event in
          if case .final(let segment) = event { await collector.add(segment) }
        }
        second = extra
      }
      let file = try AVAudioFile(forReading: url, commonFormat: .pcmFormatInt16, interleaved: true)
      let rate = file.processingFormat.sampleRate
      let hours = Double(file.length) / rate / 3600
      while file.framePosition < file.length {
        let frames = AVAudioFrameCount(min(16_000, file.length - file.framePosition))
        guard let buffer = AVAudioPCMBuffer(pcmFormat: file.processingFormat, frameCapacity: frames) else { break }
        try file.read(into: buffer, frameCount: frames)
        pipeline.append(buffer)
        second?.append(buffer)
        // Stay at most ten minutes of audio ahead of the recognizer, so long recordings don't pile
        // up in memory.
        let fed = Double(file.framePosition) / rate
        if Int(fed) % 600 == 0 {
          // During long silences the finalized position stops moving; don't wait on it then.
          var last: Double?
          var stalled = 0
          while let done = await pipeline.finalizedThrough(), fed - done > 600, stalled < 25 {
            stalled = done == last ? stalled + 1 : 0
            last = done
            try await Task.sleep(for: .milliseconds(200))
          }
          if hours > 0.5 {
            FileHandle.standardError.write(Data(String(format: "%@: %.1f of %.1f h\n", source.rawValue, fed / 3600, hours).utf8))
          }
        }
      }
      await pipeline.finish()
      await second?.finish()
    }
    var segments = await collector.segments.sorted { $0.start < $1.start }
    if let path = value("--save-segments") {
      try JSONEncoder().encode(segments).write(to: URL(fileURLWithPath: path))
    }
    if let secondLocale {
      var decided: [FinalSegment] = []
      for source in Set(segments.map(\.source)) {
        let arbiter = LanguageArbiter(primary: locale.identifier, secondary: secondLocale.identifier)
        decided += arbiter.decideAll(segments.filter { $0.source == source })
      }
      segments = decided.sorted { $0.start < $1.start }
      let kept = Dictionary(grouping: segments, by: \.locale).mapValues(\.count)
      FileHandle.standardError.write(Data("languages kept: \(kept)\n".utf8))
    }
    if CommandLine.arguments.contains("--verbose") {
      let system = segments.filter { $0.source == .system }
      for s in segments {
        let echo = s.source == .microphone && TranscriptBuilder.isEcho(s, among: system) ? " [echo]" : ""
        FileHandle.standardError.write(
          Data(String(format: "%@ %6.2f-%6.2f %@ %.2f%@ %@\n", s.source.rawValue, s.start, s.end, s.locale, s.confidence, echo, s.text).utf8))
      }
    }

    var speakers: [AudioSourceKind: [SpeakerSpan]] = [:]
    var notes: [String] = []
    if config.separateSpeakers {
      for (source, url) in files where source == .system || config.micShared {
        do {
          speakers[source] = try await Diarization.speakers(in: url).spans
        } catch {
          notes.append("Speakers couldn't be identified: \(error.localizedDescription)")
        }
      }
    }

    var turns = TranscriptBuilder.turns(segments: segments, speakers: speakers, config: config)
    if config.mode == .translate, let target = config.targetLanguage {
      // From the spoken language, or from the second one when the first is already the target.
      let from = ([locale] + (secondLocale.map { [$0] } ?? []))
        .first { !LanguageArbiter.sameLanguage($0.identifier, target.minimalIdentifier) } ?? locale
      let translator = Translator(from: from.language, to: target)
      for i in turns.indices {
        var parts: [String] = []
        for (index, piece) in turns[i].pieces.enumerated() {
          let language = index < turns[i].languages.count ? turns[i].languages[index] : ""
          if LanguageArbiter.sameLanguage(language, target.minimalIdentifier) {
            parts.append(piece)  // already in the translation language
          } else {
            parts.append(await translator.translate(piece) ?? "")
          }
        }
        turns[i].translation = parts.joined(separator: " ")
      }
    }

    let duration = files.compactMap { try? AVAudioFile(forReading: $0.1) }
      .map { Double($0.length) / $0.fileFormat.sampleRate }.max() ?? 0
    let start = startedAt ?? Date().addingTimeInterval(-duration)
    let end = start.addingTimeInterval(duration)
    let document = try TranscriptDocument(config: config, startedAt: start)
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
