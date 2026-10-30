import AVFoundation
import AppKit
import Foundation
import UniformTypeIdentifiers

enum SessionError: LocalizedError {
  case translationNotInstalled(from: String, to: String)
  case nothingToTranslate(String)
  case microphoneDenied

  var errorDescription: String? {
    switch self {
    case .translationNotInstalled(let from, let to):
      "The \(from) → \(to) translation languages aren't downloaded yet."
    case .nothingToTranslate(let language):
      "People speak \(language), the language you translate into, so there's nothing to translate."
    case .microphoneDenied:
      "Kurzschrift isn't allowed to use the microphone."
    }
  }

  var recoverySuggestion: String? {
    switch self {
    case .translationNotInstalled:
      "Open System Settings → General → Language & Region → Translation Languages… and download both languages."
    case .nothingToTranslate:
      "In Settings → Translate, set “People speak” to the meeting's language, or use Transcribe Meeting."
    case .microphoneDenied:
      "Allow it in System Settings → Privacy & Security → Microphone, or turn off “Include my microphone”."
    }
  }
}

/// Runs one meeting at a time: capture → speech → (translation) → live view and Markdown file.
@MainActor
final class SessionController: ObservableObject {
  static let shared = SessionController()

  enum Phase: Equatable {
    case idle
    case starting(String)
    case recording
    case finishing(String)
  }

  struct LiveEntry: Identifiable {
    let id: UUID
    let source: AudioSourceKind
    /// "Me"/"Others" while recording; "Speaker 1", ... once the meeting has been processed.
    let speaker: String?
    let start: Double
    let original: String
    var translation: String?
    var isBookmark = false
  }

  struct QAItem: Identifiable {
    let id = UUID()
    let question: String
    var answer: String?
    var error: String?
  }

  /// The last meeting after it stopped, kept so its file can be rewritten (questions asked
  /// afterwards, renamed speakers).
  struct FinishedMeeting {
    let document: TranscriptDocument
    var turns: [Turn]
    let endedAt: Date
    let notes: [String]
    let audioFiles: [URL]
    var minutes: String?
    let bookmarks: [Double]
    let config: SessionConfig
    /// Speaker name → voice embedding, for "remember this voice" when renaming.
    var voices: [String: [Float]]
  }

  /// What is being said right now: the recognizer's in-progress text and, when translating, its
  /// latest translation (refreshed at the live update interval, so it trails slightly).
  struct LivePartial: Equatable {
    var original = ""
    var translation: String?
    /// In mixed-language meetings, the language the text is being recognized in.
    var language: String?
  }

  @Published private(set) var phase: Phase = .idle {
    didSet { SubtitleController.shared.update() }
  }
  @Published private(set) var config: SessionConfig?
  @Published private(set) var startedAt: Date?
  @Published private(set) var elapsedSeconds = 0
  @Published private(set) var entries: [LiveEntry] = []
  @Published private(set) var livePartials: [AudioSourceKind: LivePartial] = [:]
  /// nil when live minutes are off for this meeting.
  @Published private(set) var minutes: MinutesState?
  /// The minutes as you edit them in the minutes pane; nil when you're not editing.
  @Published var minutesDraft: String?
  @Published private(set) var warning: String?
  @Published private(set) var lastTranscript: URL?
  /// Seconds until the recording stops by itself for lack of speech; set only in the last minute.
  @Published private(set) var autoStopIn: Int?
  @Published private(set) var questions: [QAItem] = []
  @Published private(set) var finished: FinishedMeeting?

  var isIdle: Bool { phase == .idle }
  var isRecording: Bool { phase == .recording }

  private var tracks: [AudioSourceKind: Track] = [:]
  private var systemSource: SystemAudioSource?
  private var micSource: MicrophoneSource?
  private var fileSource: FileAudioSource?
  private var pipelines: [AudioSourceKind: SpeechPipeline] = [:]
  /// Mixed-language meetings: a second recognizer per source and the arbiter that picks per window.
  private var secondaryPipelines: [AudioSourceKind: SpeechPipeline] = [:]
  private var arbiters: [AudioSourceKind: LanguageArbiter] = [:]
  private var translator: Translator?
  private var segments: [FinalSegment] = []
  private var document: TranscriptDocument?
  private var recordingFolder: URL?
  private var previewTask: Task<Void, Never>?
  private var pendingPreview: [AudioSourceKind: String] = [:]
  private var previewGeneration: [AudioSourceKind: Int] = [:]
  private var minutesRunner: MinutesRunner?
  /// The "minutes are on but there's no key" warning, cleared when AI minutes are switched on.
  private var minutesWarning: String?
  private var lastSpeechAt = Date()
  /// Session times (seconds) the user marked with ⌃⌥B or the ⭐ button.
  private var bookmarks: [Double] = []

