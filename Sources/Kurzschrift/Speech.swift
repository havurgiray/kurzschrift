import AVFoundation
import Foundation
import Speech
import Translation

struct TimedWord {
  let text: String  // as recognized, including its leading space and trailing punctuation
  let start: Double
  let end: Double
}

/// A finalized stretch of speech from one source, with word timings on the session timeline.
struct FinalSegment: Identifiable {
  let id = UUID()
  let source: AudioSourceKind
  let text: String
  let start: Double
  let end: Double
  let words: [TimedWord]
}

enum SpeechEvent {
  case partial(AudioSourceKind, String)
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
  private let transcriber: SpeechTranscriber
  private let analyzer: SpeechAnalyzer
  private let inputContinuation: AsyncStream<AnalyzerInput>.Continuation
  private let analyzerFormat: AVAudioFormat
  private var converter: AVAudioConverter?
  private var resultsTask: Task<Void, Never>?

  private init(
    source: AudioSourceKind, transcriber: SpeechTranscriber, analyzer: SpeechAnalyzer,
    inputContinuation: AsyncStream<AnalyzerInput>.Continuation, analyzerFormat: AVAudioFormat
  ) {
    self.source = source
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

  static func make(source: AudioSourceKind, locale: Locale) async throws -> SpeechPipeline {
    let transcriber = makeTranscriber(locale: locale)
    guard let format = await SpeechAnalyzer.bestAvailableAudioFormat(compatibleWith: [transcriber]) else {
      throw SpeechError.noAudioFormat
    }
    let analyzer = SpeechAnalyzer(
      modules: [transcriber], options: .init(priority: .userInitiated, modelRetention: .processLifetime))
    try await analyzer.prepareToAnalyze(in: format)
    let (stream, continuation) = AsyncStream<AnalyzerInput>.makeStream()
    try await analyzer.start(inputSequence: stream)
    return SpeechPipeline(
      source: source, transcriber: transcriber, analyzer: analyzer, inputContinuation: continuation,
      analyzerFormat: format)
  }

  private static func makeTranscriber(locale: Locale) -> SpeechTranscriber {
    SpeechTranscriber(
      locale: locale, transcriptionOptions: [], reportingOptions: [.volatileResults, .fastResults],
      attributeOptions: [.audioTimeRange])
  }

  func start(onEvent: @escaping @Sendable (SpeechEvent) async -> Void) {
    let source = source
    let transcriber = transcriber
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
            await onEvent(.final(FinalSegment(source: source, text: text, start: start, end: end, words: words)))
          } else {
            await onEvent(.partial(source, text))
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

  private static func words(in text: AttributedString) -> [TimedWord] {
    var words: [TimedWord] = []
    var untimedPrefix = ""
    for run in text.runs {
      let piece = String(text[run.range].characters)
      if let range = run[AttributeScopes.SpeechAttributes.TimeRangeAttribute.self] {
        words.append(TimedWord(text: untimedPrefix + piece, start: range.start.seconds, end: range.end.seconds))
        untimedPrefix = ""
      } else if let last = words.popLast() {
        // Punctuation and spacing come without timing; attach them to the previous word.
        words.append(TimedWord(text: last.text + piece, start: last.start, end: last.end))
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
