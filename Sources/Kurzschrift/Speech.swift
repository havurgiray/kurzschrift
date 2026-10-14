import AVFoundation
import Foundation
import Speech
import Translation

struct TimedWord: Codable {
  var text: String  // as recognized, including its leading space and trailing punctuation
  let start: Double
  let end: Double
  /// The recognizer's confidence in this word (0...1), when it gave one.
  var confidence: Double?
}

/// A finalized stretch of speech from one source, with word timings on the session timeline.
struct FinalSegment: Identifiable, Codable {
  var id = UUID()
  let source: AudioSourceKind
  var text: String
  let start: Double
  let end: Double
  var words: [TimedWord]
  /// The recognizer's language ("de_DE") and its average confidence (0...1), for mixed meetings.
  var locale = ""
  var confidence = 0.0
}

/// The recognizer sometimes starts a result with the punctuation that ends the previous one
/// (". Gut, dann…", "? Ja, das mache ich") and puts spaces before punctuation ("allen .").
enum TextCleanup {
  private static let punctuation: Set<Character> = [".", ",", "?", "!", ";", ":", "…"]

  /// (".", "Gut, dann") for ". Gut, dann".
  static func splitLeadingPunctuation(_ text: String) -> (lead: String, rest: String) {
    let trimmed = text.drop { $0.isWhitespace }
    let lead = trimmed.prefix { punctuation.contains($0) || $0.isWhitespace }
    return (lead.filter { !$0.isWhitespace }, String(trimmed.dropFirst(lead.count)))
  }

  static func endsWithPunctuation(_ text: String) -> Bool {
    text.trimmingCharacters(in: .whitespaces).last.map { punctuation.contains($0) } ?? false
  }

  static func tidy(_ text: String) -> String {
    text.replacingOccurrences(of: #"\s+([.,?!;:…])"#, with: "$1", options: .regularExpression)
      .trimmingCharacters(in: .whitespacesAndNewlines)
  }
}

enum SpeechEvent {
  case partial(AudioSourceKind, String, locale: String)
  case final(FinalSegment)
}

enum SpeechError: LocalizedError {
  case unavailable
  case unsupportedLocale(String)
  case noAudioFormat

  var errorDescription: String? {
    switch self {
    case .unavailable: "On-device speech recognition isn't available on this Mac."
    case .unsupportedLocale(let id): "macOS speech recognition doesn't support \(AppSettings.localeName(id))."
    case .noAudioFormat: "Speech recognition couldn't agree on an audio format."
    }
  }
}

/// Apple's on-device SpeechAnalyzer for one audio source.
final class SpeechPipeline: @unchecked Sendable {
  let source: AudioSourceKind
  let locale: Locale
  private let transcriber: SpeechTranscriber
  private let analyzer: SpeechAnalyzer
  private let inputContinuation: AsyncStream<AnalyzerInput>.Continuation
  private let analyzerFormat: AVAudioFormat
  private var converter: AVAudioConverter?
  private var resultsTask: Task<Void, Never>?

  private init(
    source: AudioSourceKind, locale: Locale, transcriber: SpeechTranscriber, analyzer: SpeechAnalyzer,
    inputContinuation: AsyncStream<AnalyzerInput>.Continuation, analyzerFormat: AVAudioFormat
  ) {
    self.source = source
    self.locale = locale
    self.transcriber = transcriber
    self.analyzer = analyzer
    self.inputContinuation = inputContinuation
    self.analyzerFormat = analyzerFormat
  }

  /// Makes sure the speech model for `locale` is on disk (macOS downloads it once) and returns the
  /// locale macOS will actually use.
  static func prepareModel(for locale: Locale) async throws -> Locale {
    guard SpeechTranscriber.isAvailable else { throw SpeechError.unavailable }
    guard let supported = await SpeechTranscriber.supportedLocale(equivalentTo: locale) else {
      throw SpeechError.unsupportedLocale(locale.identifier)
    }
    let probe = makeTranscriber(locale: supported)
    if let request = try await AssetInventory.assetInstallationRequest(supporting: [probe]) {
      try await request.downloadAndInstall()
    }
    return supported
  }

  /// `vocabulary`: names and terms the recognizer should expect (Settings → General → Vocabulary).
  static func make(source: AudioSourceKind, locale: Locale, vocabulary: [String] = []) async throws -> SpeechPipeline {
    let transcriber = makeTranscriber(locale: locale)
    guard let format = await SpeechAnalyzer.bestAvailableAudioFormat(compatibleWith: [transcriber]) else {
      throw SpeechError.noAudioFormat
    }
    let analyzer = SpeechAnalyzer(
      modules: [transcriber], options: .init(priority: .userInitiated, modelRetention: .processLifetime))
    try await analyzer.prepareToAnalyze(in: format)
    if !vocabulary.isEmpty {
      let context = AnalysisContext()
      context.contextualStrings[.general] = vocabulary
      try await analyzer.setContext(context)
    }
    let (stream, continuation) = AsyncStream<AnalyzerInput>.makeStream()
    try await analyzer.start(inputSequence: stream)
    return SpeechPipeline(
      source: source, locale: locale, transcriber: transcriber, analyzer: analyzer, inputContinuation: continuation,
      analyzerFormat: format)
  }