  // MARK: Start

  /// `testAudio` replays a recording instead of capturing system audio (no microphone), for testing.
  func start(_ mode: MeetingMode, testAudio: URL? = nil) async {
    guard phase == .idle else {
      Log.write("start(\(mode)) ignored: phase is \(phase)")
      return
    }
    Log.write("start(\(mode))")
    var config = AppSettings.shared.snapshot(for: mode)
    if testAudio != nil { config.includeMic = false }
    if AppSettings.shared.useCalendar, testAudio == nil {
      phase = .starting("Checking the calendar…")
      if let meeting = await CalendarContext.currentMeeting() {
        config.eventTitle = meeting.title
        config.attendees = meeting.attendees
        if !meeting.attendees.isEmpty, let minutes = config.minutes {
          config.minutes = minutes.adding(
            context: "Attendees from the calendar invite: " + meeting.attendees.joined(separator: ", "))
        }
      }
    }
    self.config = config
    entries = []
    livePartials = [:]
    segments = []
    bookmarks = []
    questions = []
    finished = nil
    minutesWarning = AppSettings.shared.minutesProblem(for: mode)
    warning = minutesWarning
    minutes = nil
    minutesDraft = nil
    phase = .starting("Preparing speech recognition…")
    if AppSettings.shared.showLiveWindow { LiveWindowController.shared.show() }

    do {
      let locale = try await SpeechPipeline.prepareModel(for: config.spokenLocale)

      if mode == .translate, let target = config.targetLanguage {
        phase = .starting("Preparing translation…")
        // From the spoken language, or from the second one when the first is already the target.
        let candidates = [locale] + (config.secondLocale.map { [$0] } ?? [])
        guard let from = candidates.first(where: { !LanguageArbiter.sameLanguage($0.identifier, target.minimalIdentifier) })
        else {
          throw SessionError.nothingToTranslate(AppSettings.languageName(locale.identifier))
        }
        guard await Translator.isInstalled(from: from.language, to: target) else {
          throw SessionError.translationNotInstalled(
            from: AppSettings.languageName(from.identifier), to: AppSettings.languageName(target.minimalIdentifier))
        }
        let translator = Translator(from: from.language, to: target)
        _ = await translator.translateUncached("Hallo")  // loads the model before the first real sentence
        self.translator = translator
      }

      if config.includeMic, await AVCaptureDevice.requestAccess(for: .audio) == false {
        throw SessionError.microphoneDenied
      }

      var secondLocale: Locale?
      if let wanted = config.secondLocale, !LanguageArbiter.sameLanguage(wanted.identifier, locale.identifier) {
        phase = .starting("Preparing \(AppSettings.languageName(wanted.identifier)) recognition…")
        secondLocale = try? await SpeechPipeline.prepareModel(for: wanted)
      }
      self.config?.secondLocale = secondLocale

      phase = .starting("Starting audio…")
      let sources: [AudioSourceKind] = config.includeMic ? [.system, .microphone] : [.system]
      for source in sources {
        let pipeline = try await SpeechPipeline.make(source: source, locale: locale, vocabulary: config.vocabulary)
        pipeline.start { [weak self] event in await self?.handle(event) }
        pipelines[source] = pipeline
        if let secondLocale {
          let second = try await SpeechPipeline.make(source: source, locale: secondLocale, vocabulary: config.vocabulary)
          second.start { [weak self] event in await self?.handle(event) }
          secondaryPipelines[source] = second
          arbiters[source] = LanguageArbiter(primary: locale.identifier, secondary: secondLocale.identifier)
        }
      }
      if config.separateSpeakers { Diarization.prepareModelsInBackground() }

      let folder = try Self.newRecordingFolder()
      recordingFolder = folder
      let start = Date()
      for source in sources {
        let pipeline = pipelines[source]!
        let second = secondaryPipelines[source]
        tracks[source] = try Track(
          kind: source, fileURL: folder.appendingPathComponent("\(source.rawValue).wav"), sessionStart: start
        ) { buffer in
          pipeline.append(buffer)
          second?.append(buffer)
        }
      }
      if let testAudio {
        let file = FileAudioSource(track: tracks[.system]!, url: testAudio)
        try file.start()
        fileSource = file
      } else {
        let system = SystemAudioSource(track: tracks[.system]!)
        try system.start()
        systemSource = system
      }
      if let micTrack = tracks[.microphone] {
        let mic = MicrophoneSource(track: micTrack, deviceUID: config.micDeviceUID)
        try mic.start()
        micSource = mic
      }

      document = try TranscriptDocument(config: config, startedAt: start)
      document?.writeDraft(entries: [])
      startedAt = start
      lastSpeechAt = start
      autoStopIn = nil
      elapsedSeconds = 0
      phase = .recording
      startPreviewLoop()
      if let minutesConfig = config.minutes {
        let runner = MinutesRunner(config: minutesConfig) { [weak self] state in self?.minutesChanged(state) }
        minutesRunner = runner
        runner.start()
      } else {
        // No AI minutes: the pane is for your own notes, saved with the transcript.
        minutes = MinutesState(usesAI: false, model: "", provider: AppSettings.shared.provider)
      }
      Log.write("recording: sources=\(sources.map(\.rawValue)) locale=\(locale.identifier)")
    } catch {
      Log.write("start failed: \(error)")
      await tearDown()
      phase = .idle
      self.config = nil
      present(error)
    }
  }

