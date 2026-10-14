import Foundation

/// One speaker's uninterrupted stretch of speech in the final transcript.
struct Turn {
  var speaker: String?
  var start: Double
  var end: Double
  /// Original text in pieces as recognized; translated piece by piece (cached from the live view).
  var pieces: [String]
  /// The recognizer language of each piece (mixed-language meetings translate only the others).
  var languages: [String] = []
  var translation: String?

  var text: String { pieces.joined(separator: " ") }
}

enum TranscriptBuilder {
  /// Merges both sources, drops microphone echo, assigns speakers and groups speech into turns.
  static func turns(
    segments: [FinalSegment], speakers: [AudioSourceKind: [SpeakerSpan]], config: SessionConfig
  ) -> [Turn] {
    build(segments: segments, speakers: speakers, config: config, knownNames: [:]).turns
  }

  /// `knownNames`: "source:speakerId" → a remembered person's name. Returns the turns and the name
  /// each diarized speaker ended up with ("source:speakerId" → "Speaker 2" or "Maria").
  static func build(
    segments: [FinalSegment], speakers: [AudioSourceKind: [SpeakerSpan]], config: SessionConfig,
    knownNames: [String: String]
  ) -> (turns: [Turn], speakerNames: [String: String]) {
    let segments = removingEcho(segments.sorted { $0.start < $1.start })
    let bothSources = Set(segments.map(\.source)).count > 1 || config.includeMic

    // 1. Label every word, then cut segments where the speaker changes.
    var pieces: [(label: String?, start: Double, end: Double, text: String, language: String)] = []
    for segment in segments {
      if segment.source == .microphone, !(config.micShared && speakers[.microphone] != nil) {
        pieces.append((config.myName, segment.start, segment.end, segment.text, segment.locale))
        continue
      }
      guard let spans = speakers[segment.source], !spans.isEmpty, !segment.words.isEmpty else {
        let label: String? = segment.source == .microphone ? config.myName : (bothSources ? "Others" : nil)
        pieces.append((label, segment.start, segment.end, segment.text, segment.locale))
        continue
      }
      // Word start times swallow the silence before a sentence ("Okay" timed from the previous
      // speaker's last word), so a point just before the word's end is the reliable one.
      let labels = smooth(segment.words.map { speaker(at: max($0.start, $0.end - 0.1), in: spans) })
      var current: (label: String?, words: [TimedWord])?
      for (word, raw) in zip(segment.words, labels) {
        let label = "\(segment.source.rawValue):\(raw ?? Self.unknownSpeaker)"
        if let open = current, open.label == label {
          current!.words.append(word)
        } else {
          if let open = current { pieces.append(piece(open.label, open.words, segment.locale)) }
          current = (label, [word])
        }
      }
      if let open = current { pieces.append(piece(open.label, open.words, segment.locale)) }
    }
    pieces.sort { $0.start < $1.start }

    // 2. Name diarized speakers "Speaker 1, 2, ..." in order of first appearance.
    var names: [String: String] = [:]
    var numbered = 0
    for p in pieces {
      guard let label = p.label, label.contains(":"), names[label] == nil else { continue }
      if let known = knownNames[label] {
        names[label] = known
      } else if label.hasSuffix(":\(unknownSpeaker)") {
        names[label] = "Unknown speaker"
      } else {
        numbered += 1
        names[label] = "Speaker \(numbered)"
      }
    }

    // 3. Merge consecutive pieces from the same speaker.
    var turns: [Turn] = []
    for p in pieces where !p.text.isEmpty {
      let name = p.label.map { names[$0] ?? $0 }
      if var last = turns.last, last.speaker == name, p.start - last.end < 30 {
        last.pieces.append(p.text)
        last.languages.append(p.language)
        last.end = max(last.end, p.end)
        turns[turns.count - 1] = last
      } else {
        turns.append(Turn(speaker: name, start: p.start, end: p.end, pieces: [p.text], languages: [p.language]))
      }
    }
    return (turns, names)
  }

  /// Speech that speaker detection didn't cover (very quiet or very short).
  private static let unknownSpeaker = "?"

