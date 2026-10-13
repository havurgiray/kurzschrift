import Foundation
import NaturalLanguage

/// For meetings that switch between two languages: two recognizers hear the same audio, and for
/// each sentence this keeps the one that actually understood it.
///
/// The recognizers cut their results at different points (one may run a German and an English
/// sentence together), so finished results are first grouped into windows of overlapping speech.
/// A window is decided once both recognizers have moved past it, or a few seconds after it ended.
/// It is then split at the pauses both recognizers agree on (between sentences), and each part
/// goes to the recognizer whose words there score higher: its own confidence blended with how sure
/// Apple's language detector is that the words are in that recognizer's language.
@MainActor
final class LanguageArbiter {
  let primary: String
  let secondary: String
  /// The language that won the latest part; in-progress text is shown only from this one.
  private(set) var current: String
  private var queues: [String: [FinalSegment]] = [:]
  private var decidedUntil = -Double.infinity

  init(primary: String, secondary: String) {
    self.primary = primary
    self.secondary = secondary
    current = primary
  }

  func add(_ segment: FinalSegment) -> [FinalSegment] {
    // A late result for speech that has already been decided.
    if (segment.start + segment.end) / 2 < decidedUntil {
      Log.write("arbiter: dropped late \(segment.locale) \(Self.span(segment))")
      return []
    }
    Log.write(String(format: "arbiter: got %@ %@ confidence %.2f", segment.locale, Self.span(segment), segment.confidence))
    queues[segment.locale, default: []].append(segment)
    return resolve(force: false, horizon: nil)
  }

  /// `progress`: how far each recognizer has finalized (seconds). Windows both have moved past are
  /// decided; windows that ended before `horizon` are decided regardless (safety net).
  func flush(progress: [String: Double], horizon: Double) -> [FinalSegment] {
    self.progress = progress
    return resolve(force: false, horizon: horizon)
  }

  private var progress: [String: Double] = [:]
  /// The last word emitted, to drop its duplicate at a language switch.
  private var lastWord = ""

  private static func normalized(_ word: String) -> String {
    word.lowercased().filter { $0.isLetter || $0.isNumber }
  }

  func flushAll() -> [FinalSegment] {
    resolve(force: true, horizon: nil)
  }

  /// All results of a finished recording at once (reprocessing).
  func decideAll(_ segments: [FinalSegment]) -> [FinalSegment] {
    for segment in segments { queues[segment.locale, default: []].append(segment) }
    return resolve(force: true, horizon: nil)
  }

  // MARK: Windows

  private func resolve(force: Bool, horizon: Double?) -> [FinalSegment] {
    var emitted: [FinalSegment] = []
    while true {
      let all = queues.values.joined().sorted { $0.start < $1.start }
      guard let first = all.first else { break }
      var members: Set<UUID> = [first.id]
      var end = first.end
      var grew = true
      while grew {
        grew = false
        for s in all where !members.contains(s.id) && s.start < end + 0.3 {
          members.insert(s.id)
          end = max(end, s.end)
          grew = true
        }
      }
      let a = queues[primary, default: []].filter { members.contains($0.id) }
      let b = queues[secondary, default: []].filter { members.contains($0.id) }
      // A recognizer is past the window when it has a result ending there, a later result, or has
      // finalized everything up to the window's end. A result ending early doesn't count: the
      // recognizer may still be working on the rest.
      let aPast = queues[primary, default: []].contains { $0.start > end } || (progress[primary] ?? -1) >= end - 0.3
        || a.contains { $0.end >= end - 0.5 }
      let bPast = queues[secondary, default: []].contains { $0.start > end } || (progress[secondary] ?? -1) >= end - 0.3
        || b.contains { $0.end >= end - 0.5 }
      let stale = horizon.map { end < $0 } ?? false
      guard force || (aPast && bPast) || stale else { break }

      emitted += decide(a, b)
      decidedUntil = max(decidedUntil, end)
      for key in queues.keys { queues[key]?.removeAll { members.contains($0.id) } }
    }
    return emitted.sorted { $0.start < $1.start }
  }

  // MARK: Deciding a window

  private struct Word {
    let word: TimedWord
    let language: String
    let confidence: Double
    /// Word start times swallow the silence before them, so a word is placed by its end.
    var from: Double { max(word.start, word.end - 0.4) }
  }

  private func decide(_ a: [FinalSegment], _ b: [FinalSegment]) -> [FinalSegment] {
    let words = (a + b).flatMap { segment in
      (segment.words.isEmpty
        ? [TimedWord(text: segment.text, start: segment.start, end: segment.end)] : segment.words)
        .map { Word(word: $0, language: segment.locale, confidence: segment.confidence) }
    }
    // Split where neither recognizer has a word for at least 0.35 s: the pauses between sentences.
    var parts: [[Word]] = []
    var reach = -Double.infinity
    for word in words.sorted(by: { $0.from < $1.from }) {
      if word.from > reach + 0.35 || parts.isEmpty { parts.append([]) }
      parts[parts.count - 1].append(word)
      reach = max(reach, word.word.end)
    }

    var result: [FinalSegment] = []
    for part in parts {
      let mine = part.filter { $0.language == primary }
      let theirs = part.filter { $0.language == secondary }
      let primaryScore = score(mine, language: primary)
      let secondaryScore = score(theirs, language: secondary)
      let winner = primaryScore >= secondaryScore ? mine : theirs
      Log.write(String(
        format: "arbiter: %.1f-%.1f %@ %.2f (%d words) vs %@ %.2f (%d words)",
        part.map(\.from).min() ?? 0, part.map(\.word.end).max() ?? 0, primary, primaryScore, mine.count, secondary,
        secondaryScore, theirs.count))
      guard let language = winner.first?.language else { continue }
      current = language
      var sorted = winner.sorted { $0.word.end < $1.word.end }
      // At a language switch the losing recognizer's copy of the boundary word ("section",
      // "Monday") can land in the next part; drop it when it repeats the word just kept.
      if let first = sorted.first, Self.normalized(first.word.text) == lastWord, !lastWord.isEmpty {
        sorted.removeFirst()
      }
      guard let lastKept = sorted.last else { continue }
      lastWord = Self.normalized(lastKept.word.text)
      let text = TextCleanup.tidy(sorted.map(\.word.text).joined())
      guard !text.isEmpty else { continue }
      var segment = FinalSegment(
        source: a.first?.source ?? b.first?.source ?? .system, text: text,
        start: sorted.first?.from ?? 0, end: sorted.last?.word.end ?? 0, words: sorted.map(\.word))
      segment.locale = language
      segment.confidence = sorted.map(\.confidence).reduce(0, +) / Double(sorted.count)
      result.append(segment)
    }
    return result
  }

  private func score(_ words: [Word], language: String) -> Double {
    guard !words.isEmpty else { return -1 }
    let text = words.sorted { $0.word.end < $1.word.end }.map(\.word.text).joined()
    let confidence = words.map(\.confidence).reduce(0, +) / Double(words.count)
    let recognizer = NLLanguageRecognizer()
    recognizer.processString(text)
    let code = Locale(identifier: language).language.languageCode?.identifier ?? language
    let probability = recognizer.languageHypotheses(withMaximum: 5)[NLLanguage(rawValue: code)] ?? 0
    return 0.5 * confidence + 0.5 * probability
  }

  private static func span(_ s: FinalSegment) -> String {
    String(format: "%.1f-%.1f", s.start, s.end)
  }

  static func sameLanguage(_ a: String, _ b: String) -> Bool {
    Locale(identifier: a).language.languageCode == Locale(identifier: b).language.languageCode
  }
}