  // MARK: Stop

  func stop() async {
    guard phase == .recording, let config, let document else {
      Log.write("stop ignored: phase is \(phase)")
      return
    }
    Log.write("stop: \(segments.count) segments so far")
    phase = .finishing("Finishing transcription…")
    finishEditingMinutes()
    autoStopIn = nil
    previewTask?.cancel()
    systemSource?.stop()
    micSource?.stop()
    fileSource?.stop()
    systemSource = nil
    micSource = nil
    fileSource = nil
    for track in tracks.values { track.close() }
    for pipeline in Array(pipelines.values) + Array(secondaryPipelines.values) { await pipeline.finish() }
    for arbiter in arbiters.values {
      for segment in arbiter.flushAll() { await commit(segment) }
    }
    pipelines = [:]
    secondaryPipelines = [:]
    arbiters = [:]
    livePartials = [:]
    minutesRunner?.stop()
    let endedAt = Date()
    Log.write("speech finished: \(segments.count) segments")

    var notes: [String] = []
    var speakers: [AudioSourceKind: [SpeakerSpan]] = [:]
    var speakerVoices: [String: [Float]] = [:]
    if config.separateSpeakers {
      var toDiarize: [AudioSourceKind] = segments.contains { $0.source == .system } ? [.system] : []
      if config.micShared, segments.contains(where: { $0.source == .microphone }) { toDiarize.append(.microphone) }
      for source in toDiarize {
        guard let track = tracks[source] else { continue }
        phase = .finishing("Identifying speakers…")
        do {
          let result = try await Diarization.speakers(in: track.fileURL)
          speakers[source] = result.spans
          for (id, embedding) in result.voices { speakerVoices["\(source.rawValue):\(id)"] = embedding }
          Log.write("diarized \(source): \(speakers[source]?.count ?? 0) spans, \(Set(speakers[source]?.map(\.speaker) ?? []).count) speakers")
        } catch {
          Log.write("diarization failed: \(error)")
          notes.append("Speakers couldn't be identified: \(error.localizedDescription)")
        }
      }
    }

    let known = AppSettings.shared.recognizeVoices ? VoiceLibrary.match(speakerVoices) : [:]
    if !known.isEmpty { Log.write("recognized \(known.count) known voice(s)") }
    let built = TranscriptBuilder.build(segments: segments, speakers: speakers, config: config, knownNames: known)
    var turns = built.turns
    var voicesByName: [String: [Float]] = [:]
    for (label, name) in built.speakerNames { if let voice = speakerVoices[label] { voicesByName[name] = voice } }
    if let translator {
      phase = .finishing("Translating transcript…")
      for i in turns.indices {
        var parts: [String] = []
        for (index, piece) in turns[i].pieces.enumerated() {
          let language = index < turns[i].languages.count ? turns[i].languages[index] : ""
          if let target = config.targetLanguage, LanguageArbiter.sameLanguage(language, target.minimalIdentifier) {
            parts.append(piece)
          } else {
            parts.append(await translator.translate(piece) ?? "")
          }
        }
        turns[i].translation = parts.joined(separator: " ")
      }
    }

    var finalMinutes: String?
    if let minutesRunner {
      phase = .finishing("Writing minutes…")
      finalMinutes = await minutesRunner.finalize(transcript: Self.minutesTranscript(turns: turns, bookmarks: bookmarks))
      if finalMinutes == nil, let live = minutes?.text, !live.isEmpty {
        finalMinutes = live
        notes.append("The final minutes couldn't be written (\(minutes?.error ?? "unknown error")); these are the last live minutes.")
      }
    } else if let text = minutes?.text, !text.isEmpty {
      finalMinutes = text  // your own notes
    }

    var audioFiles: [URL] = []
    if config.keepAudio {
      phase = .finishing("Saving the recording…")
      for (source, track) in tracks.sorted(by: { $0.key.rawValue > $1.key.rawValue }) {
        let name = source == .system ? "meeting audio" : "microphone"
        let base = config.saveFolder.appendingPathComponent("\(document.baseName) (\(name))")
        if let saved = await KeptRecording.save(track.fileURL, as: base, kbps: config.audioBitrate) {
          audioFiles.append(saved)
        }
      }
    }
    finished = FinishedMeeting(
      document: document, turns: turns, endedAt: endedAt, notes: notes, audioFiles: audioFiles, minutes: finalMinutes,
      bookmarks: bookmarks, config: config, voices: voicesByName)
    rewriteFinished()
    Log.write("saved \(document.url.lastPathComponent): \(turns.count) turns")
    lastTranscript = document.url
    entries = turns.map {
      LiveEntry(id: UUID(), source: .system, speaker: $0.speaker, start: $0.start, original: $0.text, translation: $0.translation)
    } + bookmarks.map(Self.bookmarkEntry)
    entries.sort { $0.start < $1.start }

    await tearDown()
    phase = .idle
  }

