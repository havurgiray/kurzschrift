import AVFoundation
import FluidAudio
import Foundation
import NaturalLanguage

/// The finished transcript, re-recognized with NVIDIA Parakeet TDT 0.6B v3 (through FluidAudio, on
/// this Mac). Apple's recognizer stays in charge of the live captions; Parakeet redoes each stretch
/// of about 15 s soon after it has been spoken, and its lines replace Apple's.
///
/// Measured on AMI ES2004a (English, against the manual transcript): Apple 20.3% word errors,
/// Parakeet v3 16.3% on the whole recording and 16.8% in 15 s stretches. Single sentences are too
/// little context (26.1%). On an Austrian German meeting Apple left out whole sentences that
/// Parakeet got. One model covers German and English, including switches between them.
actor ParakeetRecognizer {
  private let manager: AsrManager

  private init(manager: AsrManager) {
    self.manager = manager
  }

  /// Downloads the model the first time (about 470 MB, into ~/Library/Application Support/FluidAudio).
  static func load() async throws -> ParakeetRecognizer {
    let models = try await AsrModels.downloadAndLoad(version: .v3)
    // In memory, one chunk at a time; FluidAudio's disk-streamed parallel path is for huge files.
    let manager = AsrManager(config: ASRConfig(parallelChunkConcurrency: 1, streamingEnabled: false))
    try await manager.loadModels(models)
    return ParakeetRecognizer(manager: manager)
  }

  /// Words with times on the session timeline; `offset` is where `samples` (16 kHz) start.
  func recognize(_ samples: [Float], offset: Double) async throws -> [TimedWord] {
    guard samples.count >= 8_000 else { return [] }
    var state = TdtDecoderState.make(decoderLayers: await manager.decoderLayerCount)
    let result = try await manager.transcribe(samples, decoderState: &state)
    return Self.words(from: result.tokenTimings ?? [], offset: offset)
  }

  /// Tokens (" geht", "s", ".") into words: a token starting with a space starts a new word.
  static func words(from tokens: [TokenTiming], offset: Double) -> [TimedWord] {
    var words: [TimedWord] = []
    var confidences: [Double] = []
    for token in tokens {
      let text = token.token
      if text.isEmpty { continue }
      if text.hasPrefix(" ") || words.isEmpty {
        if let last = words.indices.last { words[last].confidence = confidences.reduce(0, +) / Double(confidences.count) }
        words.append(TimedWord(text: text.hasPrefix(" ") ? text : " " + text, start: offset + token.startTime, end: offset + token.endTime))
        confidences = [Double(token.confidence)]
      } else {
        let last = words.count - 1
        words[last] = TimedWord(text: words[last].text + text, start: words[last].start, end: offset + token.endTime)
        confidences.append(Double(token.confidence))
      }
    }
    if let last = words.indices.last, !confidences.isEmpty {
      words[last].confidence = confidences.reduce(0, +) / Double(confidences.count)
    }
    return words
  }

  /// Splits words into sentences: after . ? ! or …, or at a pause of more than 1.5 s.
  static func sentences(_ words: [TimedWord]) -> [[TimedWord]] {
    var result: [[TimedWord]] = []
    var current: [TimedWord] = []
    for word in words {
      if let last = current.last, word.start - last.end > 1.5 {
        result.append(current)
        current = []
      }
      current.append(word)
      if let mark = word.text.trimmingCharacters(in: .whitespaces).last, ".?!…".contains(mark) {
        result.append(current)
        current = []
      }
    }
    if !current.isEmpty { result.append(current) }
    return result
  }

  /// The language of a sentence among the meeting's languages ("de_AT", "en_US", "en"). Short or
  /// unclear sentences keep `fallback`, the language Apple's recognizer heard at that point.
  static func language(of text: String, among candidates: [String], fallback: String) -> String {
    func code(_ identifier: String) -> String {
      Locale(identifier: identifier).language.languageCode?.identifier ?? identifier
    }
    let codes = Array(Set(candidates.map(code)))
    guard codes.count > 1, text.split(separator: " ").count >= 3 else { return fallback }
    let recognizer = NLLanguageRecognizer()
    recognizer.languageConstraints = codes.map { NLLanguage(rawValue: $0) }
    recognizer.processString(text)
    guard let (best, probability) = recognizer.languageHypotheses(withMaximum: 1).first, probability >= 0.6,
      code(fallback) != best.rawValue
    else { return fallback }
    return candidates.first { code($0) == best.rawValue } ?? fallback
  }

  /// One finished segment per sentence.
  static func segments(
    _ words: [TimedWord], source: AudioSourceKind, languages: [String], fallback: (Double, Double) -> String
  ) -> [FinalSegment] {
    sentences(words).compactMap { sentence in
      guard let first = sentence.first, let last = sentence.last else { return nil }
      let text = TextCleanup.tidy(sentence.map(\.text).joined())
      guard !text.isEmpty else { return nil }
      let confidence = sentence.compactMap(\.confidence).reduce(0, +) / Double(max(1, sentence.count))
      return FinalSegment(
        source: source, text: text, start: first.start, end: last.end, words: sentence,
        locale: language(of: text, among: languages, fallback: fallback(first.start, last.end)),
        confidence: confidence)
    }
  }
}

/// The recent audio of one track as 16 kHz samples on the session timeline, kept until Parakeet
/// has re-recognized it.
final class AudioHistory: @unchecked Sendable {
  private let lock = NSLock()
  private var samples: [Float] = []
  /// Timeline position (in samples) of `samples[0]`.
  private var firstSample = 0
  private var enabled = true

  /// Called on the track's own queue with session-format (Int16) buffers.
  func append(_ buffer: AVAudioPCMBuffer) {
    guard let data = buffer.int16ChannelData?[0] else { return }
    let count = Int(buffer.frameLength)
    lock.withLock {
      guard enabled else { return }
      samples.reserveCapacity(samples.count + count)
      for i in 0..<count { samples.append(Float(data[i]) / 32768) }
    }
  }

  var duration: Double {
    lock.withLock { Double(firstSample + samples.count) / sessionAudioFormat.sampleRate }
  }

  func samples(from start: Double, to end: Double) -> [Float] {
    lock.withLock {
      let rate = sessionAudioFormat.sampleRate
      let from = max(0, Int(start * rate) - firstSample)
      let to = min(samples.count, Int(end * rate) - firstSample)
      return from < to ? Array(samples[from..<to]) : []
    }
  }

  /// Frees what has been re-recognized.
  func discard(before time: Double) {
    lock.withLock {
      let drop = min(samples.count, Int(time * sessionAudioFormat.sampleRate) - firstSample)
      guard drop > 0 else { return }
      samples.removeFirst(drop)
      firstSample += drop
    }
  }

  /// When Parakeet isn't available after all: stop keeping audio.
  func disable() {
    lock.withLock {
      enabled = false
      samples = []
    }
  }
}
