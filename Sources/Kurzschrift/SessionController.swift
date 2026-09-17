import AVFoundation
import AppKit
import Foundation

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

  @Published private(set) var phase: Phase = .idle
  @Published private(set) var config: SessionConfig?
  @Published private(set) var startedAt: Date?
  @Published private(set) var elapsedSeconds = 0
  @Published private(set) var entries: [LiveEntry] = []
  /// In-progress text per source (already translated in translation mode).
  @Published private(set) var partials: [AudioSourceKind: String] = [:]
  @Published private(set) var warning: String?
  @Published private(set) var lastTranscript: URL?

  var isIdle: Bool { phase == .idle }
  var isRecording: Bool { phase == .recording }

  private var tracks: [AudioSourceKind: Track] = [:]
  private var systemSource: SystemAudioSource?
  private var micSource: MicrophoneSource?
  private var pipelines: [AudioSourceKind: SpeechPipeline] = [:]
  private var translator: Translator?
  private var segments: [FinalSegment] = []
  private var document: TranscriptDocument?
  private var recordingFolder: URL?
  private var previewTask: Task<Void, Never>?
  private var pendingPreview: [AudioSourceKind: String] = [:]
  private var previewGeneration: [AudioSourceKind: Int] = [:]

  // MARK: Start

  func start(_ mode: MeetingMode) async {
    guard phase == .idle else {
      Log.write("start(\(mode)) ignored: phase is \(phase)")
      return
    }
    Log.write("start(\(mode))")
    let config = AppSettings.shared.snapshot(for: mode)
    self.config = config
    entries = []
    partials = [:]
    segments = []
    warning = nil
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
      let system = SystemAudioSource(track: tracks[.system]!)
      try system.start()
      systemSource = system
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
    systemSource = nil
    micSource = nil
    for track in tracks.values { track.close() }
    for pipeline in pipelines.values { await pipeline.finish() }
    pipelines = [:]
    partials = [:]
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

    var audioFiles: [URL] = []
    if config.keepAudio {
      for (source, track) in tracks.sorted(by: { $0.key.rawValue > $1.key.rawValue }) {
        let name = source == .system ? "meeting audio" : "microphone"
        let target = config.saveFolder.appendingPathComponent("\(document.baseName) (\(name)).wav")
        if (try? FileManager.default.moveItem(at: track.fileURL, to: target)) != nil { audioFiles.append(target) }
      }
    }
    document.writeFinal(turns: turns, endedAt: endedAt, notes: notes, audioFiles: audioFiles)
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
    systemSource?.stop()
    micSource?.stop()
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
    case .partial(let source, let text):
      if translator != nil {
        pendingPreview[source] = text
      } else {
        partials[source] = text
      }

    case .final(let segment):
      Log.write("final \(segment.source) \(String(format: "%.1f-%.1f", segment.start, segment.end)): \(segment.text.count) chars")
      previewGeneration[segment.source, default: 0] += 1
      pendingPreview[segment.source] = nil
      partials[segment.source] = nil
      segments.append(segment)
      let speaker = config?.includeMic == true ? (segment.source == .microphone ? config?.myName : "Others") : nil
      var entry = LiveEntry(
        id: segment.id, source: segment.source, speaker: speaker, start: segment.start, original: segment.text)
      if let translator { entry.translation = await translator.translate(segment.text) }
      insert(entry, for: segment)
      document?.writeDraft(entries: entries)
    }
  }

  private func insert(_ entry: LiveEntry, for segment: FinalSegment) {
    // Hide microphone lines that are just loudspeaker echo of what someone else said.
    let system = segments.filter { $0.source == .system }
    if segment.source == .microphone, TranscriptBuilder.isEcho(segment, among: system) {
      return
    }
    if segment.source == .system {
      let echoes = Set(
        segments.filter { $0.source == .microphone && TranscriptBuilder.isEcho($0, among: system) }.map(\.id))
      entries.removeAll { echoes.contains($0.id) }
    }
    let index = entries.lastIndex { $0.start <= entry.start }.map { $0 + 1 } ?? 0
    entries.insert(entry, at: index)
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
            self.previewGeneration[source, default: 0] == generation, self.phase == .recording
          {
            self.partials[source] = translated
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