  private func tearDown() async {
    previewTask?.cancel()
    minutesRunner?.stop()
    minutesRunner = nil
    systemSource?.stop()
    micSource?.stop()
    fileSource?.stop()
    fileSource = nil
    for track in tracks.values { track.close() }
    for pipeline in Array(pipelines.values) + Array(secondaryPipelines.values) { await pipeline.finish() }
    systemSource = nil
    micSource = nil
    pipelines = [:]
    secondaryPipelines = [:]
    arbiters = [:]
    tracks = [:]
    translator = nil
    document = nil
    pendingPreview = [:]
    if let recordingFolder { try? FileManager.default.removeItem(at: recordingFolder) }
    recordingFolder = nil
  }

  // MARK: Live results

  private func handle(_ event: SpeechEvent) async {
    lastSpeechAt = Date()
    autoStopIn = nil
    switch event {
    case .partial(let source, let raw, let language):
      // In mixed meetings, show in-progress text only from the language currently being spoken.
      if let arbiter = arbiters[source] {
        arbiter.notePartial(raw, language: language)
        if !LanguageArbiter.sameLanguage(language, arbiter.current) { return }
      }
      let text = TextCleanup.tidy(TextCleanup.splitLeadingPunctuation(raw).rest)
      livePartials[source, default: LivePartial()].original = text
      if arbiters[source] != nil { livePartials[source]?.language = language }
      if let target = config?.targetLanguage, LanguageArbiter.sameLanguage(language, target.minimalIdentifier) {
        livePartials[source]?.translation = text  // already in the translation language
      } else if translator != nil {
        pendingPreview[source] = text
      }

    case .final(let segment):
      if let arbiter = arbiters[segment.source] {
        for chosen in arbiter.add(segment) { await commit(chosen) }
      } else {
        await commit(segment)
      }
    }
  }

  /// A finished stretch of speech: into the transcript, the translation, the file and the minutes.
  private func commit(_ incoming: FinalSegment) async {
    var segment = incoming
    previewGeneration[segment.source, default: 0] += 1
    pendingPreview[segment.source] = nil
    livePartials[segment.source] = nil
    repairPunctuation(&segment)
    guard !segment.text.isEmpty else { return }
    Log.write("final \(segment.source) \(String(format: "%.1f-%.1f", segment.start, segment.end)): \(segment.text.count) chars")
    segments.append(segment)
    let speaker = config?.includeMic == true ? (segment.source == .microphone ? config?.myName : "Others") : nil
    var entry = LiveEntry(
      id: segment.id, source: segment.source, speaker: speaker, start: segment.start, original: segment.text)
    if let translator {
      if let target = config?.targetLanguage, LanguageArbiter.sameLanguage(segment.locale, target.minimalIdentifier) {
        entry.translation = segment.text  // spoken in the translation language already
      } else {
        entry.translation = await translator.translate(segment.text)
      }
    }
    if insert(entry, for: segment) {
      minutesRunner?.add(line: "[\(TranscriptDocument.clock(segment.start))] \(speaker ?? "Speaker"): \(segment.text)")
    }
    document?.writeDraft(entries: entries, minutes: minutes?.text)
  }