  private static func makeTranscriber(locale: Locale) -> SpeechTranscriber {
    SpeechTranscriber(
      locale: locale, transcriptionOptions: [], reportingOptions: [.volatileResults, .fastResults],
      attributeOptions: [.audioTimeRange, .transcriptionConfidence])
  }

  func start(onEvent: @escaping @Sendable (SpeechEvent) async -> Void) {
    let source = source
    let transcriber = transcriber
    let localeID = locale.identifier
    resultsTask = Task {
      Log.write("\(source) speech results: listening")
      do {
        for try await result in transcriber.results {
          let text = String(result.text.characters).trimmingCharacters(in: .whitespacesAndNewlines)
          if result.isFinal {
            guard !text.isEmpty else { continue }
            let words = Self.words(in: result.text)
            let start = words.first?.start ?? result.range.start.seconds
            let end = words.last?.end ?? result.range.end.seconds
            var segment = FinalSegment(source: source, text: text, start: start, end: end, words: words)
            segment.locale = localeID
            segment.confidence = Self.confidence(of: result.text)
            await onEvent(.final(segment))
          } else {
            await onEvent(.partial(source, text, locale: localeID))
          }
        }
      } catch {
        Log.write("\(source) speech recognition ended: \(error)")
      }
    }
  }

  /// Called on the track's queue with audio in the session format.
  func append(_ buffer: AVAudioPCMBuffer) {
    if buffer.format == analyzerFormat {
      inputContinuation.yield(AnalyzerInput(buffer: buffer))
      return
    }
    if converter == nil { converter = AVAudioConverter(from: buffer.format, to: analyzerFormat) }
    guard let converter,
      let out = AVAudioPCMBuffer(
        pcmFormat: analyzerFormat,
        frameCapacity: AVAudioFrameCount(Double(buffer.frameLength) * analyzerFormat.sampleRate / buffer.format.sampleRate) + 64)
    else { return }
    var consumed = false
    var error: NSError?
    converter.convert(to: out, error: &error) { _, status in
      if consumed {
        status.pointee = .noDataNow
        return nil
      }
      consumed = true
      status.pointee = .haveData
      return buffer
    }
    if error == nil, out.frameLength > 0 { inputContinuation.yield(AnalyzerInput(buffer: out)) }
  }

  /// Audio time (seconds) up to which results are final; nil when nothing is pending.
  func finalizedThrough() async -> Double? {
    await analyzer.volatileRange?.start.seconds
  }

  /// Ends the input and waits until the last words have been finalized and delivered.
  func finish() async {
    inputContinuation.finish()
    do {
      try await analyzer.finalizeAndFinishThroughEndOfInput()
    } catch {
      Log.write("finalizing \(source) speech failed: \(error)")
    }
    await resultsTask?.value
  }

  /// Character-weighted mean of the recognizer's per-word confidence.
  private static func confidence(of text: AttributedString) -> Double {
    var total = 0.0
    var weight = 0.0
    for run in text.runs {
      guard let value = run[AttributeScopes.SpeechAttributes.ConfidenceAttribute.self] else { continue }
      let length = Double(text[run.range].characters.count)
      total += value * length
      weight += length
    }
    return weight > 0 ? total / weight : 0.5
  }

  private static func words(in text: AttributedString) -> [TimedWord] {
    var words: [TimedWord] = []
    var untimedPrefix = ""
    for run in text.runs {
      let piece = String(text[run.range].characters)
      if let range = run[AttributeScopes.SpeechAttributes.TimeRangeAttribute.self] {
        words.append(TimedWord(
          text: untimedPrefix + piece, start: range.start.seconds, end: range.end.seconds,
          confidence: run[AttributeScopes.SpeechAttributes.ConfidenceAttribute.self]))
        untimedPrefix = ""
      } else if let last = words.popLast() {
        // Punctuation and spacing come without timing; attach them to the previous word.
        words.append(TimedWord(text: last.text + piece, start: last.start, end: last.end, confidence: last.confidence))
      } else {
        untimedPrefix += piece
      }
    }
    return words
  }
}

// MARK: - Translation

actor Translator {
  private let session: TranslationSession
  private var cache: [String: String] = [:]

  init(from source: Locale.Language, to target: Locale.Language) {
    session = TranslationSession(installedSource: source, target: target)
  }

  static func isInstalled(from source: Locale.Language, to target: Locale.Language) async -> Bool {
    await LanguageAvailability().status(from: source, to: target) == .installed
  }

  func translate(_ text: String) async -> String? {
    let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
    guard !trimmed.isEmpty else { return "" }
    if let cached = cache[trimmed] { return cached }
    guard let translated = try? await session.translate(trimmed).targetText else { return nil }
    cache[trimmed] = translated
    return translated
  }

  /// For in-progress text, which changes constantly: don't fill the cache with fragments.
  func translateUncached(_ text: String) async -> String? {
    try? await session.translate(text).targetText
  }
}