  private static func piece(_ label: String?, _ words: [TimedWord], _ language: String)
    -> (label: String?, start: Double, end: Double, text: String, language: String)
  {
    let text = TextCleanup.tidy(words.map(\.text).joined())
    return (label, words.first?.start ?? 0, words.last?.end ?? 0, text, language)
  }

  /// Between two speakers' spans: speaker detection trims the tail of a turn, so a word shortly
  /// after a span ends usually still belongs to it, unless the next speaker starts right away.
  private static func speaker(at time: Double, in spans: [SpeakerSpan]) -> String? {
    if let hit = spans.first(where: { $0.start <= time && time <= $0.end }) { return hit.speaker }
    let next = spans.filter { $0.start > time }.min { $0.start < $1.start }
    let previous = spans.filter { $0.end < time }.max { $0.end < $1.end }
    if let next, next.start - time <= 0.3 { return next.speaker }
    if let previous, time - previous.end <= 1.0 { return previous.speaker }
    let nearest = spans.min { distance(time, $0) < distance(time, $1) }
    return nearest.flatMap { distance(time, $0) <= 1.5 ? $0.speaker : nil }
  }

  private static func distance(_ time: Double, _ span: SpeakerSpan) -> Double {
    time < span.start ? span.start - time : max(0, time - span.end)
  }

  /// Fills unknown labels from neighbours and removes one- or two-word blips of another speaker in
  /// the middle of someone's sentence, which are almost always boundary noise.
  private static func smooth(_ labels: [String?]) -> [String?] {
    var out = labels
    var lastKnown: String?
    for i in out.indices {
      if out[i] == nil { out[i] = lastKnown } else { lastKnown = out[i] }
    }
    if let firstKnown = out.first(where: { $0 != nil }) {
      for i in out.indices where out[i] == nil { out[i] = firstKnown }
    }
    var i = 0
    while i < out.count {
      var j = i
      while j + 1 < out.count, out[j + 1] == out[i] { j += 1 }
      let runLength = j - i + 1
      if runLength <= 2, i > 0, j + 1 < out.count, out[i - 1] == out[j + 1] {
        for k in i...j { out[k] = out[i - 1] }
      }
      i = j + 1
    }
    return out
  }

  /// With loudspeakers instead of headphones the microphone also hears the other participants.
  /// Drops microphone segments that repeat what system audio said at the same time.
  static func removingEcho(_ segments: [FinalSegment]) -> [FinalSegment] {
    let system = segments.filter { $0.source == .system }
    return segments.filter { $0.source != .microphone || !isEcho($0, among: system) }
  }

  /// An echo happens while someone else is talking (it mostly overlaps system speech in time) and
  /// says roughly the same thing. The microphone's copy is re-recognized from room sound, so words
  /// only match approximately ("Prises at the approved by Friday" for "Rising should be approved by
  /// Friday"): at least half of its real words must be close to a word the others said.
  static func isEcho(_ mic: FinalSegment, among system: [FinalSegment]) -> Bool {
    let slack = 0.5
    let nearby = system.filter { mic.start < $0.end + slack && $0.start < mic.end + slack }
    guard !nearby.isEmpty else { return false }
    let overlap = nearby.reduce(0.0) { $0 + max(0, min(mic.end, $1.end + slack) - max(mic.start, $1.start - slack)) }
    guard overlap / max(mic.end - mic.start, 0.1) >= 0.6 else { return false }

    let systemWords = words(nearby.map(\.text).joined(separator: " "))
    let micWords = words(mic.text)
    let content = micWords.filter { $0.count >= 3 }
    guard !content.isEmpty else {
      // "OK", "ja": only an exact repeat counts.
      return !micWords.isEmpty && systemWords.joined(separator: " ").contains(micWords.joined(separator: " "))
    }
    let matched = content.filter { word in systemWords.contains { isClose(word, $0) } }
    return Double(matched.count) / Double(content.count) >= 0.5
  }

  private static func words(_ text: String) -> [String] {
    text.lowercased().split { !$0.isLetter && !$0.isNumber }.map(String.init)
  }

  private static func isClose(_ a: String, _ b: String) -> Bool {
    if a == b { return true }
    guard abs(a.count - b.count) <= 2 else { return false }
    return editDistance(Array(a), Array(b)) <= max(1, a.count / 4)
  }