  /// Returns false when the line was dropped as loudspeaker echo.
  @discardableResult
  private func insert(_ entry: LiveEntry, for segment: FinalSegment) -> Bool {
    // Hide microphone lines that are just loudspeaker echo of what someone else said.
    let system = segments.filter { $0.source == .system }
    if segment.source == .microphone, TranscriptBuilder.isEcho(segment, among: system) {
      return false
    }
    if segment.source == .system {
      let echoes = Set(
        segments.filter { $0.source == .microphone && TranscriptBuilder.isEcho($0, among: system) }.map(\.id))
      entries.removeAll { echoes.contains($0.id) }
    }
    let index = entries.lastIndex { $0.start <= entry.start }.map { $0 + 1 } ?? 0
    entries.insert(entry, at: index)
    return true
  }

  /// Stops and saves after Settings → "Stop after silence" minutes without speech, counting down
  /// the last minute in the window.
  private func checkAutoStop() {
    let limit = Double(AppSettings.shared.autoStopMinutes * 60)
    guard limit > 0, isRecording else { return }
    let quiet = Date().timeIntervalSince(lastSpeechAt)
    if quiet >= limit {
      Log.write("auto-stop after \(Int(quiet)) s without speech")
      autoStopIn = nil
      Task { @MainActor in await self.stop() }
    } else if quiet >= limit - 60 {
      autoStopIn = Int(limit - quiet)
    } else if autoStopIn != nil {
      autoStopIn = nil
    }
  }

  /// Writes the finished meeting's file again with its current speaker names and questions.
  func rewriteFinished() {
    guard let f = finished else { return }
    let qa = questions.compactMap { item in item.answer.map { (question: item.question, answer: $0) } }
    f.document.writeFinal(
      turns: f.turns, endedAt: f.endedAt, notes: f.notes, audioFiles: f.audioFiles, minutes: f.minutes,
      bookmarks: f.bookmarks, qa: qa)
  }

  /// Renames a speaker throughout the finished meeting (file, window, minutes) and, if asked,
  /// remembers the voice so the name is used automatically next time.
  func renameSpeaker(_ old: String, to newName: String, remember: Bool) {
    let new = newName.trimmingCharacters(in: .whitespacesAndNewlines)
    guard var f = finished, !new.isEmpty, new != old else { return }
    for i in f.turns.indices where f.turns[i].speaker == old { f.turns[i].speaker = new }
    let pattern = "\\b" + NSRegularExpression.escapedPattern(for: old) + "\\b"
    let template = NSRegularExpression.escapedTemplate(for: new)
    f.minutes = f.minutes?.replacingOccurrences(of: pattern, with: template, options: .regularExpression)
    if let voice = f.voices.removeValue(forKey: old) {
      f.voices[new] = voice
      if remember { VoiceLibrary.remember(new, embedding: voice) }
    }
    finished = f
    entries = entries.map { entry in
      guard entry.speaker == old else { return entry }
      return LiveEntry(
        id: entry.id, source: entry.source, speaker: new, start: entry.start, original: entry.original,
        translation: entry.translation, isBookmark: entry.isBookmark)
    }
    if var state = minutes, let text = f.minutes {
      state.text = text
      minutes = state
    }
    rewriteFinished()
    Log.write("speaker renamed")
  }

  // MARK: Questions

  var canAsk: Bool {
    let settings = AppSettings.shared
    return !settings.minutesModel.isEmpty && APIKeyStore.key(for: settings.provider) != nil
  }

