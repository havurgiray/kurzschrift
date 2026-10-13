import AVFoundation
import AppKit
import Foundation
import UniformTypeIdentifiers

enum SessionError: LocalizedError {
  case translationNotInstalled(from: String, to: String)
  case microphoneDenied

  var errorDescription: String? {
    switch self {
    case .translationNotInstalled(let from, let to):
      "The \(from) → \(to) translation languages aren't downloaded yet."
    case .microphoneDenied:
      "Kurzschrift isn't allowed to use the microphone."
    }
  }

  var recoverySuggestion: String? {
    switch self {
    case .translationNotInstalled:
      "Open System Settings → General → Language & Region → Translation Languages… and download both languages."
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
  }

  /// What is being said right now: the recognizer's in-progress text and, when translating, its
  /// latest translation (refreshed at the live update interval, so it trails slightly).
  struct LivePartial: Equatable {
    var original = ""
    var translation: String?
  }

  @Published private(set) var phase: Phase = .idle
  @Published private(set) var config: SessionConfig?
  @Published private(set) var startedAt: Date?
  @Published private(set) var elapsedSeconds = 0
  @Published private(set) var entries: [LiveEntry] = []
  @Published private(set) var livePartials: [AudioSourceKind: LivePartial] = [:]
  /// nil when live minutes are off for this meeting.
  @Published private(set) var minutes: MinutesState?
  @Published private(set) var warning: String?
  @Published private(set) var lastTranscript: URL?

  var isIdle: Bool { phase == .idle }
  var isRecording: Bool { phase == .recording }

  private var tracks: [AudioSourceKind: Track] = [:]
  private var systemSource: SystemAudioSource?
  private var micSource: MicrophoneSource?
  private var fileSource: FileAudioSource?
  private var pipelines: [AudioSourceKind: SpeechPipeline] = [:]
  private var translator: Translator?
  private var segments: [FinalSegment] = []
  private var document: TranscriptDocument?
  private var recordingFolder: URL?
  private var previewTask: Task<Void, Never>?
  private var pendingPreview: [AudioSourceKind: String] = [:]
  private var previewGeneration: [AudioSourceKind: Int] = [:]
  private var minutesRunner: MinutesRunner?

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
    self.config = config
    entries = []
    livePartials = [:]
    segments = []
    warning = AppSettings.shared.minutesProblem(for: mode)
    minutes = nil
    phase = .starting("Preparing speech recognition…")
    if AppSettings.shared.showLiveWindow { LiveWindowController.shared.show() }

    do {
      let locale = try await SpeechPipeline.prepareModel(for: config.spokenLocale)

      if mode == .translate, let target = config.targetLanguage {
        phase = .starting("Preparing translation…")
        guard await Translator.isInstalled(from: locale.language, to: target) else {
          throw SessionError.translationNotInstalled(
            from: AppSettings.languageName(locale.identifier), to: AppSettings.languageName(target.minimalIdentifier))
        }
        let translator = Translator(from: locale.language, to: target)
        _ = await translator.translateUncached("Hallo")  // loads the model before the first real sentence
        self.translator = translator
      }

      if config.includeMic, await AVCaptureDevice.requestAccess(for: .audio) == false {
        throw SessionError.microphoneDenied
      }

      phase = .starting("Starting audio…")
      let sources: [AudioSourceKind] = config.includeMic ? [.system, .microphone] : [.system]
      for source in sources {
        let pipeline = try await SpeechPipeline.make(source: source, locale: locale)
        pipeline.start { [weak self] event in await self?.handle(event) }
        pipelines[source] = pipeline
      }
      if config.separateSpeakers { Diarization.prepareModelsInBackground() }

      let folder = try Self.newRecordingFolder()
      recordingFolder = folder
      let start = Date()
      for source in sources {
        let pipeline = pipelines[source]!
        tracks[source] = try Track(
          kind: source, fileURL: folder.appendingPathComponent("\(source.rawValue).wav"), sessionStart: start
        ) { buffer in pipeline.append(buffer) }
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
      elapsedSeconds = 0
      phase = .recording
      startPreviewLoop()
      if let minutesConfig = config.minutes {
        let runner = MinutesRunner(config: minutesConfig) { [weak self] state in self?.minutesChanged(state) }
        minutesRunner = runner
        runner.start()
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
    previewTask?.cancel()
    systemSource?.stop()
    micSource?.stop()
    fileSource?.stop()
    systemSource = nil
    micSource = nil
    fileSource = nil
    for track in tracks.values { track.close() }
    for pipeline in pipelines.values { await pipeline.finish() }
    pipelines = [:]
    livePartials = [:]
    minutesRunner?.stop()
    let endedAt = Date()
    Log.write("speech finished: \(segments.count) segments")

    var notes: [String] = []
    var speakers: [AudioSourceKind: [SpeakerSpan]] = [:]
    if config.separateSpeakers {
      var toDiarize: [AudioSourceKind] = segments.contains { $0.source == .system } ? [.system] : []
      if config.micShared, segments.contains(where: { $0.source == .microphone }) { toDiarize.append(.microphone) }
      for source in toDiarize {
        guard let track = tracks[source] else { continue }
        phase = .finishing("Identifying speakers…")
        do {
          speakers[source] = try await Diarization.speakers(in: track.fileURL)
          Log.write("diarized \(source): \(speakers[source]?.count ?? 0) spans, \(Set(speakers[source]?.map(\.speaker) ?? []).count) speakers")
        } catch {
          Log.write("diarization failed: \(error)")
          notes.append("Speakers couldn't be identified: \(error.localizedDescription)")
        }
      }
    }

    var turns = TranscriptBuilder.turns(segments: segments, speakers: speakers, config: config)
    if let translator {
      phase = .finishing("Translating transcript…")
      for i in turns.indices {
        var parts: [String] = []
        for piece in turns[i].pieces {
          parts.append(await translator.translate(piece) ?? "")
        }
        turns[i].translation = parts.joined(separator: " ")
      }
    }

    var finalMinutes: String?
    if let minutesRunner {
      phase = .finishing("Writing minutes…")
      let transcript = turns.map { "[\(TranscriptDocument.clock($0.start))] \($0.speaker ?? "Speaker"): \($0.text)" }
        .joined(separator: "\n")
      finalMinutes = await minutesRunner.finalize(transcript: transcript)
      if finalMinutes == nil, let live = minutes?.text, !live.isEmpty {
        finalMinutes = live
        notes.append("The final minutes couldn't be written (\(minutes?.error ?? "unknown error")); these are the last live minutes.")
      }
    }

    var audioFiles: [URL] = []
    if config.keepAudio {
      for (source, track) in tracks.sorted(by: { $0.key.rawValue > $1.key.rawValue }) {
        let name = source == .system ? "meeting audio" : "microphone"
        let target = config.saveFolder.appendingPathComponent("\(document.baseName) (\(name)).wav")
        if (try? FileManager.default.moveItem(at: track.fileURL, to: target)) != nil { audioFiles.append(target) }
      }
    }
    document.writeFinal(turns: turns, endedAt: endedAt, notes: notes, audioFiles: audioFiles, minutes: finalMinutes)
    Log.write("saved \(document.url.lastPathComponent): \(turns.count) turns")
    lastTranscript = document.url
    entries = turns.map {
      LiveEntry(id: UUID(), source: .system, speaker: $0.speaker, start: $0.start, original: $0.text, translation: $0.translation)
    }

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
    for pipeline in pipelines.values { await pipeline.finish() }
    systemSource = nil
    micSource = nil
    pipelines = [:]
    tracks = [:]
    translator = nil
    document = nil
    pendingPreview = [:]
    if let recordingFolder { try? FileManager.default.removeItem(at: recordingFolder) }
    recordingFolder = nil
  }

  // MARK: Live results

  private func handle(_ event: SpeechEvent) async {
    switch event {
    case .partial(let source, let raw):
      let text = TextCleanup.tidy(TextCleanup.splitLeadingPunctuation(raw).rest)
      livePartials[source, default: LivePartial()].original = text
      if translator != nil { pendingPreview[source] = text }

    case .final(var segment):
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
      if let translator { entry.translation = await translator.translate(segment.text) }
      if insert(entry, for: segment) {
        minutesRunner?.add(line: "[\(TranscriptDocument.clock(segment.start))] \(speaker ?? "Speaker"): \(segment.text)")
      }
      document?.writeDraft(entries: entries, minutes: minutes?.text)
    }
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
      if let minutes = minutes?.text, !minutes.isEmpty { lines += ["MINUTES", "", minutes, "", "TRANSCRIPT", ""] }
      for entry in entries {
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