  private static func editDistance(_ a: [Character], _ b: [Character]) -> Int {
    var previous = Array(0...b.count)
    for i in 1...max(a.count, 1) where !a.isEmpty {
      var current = [i] + Array(repeating: 0, count: b.count)
      for j in 1...max(b.count, 1) where !b.isEmpty {
        current[j] = min(previous[j] + 1, current[j - 1] + 1, previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1))
      }
      previous = current
    }
    return previous[b.count]
  }
}

// MARK: - Markdown file

/// The transcript file: rewritten as a live draft while recording, replaced by the final
/// transcript (speakers, translation) when the meeting stops.
final class TranscriptDocument {
  let url: URL
  private let config: SessionConfig
  private let startedAt: Date

  init(config: SessionConfig, startedAt: Date) throws {
    self.config = config
    self.startedAt = startedAt
    try FileManager.default.createDirectory(at: config.saveFolder, withIntermediateDirectories: true)
    let stamp = Self.fileStamp.string(from: startedAt)
    let base = "\(stamp) \(Self.kindTitle(config))"
    var candidate = config.saveFolder.appendingPathComponent("\(base).md")
    var n = 2
    while FileManager.default.fileExists(atPath: candidate.path) {
      candidate = config.saveFolder.appendingPathComponent("\(base) \(n).md")
      n += 1
    }
    url = candidate
  }

  var baseName: String { url.deletingPathExtension().lastPathComponent }

  /// AI minutes, or only your own notes when live minutes are off.
  private var minutesHeading: String { config.minutes == nil ? "Notes" : "Minutes" }

  func writeDraft(entries: [SessionController.LiveEntry], minutes: String? = nil) {
    write(draftMarkdown(entries: entries, minutes: minutes))
  }

  func draftMarkdown(entries: [SessionController.LiveEntry], minutes: String?) -> String {
    var md = header(endedAt: nil, speakers: [])
    md += "_Recording in progress. Speakers are identified when the recording stops._\n\n"
    if let minutes, !minutes.isEmpty {
      md += "## \(minutesHeading) (live draft)\n\n\(minutes)\n\n## Transcript\n\n"
    }
    for entry in entries {
      if entry.isBookmark {
        md += Self.bookmarkMarkdown(entry.start)
        continue
      }
      md += heading(entry.speaker, entry.start)
      if config.mode == .translate {
        md += "> \(entry.original)\n\n\(entry.translation ?? "")\n\n"
      } else {
        md += "\(entry.original)\n\n"
      }
    }
    return md
  }

  func writeFinal(
    turns: [Turn], endedAt: Date, notes: [String], audioFiles: [URL], minutes: String? = nil, bookmarks: [Double] = [],
    qa: [(question: String, answer: String)] = []
  ) {
    var pending = bookmarks.sorted()
    /// Bookmarks that fall before `time` and haven't been written yet.
    func marks(before time: Double) -> String {
      var md = ""
      while let next = pending.first, next <= time {
        md += Self.bookmarkMarkdown(next)
        pending.removeFirst()
      }
      return md
    }
    let speakers = turns.compactMap(\.speaker).filter { $0 != "Unknown speaker" }
      .reduce(into: [String]()) { if !$0.contains($1) { $0.append($1) } }
    var md = header(endedAt: endedAt, speakers: speakers)
    for note in notes { md += "> [!note]\n> \(note)\n\n" }
    if !audioFiles.isEmpty {
      md += "Audio: " + audioFiles.map { "[\($0.lastPathComponent)](<\($0.lastPathComponent)>)" }.joined(separator: ", ") + "\n\n"
    }
    if let minutes, !minutes.isEmpty {
      md += "## \(minutesHeading)\n\n\(minutes)\n\n"
    }
    if !qa.isEmpty {
      md += "## Questions\n\n"
      for item in qa { md += "**Q: \(item.question)**\n\n\(item.answer)\n\n" }
    }
    if (minutes?.isEmpty == false || !qa.isEmpty), !(config.mode == .translate && config.layout == .sections) {
      md += "## Transcript\n\n"
    }
    if turns.isEmpty { md += "_No speech was recognized._\n" }

    switch (config.mode, config.layout) {
    case (.transcribe, _):
      for turn in turns { md += marks(before: turn.start) + heading(turn.speaker, turn.start) + "\(turn.text)\n\n" }
    case (.translate, .interleaved):
      for turn in turns {
        md += marks(before: turn.start) + heading(turn.speaker, turn.start)
          + "> \(turn.text)\n\n\(turn.translation ?? "_(not translated)_")\n\n"
      }
    case (.translate, .sections):
      md += "## \(AppSettings.languageName(config.spokenLocale.identifier)) (original)\n\n"
      for turn in turns { md += marks(before: turn.start) + heading(turn.speaker, turn.start) + "\(turn.text)\n\n" }
      md += "## \(targetName) (translation)\n\n"
      for turn in turns { md += heading(turn.speaker, turn.start) + "\(turn.translation ?? "_(not translated)_")\n\n" }
    }
    md += marks(before: .infinity)
    write(md)
  }