  func ask(_ question: String) {
    let question = question.trimmingCharacters(in: .whitespacesAndNewlines)
    guard !question.isEmpty else { return }
    let settings = AppSettings.shared
    let index = questions.count
    questions.append(QAItem(question: question))
    guard let key = APIKeyStore.key(for: settings.provider), !settings.minutesModel.isEmpty else {
      questions[index].error = MinutesError.noKey.localizedDescription
      return
    }
    let transcript = entries.map { entry in
      entry.isBookmark
        ? "[\(TranscriptDocument.clock(entry.start))] ⭐ bookmarked by the user"
        : "[\(TranscriptDocument.clock(entry.start))] \(entry.speaker ?? "Speaker"): \(entry.original)"
    }.joined(separator: "\n")
    let language = config.map { config in
      AppSettings.languageName(config.targetLanguage?.minimalIdentifier ?? config.spokenLocale.identifier)
    } ?? "English"
    let client = MinutesClient(provider: settings.provider, model: settings.minutesModel, apiKey: key)
    Task {
      do {
        let result = try await client.answer(question: question, transcript: transcript, language: language)
        questions[index].answer = result.text
        Log.write("question answered, usage in=\(result.usage.input) out=\(result.usage.output)")
        if isIdle { rewriteFinished() }
      } catch {
        questions[index].error = error.localizedDescription
      }
    }
  }

  /// Start/stop from the global shortcuts: stops whatever is recording, otherwise starts `mode`.
  func toggle(_ mode: MeetingMode) {
    Task {
      if isRecording { await stop() } else if isIdle { await start(mode) }
    }
  }

  func addBookmark() {
    guard isRecording, let startedAt else { return }
    let time = Date().timeIntervalSince(startedAt)
    bookmarks.append(time)
    let index = entries.lastIndex { $0.start <= time }.map { $0 + 1 } ?? 0
    entries.insert(Self.bookmarkEntry(time), at: index)
    minutesRunner?.add(line: Self.bookmarkLine(time))
    document?.writeDraft(entries: entries, minutes: minutes?.text)
    NSSound(named: "Pop")?.play()
    Log.write("bookmark at \(Int(time)) s")
  }

  private static func bookmarkEntry(_ time: Double) -> LiveEntry {
    LiveEntry(id: UUID(), source: .system, speaker: nil, start: time, original: "Bookmark", isBookmark: true)
  }

  private static func bookmarkLine(_ time: Double) -> String {
    "[\(TranscriptDocument.clock(time))] ⭐ The user bookmarked this moment as important."
  }

  func keepRecording() {
    lastSpeechAt = Date()
    autoStopIn = nil
  }

  /// Moves punctuation that the recognizer put at the start of this segment back to the end of the
  /// previous one from the same source, and removes spaces before punctuation.
  private func repairPunctuation(_ segment: inout FinalSegment) {
    let (lead, rest) = TextCleanup.splitLeadingPunctuation(segment.text)
    segment.text = TextCleanup.tidy(rest)
    if !lead.isEmpty, !segment.words.isEmpty {
      segment.words[0].text = TextCleanup.splitLeadingPunctuation(segment.words[0].text).rest
    }
    guard !lead.isEmpty, let i = segments.lastIndex(where: { $0.source == segment.source }),
      !TextCleanup.endsWithPunctuation(segments[i].text)
    else { return }
    let mark = String(lead.prefix(1))
    segments[i].text += mark
    if !segments[i].words.isEmpty { segments[i].words[segments[i].words.count - 1].text += mark }
    if let e = entries.firstIndex(where: { $0.id == segments[i].id }) {
      entries[e] = LiveEntry(
        id: entries[e].id, source: entries[e].source, speaker: entries[e].speaker, start: entries[e].start,
        original: segments[i].text, translation: entries[e].translation)
    }
  }

  // MARK: Export

  enum ExportFormat { case markdown, text }

  var canExport: Bool { !entries.isEmpty || minutes?.text.isEmpty == false }

  /// The transcript (and translation and minutes) as it stands right now.
  func exportText(_ format: ExportFormat) -> String {
    switch format {
    case .markdown:
      if isRecording, let document { return document.draftMarkdown(entries: entries, minutes: minutes?.text) }
      if let lastTranscript, let saved = try? String(contentsOf: lastTranscript, encoding: .utf8) { return saved }
      return ""
    case .text:
      var lines: [String] = []
      if let minutes = minutes?.text, !minutes.isEmpty {
        lines += [self.minutes?.usesAI == false ? "NOTES" : "MINUTES", "", minutes, "", "TRANSCRIPT", ""]
      }
      for entry in entries {
        if entry.isBookmark {
          lines += ["[\(TranscriptDocument.clock(entry.start))] * BOOKMARK *", ""]
          continue
        }
        lines.append("[\(TranscriptDocument.clock(entry.start))]\(entry.speaker.map { " \($0)" } ?? "")")
        lines.append(entry.original)
        if let translation = entry.translation { lines.append(translation) }
        lines.append("")
      }
      return lines.joined(separator: "\n")
    }
  }