  // MARK: Formatting

  private var targetName: String {
    config.targetLanguage.map { AppSettings.languageName($0.minimalIdentifier) } ?? ""
  }

  private func header(endedAt: Date?, speakers: [String]) -> String {
    let iso = ISO8601DateFormatter()
    iso.timeZone = .current
    var front = "---\n"
    front += "date: \(iso.string(from: startedAt))\n"
    if let endedAt {
      front += "end: \(iso.string(from: endedAt))\n"
      front += "duration: \(Int((endedAt.timeIntervalSince(startedAt) / 60).rounded())) min\n"
    }
    front += "type: meeting-transcript\n"
    front += "language: \(config.spokenLocale.identifier)\n"
    if config.mode == .translate, let target = config.targetLanguage {
      front += "translation: \(target.minimalIdentifier)\n"
    }
    if !speakers.isEmpty {
      front += "speakers: [" + speakers.map { "\"\($0)\"" }.joined(separator: ", ") + "]\n"
    }
    if let event = config.eventTitle {
      front += "event: \"\(event.replacingOccurrences(of: "\"", with: "'"))\"\n"
    }
    if !config.attendees.isEmpty {
      front += "attendees: [" + config.attendees.map { "\"\($0)\"" }.joined(separator: ", ") + "]\n"
    }
    front += "---\n\n"
    let title = "# \(Self.kindTitle(config)), \(Self.titleDate.string(from: startedAt))\n\n"
    return front + title
  }

  private func heading(_ speaker: String?, _ seconds: Double) -> String {
    let time = Self.clock(seconds)
    return speaker.map { "**\($0)** · \(time)\n\n" } ?? "**\(time)**\n\n"
  }

  private func write(_ text: String) {
    do {
      try text.write(to: url, atomically: true, encoding: .utf8)
    } catch {
      Log.write("writing transcript failed: \(error)")
    }
  }

  static func bookmarkMarkdown(_ seconds: Double) -> String {
    "**⭐ Bookmark** · \(clock(seconds))\n\n"
  }

  static func clock(_ seconds: Double) -> String {
    let s = max(0, Int(seconds))
    return String(format: "%02d:%02d:%02d", s / 3600, s / 60 % 60, s % 60)
  }

  private static func kindTitle(_ config: SessionConfig) -> String {
    if let event = config.eventTitle {
      let safe = event.components(separatedBy: CharacterSet(charactersIn: "/:\\?%*|\"<>")).joined(separator: "-")
        .trimmingCharacters(in: .whitespaces)
      let short = safe.count > 80 ? String(safe.prefix(80)) : safe
      return config.mode == .translate ? "\(short) (translated)" : short
    }
    let spoken = AppSettings.languageName(config.spokenLocale.identifier)
    switch config.mode {
    case .translate: return "\(spoken) meeting (translated)"
    case .transcribe: return "\(spoken) meeting"
    }
  }

  private static let fileStamp: DateFormatter = {
    let f = DateFormatter()
    f.dateFormat = "yyyy-MM-dd HH-mm"
    return f
  }()

  private static let titleDate: DateFormatter = {
    let f = DateFormatter()
    f.dateFormat = "d MMM yyyy, HH:mm"
    f.locale = Locale(identifier: "en_GB")
    return f
  }()
}