  func copyToClipboard() {
    NSPasteboard.general.clearContents()
    NSPasteboard.general.setString(exportText(.text), forType: .string)
  }

  func exportWithSavePanel(_ format: ExportFormat) {
    let panel = NSSavePanel()
    let ext = format == .markdown ? "md" : "txt"
    panel.allowedContentTypes = [UTType(filenameExtension: ext) ?? .plainText]
    let base = document?.baseName ?? lastTranscript?.deletingPathExtension().lastPathComponent ?? "Meeting"
    let stamp = Date().formatted(.dateTime.hour(.twoDigits(amPM: .omitted)).minute(.twoDigits))
      .replacingOccurrences(of: ":", with: "-")
    panel.nameFieldStringValue = isRecording ? "\(base) (as of \(stamp)).\(ext)" : "\(base).\(ext)"
    panel.directoryURL = config?.saveFolder ?? lastTranscript?.deletingLastPathComponent()
    panel.level = .floating
    NSApp.activate()
    guard panel.runModal() == .OK, let url = panel.url else { return }
    do {
      try exportText(format).write(to: url, atomically: true, encoding: .utf8)
      Log.write("exported \(format) transcript")
    } catch {
      present(error)
    }
  }

  private func minutesChanged(_ state: MinutesState) {
    let textChanged = state.text != minutes?.text
    minutes = state
    if textChanged, phase == .recording { document?.writeDraft(entries: entries, minutes: state.text) }
  }

  func updateMinutesNow() {
    minutesRunner?.updateNow()
  }

  func minutesIntervalChanged() {
    minutesRunner?.setInterval(TimeInterval(max(1, AppSettings.shared.minutesIntervalMinutes) * 60))
  }

  /// The pane holds only notes (there was no API key when the meeting started, or minutes are off
  /// for this kind of meeting), and a key and model are set now.
  var canStartAIMinutes: Bool {
    minutes?.usesAI == false && canAsk && (isRecording || (isIdle && finished != nil))
  }

  /// "AI Minutes" in the notes pane. During the meeting: minutes from the transcript so far right
  /// away, then at the usual interval. After it: final minutes from the finished transcript. Your
  /// notes are handed over as your own edits, so they're worked in rather than replaced.
  func startAIMinutes() {
    let meetingConfig = isRecording ? config : finished?.config
    guard canStartAIMinutes, let meetingConfig,
      var minutesConfig = AppSettings.shared.minutesConfig(for: meetingConfig.mode, evenIfOff: true)
    else { return }
    if !meetingConfig.attendees.isEmpty {
      minutesConfig = minutesConfig.adding(
        context: "Attendees from the calendar invite: " + meetingConfig.attendees.joined(separator: ", "))
    }
    finishEditingMinutes()  // notes still being typed count too
    let notes = minutes?.text ?? ""
    let runner = MinutesRunner(config: minutesConfig) { [weak self] state in self?.minutesChanged(state) }
    if !notes.isEmpty { runner.finishEditing(text: notes) }
    minutes = runner.state
    if warning == minutesWarning { warning = nil }
    Log.write("AI minutes switched on \(isRecording ? "during" : "after") the meeting, notes: \(notes.isEmpty ? "none" : "yes")")

    if isRecording {
      config?.minutes = minutesConfig
      document?.usesAIMinutes = true
      for entry in entries {
        runner.add(
          line: entry.isBookmark
            ? Self.bookmarkLine(entry.start)
            : "[\(TranscriptDocument.clock(entry.start))] \(entry.speaker ?? "Speaker"): \(entry.original)")
      }
      minutesRunner = runner
      runner.start()
      runner.updateNow()
    } else if let f = finished {
      Task {
        guard let text = await runner.finalize(transcript: Self.minutesTranscript(turns: f.turns, bookmarks: f.bookmarks))
        else { return }  // the error shows in the pane; your notes stay as they were
        f.document.usesAIMinutes = true
        finished?.minutes = text
        rewriteFinished()
      }
    }
  }

  /// The transcript as the minutes see it: one line per turn, with bookmarks in place.
  private static func minutesTranscript(turns: [Turn], bookmarks: [Double]) -> String {
    let lines = turns.map { (start: $0.start, line: "[\(TranscriptDocument.clock($0.start))] \($0.speaker ?? "Speaker"): \($0.text)") }
      + bookmarks.map { (start: $0, line: bookmarkLine($0)) }
    return lines.sorted { $0.start < $1.start }.map(\.line).joined(separator: "\n")
  }

  func beginEditingMinutes() {
    guard let minutes, minutesDraft == nil else { return }
    minutesDraft = minutes.text
    minutesRunner?.beginEditing()
  }

  /// During the meeting the next update builds on your version; afterwards it goes straight into the file.
  func finishEditingMinutes() {
    guard let draft = minutesDraft else { return }
    minutesDraft = nil
    if let minutesRunner {
      minutesRunner.finishEditing(text: draft)
    } else if phase != .idle, var state = minutes {
      state.text = draft
      minutes = state
      document?.writeDraft(entries: entries, minutes: draft)
    } else if var f = finished, var state = minutes, draft != state.text {
      f.minutes = draft
      finished = f
      state.text = draft
      minutes = state
      rewriteFinished()
    }
  }

  func cancelEditingMinutes() {
    guard minutesDraft != nil else { return }
    minutesDraft = nil
    minutesRunner?.cancelEditing()
  }

  /// Translates the newest in-progress text at the live update interval (Settings, default 500 ms),
  /// so the live line reads in English while the person is still talking.
  private func startPreviewLoop() {
    previewTask = Task { [weak self] in
      var warnedAboutSilence = false
      while !Task.isCancelled {
        try? await Task.sleep(for: .milliseconds(AppSettings.shared.liveUpdateMilliseconds))
        guard let self, self.phase == .recording, let startedAt = self.startedAt else { continue }
        let elapsed = Int(Date().timeIntervalSince(startedAt))
        if elapsed != self.elapsedSeconds { self.elapsedSeconds = elapsed }
        self.checkAutoStop()
        // Decide mixed-language windows once both recognizers have finalized past them.
        let now = Date().timeIntervalSince(startedAt)
        for (source, arbiter) in self.arbiters {
          var progress: [String: Double] = [:]
          for pipeline in [self.pipelines[source], self.secondaryPipelines[source]].compactMap({ $0 }) {
            progress[pipeline.locale.identifier] = await pipeline.finalizedThrough() ?? now
          }
          for segment in arbiter.flush(progress: progress, horizon: now - 30) { await self.commit(segment) }
        }
        if !warnedAboutSilence, elapsed >= 20, self.tracks[.system]?.hasSignal == false {
          warnedAboutSilence = true
          self.warning = "No meeting audio received yet. If people are talking, allow Kurzschrift under "
            + "System Settings → Privacy & Security → Screen & System Audio Recording, then restart the recording."
        } else if self.warning != nil, self.tracks[.system]?.hasSignal == true {
          self.warning = nil
        }
        guard let translator = self.translator else { continue }
        for (source, text) in self.pendingPreview {
          self.pendingPreview[source] = nil
          let generation = self.previewGeneration[source, default: 0]
          if let translated = await translator.translateUncached(text),
            self.previewGeneration[source, default: 0] == generation, self.phase == .recording,
            self.livePartials[source] != nil
          {
            self.livePartials[source]?.translation = translated
          }
        }
      }
    }
  }

  // MARK: Helpers

  private static func newRecordingFolder() throws -> URL {
    let support = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
    let folder = support.appendingPathComponent("Kurzschrift/Recordings/\(UUID().uuidString)")
    try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
    return folder
  }

  func present(_ error: Error) {
    let alert = NSAlert()
    alert.messageText = error.localizedDescription
    alert.informativeText = (error as? LocalizedError)?.recoverySuggestion ?? ""
    alert.alertStyle = .warning
    if case SessionError.translationNotInstalled = error {
      alert.addButton(withTitle: "Open Translation Settings")
      alert.addButton(withTitle: "Cancel")
      NSApp.activate()
      if alert.runModal() == .alertFirstButtonReturn {
        NSWorkspace.shared.open(URL(string: "x-apple.systempreferences:com.apple.Localization-Settings.extension")!)
      }
      return
    }
    NSApp.activate()
    alert.runModal()
  }
}
