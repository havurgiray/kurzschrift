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

/// For two tasks racing to answer one continuation: only the first claim wins.
private final class Once: @unchecked Sendable {
  private let lock = NSLock()
  private var claimed = false
  func claim() -> Bool {
    lock.withLock {
      if claimed { return false }
      claimed = true
      return true
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
    var notes: [String]
    var audioFiles: [URL]
    var minutes: String?
    let bookmarks: [Double]
    let config: SessionConfig
    /// Speaker name → voice embedding, for "remember this voice" when renaming.
    var voices: [String: [Float]]
    /// The final minutes (or minutes asked for afterwards) couldn't be written, so they can be tried again.
    var minutesFailed = false
    /// Names whose voice was remembered from this meeting, each with the voice library's entry
    /// as it was before (nil: the name was new), so correcting the name takes that back.
    var remembered: [String: RememberedVoice] = [:]
  }

  struct RememberedVoice {
    let previous: VoiceLibrary.Voice?
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
  /// New for every meeting, so the meeting window builds a fresh transcript list instead of
  /// reusing the previous meeting's.
  @Published private(set) var meetingID = UUID()
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
  /// The Context tab: who's in the meeting and what it's about. Applied a second after you stop typing.
  @Published var context = MeetingContext() {
    didSet { if context != oldValue { contextChanged() } }
  }
  /// The context the finished meeting's minutes were written with, so they can be redone after a change.
  @Published private(set) var minutesContext: MeetingContext?

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
  private var contextTask: Task<Void, Never>?
  /// A Context change that hasn't been applied yet (it waits a second after the last keystroke).
  private var contextPending = false
  /// The context's names the recognizers have been given.
  private var recognizerNames: [String] = []
  /// What the user should know about the meeting, each kind shown on its own line of `warning`.
  /// They stay after the meeting and go when the next one starts.
  private enum WarningKind: Int {
    /// Minutes are on but there's no key (cleared when AI minutes are switched on).
    case minutes
    /// No meeting audio is arriving.
    case audio
    case microphone
    case recognition
    /// The transcript file can't be written right now.
    case writing
    /// At the end: the transcript or the recording couldn't be saved where it belongs.
    case saving
  }
  private var warnings: [WarningKind: String] = [:]
  /// Sources whose Apple recognizer ended with an error; Parakeet's lines count as speech there.
  private var failedRecognition: Set<AudioSourceKind> = []
  /// The draft file is written at most every few seconds: when it last was, and the write that is waiting.
  private var lastDraftWrite = Date.distantPast
  private var draftTask: Task<Void, Never>?
  /// The final transcript has been written: a draft must not be written over it any more.
  private var finalWritten = false
  /// The meeting has stopped listening: a recognizer result that still arrives is ignored.
  private var speechClosed = false
  private var lastSpeechAt = Date()
  /// Session times (seconds) the user marked with ⌃⌥B or the ⭐ button.
  private var bookmarks: [Double] = []

  /// Parakeet re-recognition (Parakeet.swift): the model, each track's audio until it has been
  /// redone, how far each track has been redone, and the stretch being redone right now.
  private enum ParakeetState { case off, loading, ready, failed }
  private var parakeetState = ParakeetState.off
  /// Apple's recognizer isn't available (seen under Rosetta, likely on Intel Macs): Parakeet alone
  /// recognizes the meeting, a stretch every ~10 s, like the Windows version.
  private var parakeetOnly = false
  private var parakeet: ParakeetRecognizer?
  private var histories: [AudioSourceKind: AudioHistory] = [:]
  private var refinedUntil: [AudioSourceKind: Double] = [:]
  private var refining: [AudioSourceKind: Task<Void, Never>] = [:]
  private var refinedIDs: Set<UUID> = []
  /// Apple's lines for the minutes while Parakeet is loading: dropped once it's ready (its own lines
  /// take their place), passed on if it can't be loaded.
  private var heldMinutesLines: [String] = []
  /// Changes with every meeting, so work finishing late can't touch the next one.
  private var sessionID = UUID()

  // MARK: Start

  /// `testAudio` replays a recording instead of capturing system audio (no microphone), for testing.
  /// `secondLanguage` starts the second Transcribe entry (see `AppSettings.secondTranscribe`).
  func start(_ mode: MeetingMode, secondLanguage: Bool = false, testAudio: URL? = nil) async {
    guard phase == .idle else {
      Log.write("start(\(mode)) ignored: phase is \(phase)")
      return
    }
    Log.write("start(\(mode)\(secondLanguage ? ", second language" : ""))")
    commitPendingEdits()  // the last meeting's unfinished minutes edit and Context changes go into its file first
    var config = AppSettings.shared.snapshot(for: mode, secondLanguage: secondLanguage)
    if testAudio != nil { config.includeMic = false }
    if AppSettings.shared.useCalendar, testAudio == nil {
      phase = .starting("Checking the calendar…")
      if let meeting = await CalendarContext.currentMeeting() {
        config.eventTitle = meeting.title
        config.attendees = meeting.attendees
      }
    }
    self.config = config
    // The invite's attendees go to the minutes as participants, which you can correct in the Context tab.
    context = MeetingContext(attendees: config.attendees)
    contextTask?.cancel()
    contextPending = false
    minutesContext = nil
    meetingID = UUID()
    entries = []
    livePartials = [:]
    segments = []
    bookmarks = []
    questions = []
    finished = nil
    warnings = [:]
    warning = nil
    setWarning(.minutes, AppSettings.shared.minutesProblem(for: mode))
    minutes = nil
    minutesDraft = nil
    phase = .starting("Preparing speech recognition…")
    if AppSettings.shared.showLiveWindow { LiveWindowController.shared.show() }

    do {
      let locale: Locale
      do {
        // Hidden testing default: `defaults write local.kurzschrift forceParakeetOnly -bool true`.
        if UserDefaults.standard.bool(forKey: "forceParakeetOnly") { throw SpeechError.unavailable }
        locale = try await SpeechPipeline.prepareModel(for: config.spokenLocale)
      } catch SpeechError.unavailable {
        parakeetOnly = true
        locale = config.spokenLocale
        config.parakeet = true
        self.config?.parakeet = true
        let forced = UserDefaults.standard.bool(forKey: "forceParakeetOnly")
        Log.write("Apple speech recognition unavailable\(forced ? " (forced for testing)" : ""): using Parakeet alone")
      }

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

      if config.includeMic, AudioDevices.inputDevices().isEmpty {
        config.includeMic = false
        continueWithoutMicrophone()
      }
      if config.includeMic, await AVCaptureDevice.requestAccess(for: .audio) == false {
        throw SessionError.microphoneDenied
      }

      var secondLocale: Locale?
      if !parakeetOnly, let wanted = config.secondLocale, !LanguageArbiter.sameLanguage(wanted.identifier, locale.identifier) {
        phase = .starting("Preparing \(AppSettings.languageName(wanted.identifier)) recognition…")
        secondLocale = try? await SpeechPipeline.prepareModel(for: wanted)
      }
      self.config?.secondLocale = secondLocale

      phase = .starting("Starting audio…")
      let sources: [AudioSourceKind] = config.includeMic ? [.system, .microphone] : [.system]
      sessionID = UUID()
      let session = sessionID
      speechClosed = false
      finalWritten = false
      if config.parakeet {
        for source in sources { histories[source] = AudioHistory() }
        loadParakeet()
      }
      recognizerNames = context.names
      let vocabulary = Self.vocabulary(config.vocabulary, recognizerNames)
      for source in sources where !parakeetOnly {
        let pipeline = try await SpeechPipeline.make(source: source, locale: locale, vocabulary: vocabulary)
        pipeline.start(
          onEvent: { [weak self] event in await self?.handle(event, session: session) },
          onFailure: { [weak self] _ in await self?.recognitionFailed(source, secondLanguage: false) })
        pipelines[source] = pipeline
        if let secondLocale {
          let second = try await SpeechPipeline.make(source: source, locale: secondLocale, vocabulary: vocabulary)
          second.start(
            onEvent: { [weak self] event in await self?.handle(event, session: session) },
            onFailure: { [weak self] _ in await self?.recognitionFailed(source, secondLanguage: true) })
          secondaryPipelines[source] = second
          arbiters[source] = LanguageArbiter(primary: locale.identifier, secondary: secondLocale.identifier)
        }
      }
      if config.separateSpeakers { Diarization.prepareModelsInBackground() }

      let folder = try Self.newRecordingFolder()
      recordingFolder = folder
      let start = Date()
      for source in sources {
        let pipeline = pipelines[source]
        let second = secondaryPipelines[source]
        let history = histories[source]
        tracks[source] = try Track(
          kind: source, fileURL: folder.appendingPathComponent("\(source.rawValue).wav"), sessionStart: start
        ) { buffer in
          pipeline?.append(buffer)
          second?.append(buffer)
          history?.append(buffer)
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
        do {
          try mic.start()
          mic.onFailure = { [weak self] in self?.microphoneFailed() }
          micSource = mic
        } catch CaptureError.noMicrophone {
          // Better the meeting audio alone than no recording at all.
          config.includeMic = false
          continueWithoutMicrophone()
          micTrack.close()
          tracks[.microphone] = nil
          try? FileManager.default.removeItem(at: micTrack.fileURL)
          let unused = [pipelines.removeValue(forKey: .microphone), secondaryPipelines.removeValue(forKey: .microphone)]
          for pipeline in unused.compactMap({ $0 }) { await pipeline.finish() }
          arbiters[.microphone] = nil
          histories[.microphone] = nil
        }
      }

      let document = try TranscriptDocument(config: config, startedAt: start)
      self.document = document
      document.context = context
      // What a later launch needs to put this recording next to its transcript if the meeting is never saved.
      MeetingSidecar(
        transcript: document.url.path, mode: mode.rawValue, spokenLocale: locale.identifier(.bcp47),
        secondLocale: secondLocale?.identifier(.bcp47), secondLanguageEntry: secondLanguage, startedAt: start,
        includesMicrophone: config.includeMic
      ).write(to: folder)
      lastDraftWrite = Date()
      noteWritten(document.writeDraft(entries: []), document)
      startedAt = start
      lastSpeechAt = start
      autoStopIn = nil
      elapsedSeconds = 0
      phase = .recording
      startPreviewLoop()
      if let minutesConfig = config.minutes {
        let runner = MinutesRunner(config: minutesConfig) { [weak self] state in
          // Not once another meeting has started (an update can still be on its way then).
          guard let self, self.isCurrent(document) else { return }
          self.minutesChanged(state)
        }
        runner.meetingContext = context.promptText
        minutesRunner = runner
        runner.start()
      } else {
        // No AI minutes: the pane is for your own notes, saved with the transcript.
        minutes = MinutesState(usesAI: false, model: "", provider: AppSettings.shared.provider)
      }
      contextChanged()  // picks up anything typed into the Context tab while starting
      Log.write("recording: sources=\(sources.filter { tracks[$0] != nil }.map(\.rawValue)) locale=\(locale.identifier)")
    } catch {
      Log.write("start failed: \(error)")
      await tearDown()
      warnings = [:]
      warning = nil
      phase = .idle
      self.config = nil
      present(error)
    }
  }

  /// There's no microphone to record from: the meeting goes on with the meeting audio alone.
  private func continueWithoutMicrophone() {
    config?.includeMic = false
    setWarning(.microphone, "No usable microphone was found. Only the meeting audio is being recorded.")
    Log.write("no usable microphone: recording the meeting audio only")
  }

  /// The microphone couldn't be started again after a device change (second try included).
  private func microphoneFailed() {
    guard isRecording else { return }
    setWarning(
      .microphone,
      "The microphone stopped after a device change and couldn't be started again. The meeting audio is still "
        + "recorded; stop and start the recording to include your microphone again.")
  }

  /// A recognizer's results ended with an error mid-meeting: nothing more comes from it.
  private func recognitionFailed(_ source: AudioSourceKind, secondLanguage: Bool) {
    guard isRecording else { return }
    let what = source == .system ? "the meeting audio" : "your microphone"
    if secondLanguage {
      setWarning(.recognition, "Recognition of the second language stopped for \(what). The meeting goes on in the first language only.")
      return
    }
    failedRecognition.insert(source)
    let parakeetCarriesOn = config?.parakeet == true && (parakeetState == .ready || parakeetState == .loading)
    setWarning(
      .recognition,
      parakeetCarriesOn
        ? "Live speech recognition stopped for \(what). The recording goes on, and the transcript continues about a minute behind."
        : "Speech recognition stopped for \(what). The recording goes on, but nothing more is written down from it. "
          + "Stop and start the recording to continue the transcript.")
  }

  private func setWarning(_ kind: WarningKind, _ text: String?) {
    guard warnings[kind] != text else { return }
    warnings[kind] = text
    let lines = warnings.sorted { $0.key.rawValue < $1.key.rawValue }.map(\.value)
    warning = lines.isEmpty ? nil : lines.joined(separator: "\n")
  }

  /// Whether `document` belongs to the meeting that is running or the one just finished: what
  /// arrives late for an earlier meeting is dropped.
  private func isCurrent(_ document: TranscriptDocument) -> Bool {
    self.document === document || finished?.document === document
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
    // Every wait from here on has a limit that grows with the length of the recording, so a
    // meeting is always saved and the app always gets back to idle, whatever hangs.
    let audioSeconds = tracks.values.map(\.duration).max() ?? 0
    for pipeline in Array(pipelines.values) + Array(secondaryPipelines.values) {
      if await Self.finishes(within: 60 + audioSeconds / 20, { await pipeline.finish() }) == false {
        Log.write("\(pipeline.source) speech recognition didn't finish in time; going on without its last words")
      }
    }
    for arbiter in arbiters.values {
      for segment in arbiter.flushAll() { await commit(segment) }
    }
    speechClosed = true
    pipelines = [:]
    secondaryPipelines = [:]
    arbiters = [:]
    livePartials = [:]
    minutesRunner?.stop()
    let endedAt = Date()
    writeDraftNow()  // everything recognized is in the file before the slow steps start
    var notes: [String] = []
    /// The recording folder stays (instead of being deleted with the meeting) when something
    /// couldn't be saved where it belongs.
    var keepRecordingFolder = false
    if parakeetOnly, parakeetState == .loading {
      let deadline = Date().addingTimeInterval(min(600, 60 + audioSeconds / 4))
      while parakeetState == .loading, Date() < deadline { try? await Task.sleep(for: .milliseconds(200)) }
    }
    Log.write("speech finished: \(segments.count) segments")
    if parakeetState == .ready {
      phase = .finishing("Improving the transcript…")
      let limit = 120 + audioSeconds / 2
      for source in Array(histories.keys) {
        // The stretch being redone can start the next one when it ends: wait for all of them
        // before the last stretch, or that one is never done.
        guard await waitForRefining(source, limit: limit) else { continue }
        refine(source, final: true)
        _ = await waitForRefining(source, limit: limit)
      }
    } else if parakeetState == .loading {
      // Not loaded in time. If it finishes loading now, it must not start replacing lines while
      // the meeting is being saved.
      parakeetState = .off
      for history in histories.values { history.disable() }
      Log.write("parakeet still loading at the end; the transcript stays as recognized live")
      if parakeetOnly {
        notes.append(
          "Speech recognition (Parakeet) wasn't loaded in time, so nothing was written down. The recording can be "
            + "transcribed later (Troubleshooting in the README: --reprocess).")
        if !config.keepAudio { keepRecordingFolder = true }
      }
    }

    var speakers: [AudioSourceKind: [SpeakerSpan]] = [:]
    var speakerVoices: [String: [Float]] = [:]
    if config.separateSpeakers {
      var toDiarize: [AudioSourceKind] = segments.contains { $0.source == .system } ? [.system] : []
      if config.micShared, segments.contains(where: { $0.source == .microphone }) { toDiarize.append(.microphone) }
      for source in toDiarize {
        guard let track = tracks[source] else { continue }
        phase = .finishing("Identifying speakers…")
        do {
          let result = try await Diarization.speakers(
            in: track.fileURL, timeout: Diarization.timeout(forAudioSeconds: track.duration))
          speakers[source] = result.spans
          for (id, embedding) in result.voices { speakerVoices["\(source.rawValue):\(id)"] = embedding }
          Log.write("diarized \(source): \(speakers[source]?.count ?? 0) spans, \(Set(speakers[source]?.map(\.speaker) ?? []).count) speakers")
        } catch {
          Log.write("diarization failed: \(error)")
          notes.append("Speakers couldn't be identified: \(error.localizedDescription)")
        }
      }
    }

    let sameSpeakers = Diarization.sameVoices(speakerVoices)
    if !sameSpeakers.isEmpty { Log.write("\(sameSpeakers.count) microphone speaker(s) are people in the meeting audio") }
    let known = AppSettings.shared.recognizeVoices
      ? VoiceLibrary.match(speakerVoices.filter { sameSpeakers[$0.key] == nil }) : [:]
    if !known.isEmpty { Log.write("recognized \(known.count) known voice(s)") }
    let built = TranscriptBuilder.build(
      segments: segments, speakers: speakers, config: config, knownNames: known, sameSpeakers: sameSpeakers,
      bookmarks: bookmarks)
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

    // The transcript is written now, with the minutes as they stand (the live ones, or your
    // notes), and again as the final minutes and the recording arrive: whatever goes wrong or
    // hangs after this point, the finished transcript is on disk.
    contextTask?.cancel()
    contextPending = false
    document.context = context
    draftTask?.cancel()
    draftTask = nil
    finalWritten = true
    let liveMinutes = minutes?.text ?? ""
    var result = FinishedMeeting(
      document: document, turns: turns, endedAt: endedAt, notes: notes, audioFiles: [],
      minutes: liveMinutes.isEmpty ? nil : liveMinutes, bookmarks: bookmarks, config: config, voices: voicesByName)
    var saved = writeFinal(result)

    if let minutesRunner {
      phase = .finishing("Writing minutes…")
      let final = await minutesRunner.finalize(transcript: Self.minutesTranscript(turns: turns, bookmarks: bookmarks))
      minutesContext = context
      if let final {
        result.minutes = final
      } else if !turns.isEmpty {
        result.minutesFailed = true
        result.notes.append(
          "\(Self.minutesFailedNote) (\(minutes?.error ?? "unknown error"))"
            + (result.minutes == nil ? "." : "; these are the last live minutes."))
      }
      saved = writeFinal(result)
    }

    var audioFailed = false
    if config.keepAudio {
      phase = .finishing("Saving the recording…")
      for (source, track) in tracks.sorted(by: { $0.key.rawValue > $1.key.rawValue }) {
        let name = source == .system ? "meeting audio" : "microphone"
        let base = config.saveFolder.appendingPathComponent("\(document.baseName) (\(name))")
        if let kept = await KeptRecording.save(track.fileURL, as: base, kbps: config.audioBitrate) {
          result.audioFiles.append(kept)
        } else {
          audioFailed = true
        }
      }
      document.context = context
      if !result.audioFiles.isEmpty { saved = writeFinal(result) }
    }

    if !saved || audioFailed || keepRecordingFolder, let recordingFolder {
      // The audio stays where it was recorded; the next launch moves it next to the transcripts.
      keepRecordingFolder = true
      let place = (recordingFolder.path as NSString).abbreviatingWithTildeInPath
      let folder = (config.saveFolder.path as NSString).abbreviatingWithTildeInPath
      let what = !saved
        ? "The transcript couldn't be saved to “\(folder)” (\(document.writeError ?? "unknown error"))."
        : audioFailed ? "The recording couldn't be saved to “\(folder)”." : "The meeting has no transcript yet."
      setWarning(
        .saving, "\(what) The recording is kept in “\(place)”; Kurzschrift moves it to your transcripts when it next starts.")
      Log.write("the recording folder \(recordingFolder.lastPathComponent) is kept: transcript saved=\(saved), recording failed=\(audioFailed)")
    }
    if saved { setWarning(.writing, nil) }  // a draft that couldn't be written earlier no longer matters
    finished = result
    Log.write("\(saved ? "saved" : "NOT saved") \(document.url.lastPathComponent): \(turns.count) turns")
    lastTranscript = document.url
    entries = turns.map {
      LiveEntry(id: UUID(), source: .system, speaker: $0.speaker, start: $0.start, original: $0.text, translation: $0.translation)
    } + bookmarks.map(Self.bookmarkEntry)
    entries.sort { $0.start < $1.start }

    await tearDown(keepRecording: keepRecordingFolder)
    phase = .idle
  }

  /// How the note about final minutes that couldn't be written starts (it goes when a retry works).
  private static let minutesFailedNote = "The final minutes couldn't be written"

  /// Waits for the stretch Parakeet is redoing on `source` and any it starts right after it, at
  /// most `limit` seconds. False when that wasn't enough: whatever it still finishes is then ignored.
  private func waitForRefining(_ source: AudioSourceKind, limit: Double) async -> Bool {
    let deadline = Date().addingTimeInterval(limit)
    while let task = refining[source] {
      let done = await Self.finishes(within: max(1, deadline.timeIntervalSinceNow)) { await task.value }
      guard done else {
        Log.write("parakeet didn't finish \(source) in time; the transcript keeps what is there")
        sessionID = UUID()
        return false
      }
      if refining[source] == task { refining[source] = nil }
    }
    return true
  }

  /// Runs `work` and returns true, or false when it hasn't finished after `seconds` (it is then
  /// left to finish, or to hang, by itself).
  private static func finishes(within seconds: Double, _ work: @escaping @Sendable () async -> Void) async -> Bool {
    let once = Once()
    return await withCheckedContinuation { (continuation: CheckedContinuation<Bool, Never>) in
      let timer = Task {
        do { try await Task.sleep(for: .seconds(seconds)) } catch { return }
        if once.claim() { continuation.resume(returning: false) }
      }
      Task {
        await work()
        timer.cancel()
        if once.claim() { continuation.resume(returning: true) }
      }
    }
  }

  /// `keepRecording` leaves the recording folder in place (something couldn't be saved).
  private func tearDown(keepRecording: Bool = false) async {
    previewTask?.cancel()
    draftTask?.cancel()
    draftTask = nil
    failedRecognition = []
    minutesRunner?.stop()
    minutesRunner = nil
    systemSource?.stop()
    micSource?.stop()
    fileSource?.stop()
    fileSource = nil
    for track in tracks.values { track.close() }
    speechClosed = true
    for pipeline in Array(pipelines.values) + Array(secondaryPipelines.values) {
      _ = await Self.finishes(within: 30) { await pipeline.finish() }
    }
    systemSource = nil
    micSource = nil
    pipelines = [:]
    secondaryPipelines = [:]
    arbiters = [:]
    tracks = [:]
    translator = nil
    document = nil
    pendingPreview = [:]
    sessionID = UUID()
    parakeet = nil
    parakeetState = .off
    parakeetOnly = false
    histories = [:]
    refinedUntil = [:]
    refining = [:]
    refinedIDs = []
    heldMinutesLines = []
    if let recordingFolder, !keepRecording { try? FileManager.default.removeItem(at: recordingFolder) }
    recordingFolder = nil
  }

  // MARK: Live results

  /// `session` is the meeting the recognizer belongs to: a result that arrives after its meeting
  /// (from a recognizer that didn't finish in time) is dropped.
  private func handle(_ event: SpeechEvent, session: UUID) async {
    guard session == sessionID, !speechClosed else { return }
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
    // Apple's version of a stretch Parakeet has already redone arrives too late to matter.
    if let until = refinedUntil[segment.source], (segment.start + segment.end) / 2 < until { return }
    Log.write("final \(segment.source) \(String(format: "%.1f-%.1f", segment.start, segment.end)): \(segment.text.count) chars")
    segments.append(segment)
    let id = sessionID
    let entry = await liveEntry(for: segment)
    // While this line was being translated, the meeting may have ended, or Parakeet may have
    // redone the stretch and taken the line out again: then it mustn't be put (back) in.
    guard id == sessionID, segments.contains(where: { $0.id == segment.id }) else { return }
    if insert(entry, for: segment) {
      let line = Self.minutesLine(segment, speaker: entry.speaker)
      switch parakeetState {
      case .off, .failed: minutesRunner?.add(line: line)
      case .loading: heldMinutesLines.append(line)
      case .ready: break  // Parakeet's version of this stretch goes to the minutes
      }
    }
    writeDraftSoon()
    refine(segment.source)
  }

  /// The transcript line for a finished segment: who (when both sources are on) and the translation.
  private func liveEntry(for segment: FinalSegment) async -> LiveEntry {
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
    return entry
  }

  private static func minutesLine(_ segment: FinalSegment, speaker: String?) -> String {
    "[\(TranscriptDocument.clock(segment.start))] \(speaker ?? "Speaker"): \(segment.text)"
  }

  // MARK: Parakeet

  private func loadParakeet() {
    parakeetState = .loading
    let id = sessionID
    Task {
      do {
        let started = Date()
        let recognizer = try await ParakeetRecognizer.load()
        // Not for a later meeting, and not once this one has stopped waiting for it.
        guard id == sessionID, parakeetState == .loading else { return }
        parakeet = recognizer
        parakeetState = .ready
        heldMinutesLines = []
        Log.write("parakeet ready after \(Int(Date().timeIntervalSince(started))) s")
        for source in Array(histories.keys) { refine(source) }
      } catch {
        guard id == sessionID, parakeetState == .loading else { return }
        parakeetState = .failed
        for history in histories.values { history.disable() }
        for line in heldMinutesLines { minutesRunner?.add(line: line) }
        heldMinutesLines = []
        Log.write("parakeet unavailable: \(error)")
        if parakeetOnly { setWarning(.recognition, "Speech recognition couldn't be loaded: \(error.localizedDescription)") }
      }
    }
  }

  /// Hands the next stretch of a track to Parakeet once Apple has finished 15 s of it (the length
  /// at which Parakeet is as accurate as on a whole recording), cut at the end of a sentence. With
  /// `final`, the rest of the track after the meeting.
  private func refine(_ source: AudioSourceKind, final: Bool = false) {
    guard parakeetState == .ready, refining[source] == nil, let parakeet, let history = histories[source] else { return }
    let from = refinedUntil[source] ?? 0
    var to = history.duration
    if !final, parakeetOnly {
      // No Apple lines to follow: every ~10 s, cut at the quietest moment of the last few seconds.
      guard to - from >= 10 else { return }
      to = Self.quietPoint(history, from: max(from + 6, to - 4), to: to - 0.3) ?? to
    } else if !final {
      let lastEnd = segments.filter({ $0.source == source && !refinedIDs.contains($0.id) }).map(\.end).max()
      if let lastEnd, lastEnd - from >= 15 {
        to = min(lastEnd + 0.2, to)
      } else if to - from >= Self.unheardSeconds + Self.unheardMargin {
        // Apple's recognizer has written next to nothing here for a while: silence (a microphone
        // nobody speaks into), speech it missed, or it has stopped. The stretch is redone anyway,
        // up to a quiet moment well behind what is being said now, so its audio doesn't pile up
        // in memory until the end of the meeting.
        let limit = to - Self.unheardMargin
        to = Self.quietPoint(history, from: max(from + 6, limit - 4), to: limit) ?? limit
      } else {
        return
      }
    }
    guard to - from >= 0.5 else { return }
    let samples = history.samples(from: from, to: to)
    let id = sessionID
    refining[source] = Task {
      do {
        let words = try await parakeet.recognize(samples, offset: from)
        guard id == sessionID else { return }
        await replaceWithRefined(words, source: source, from: from, to: to, session: id)
        guard id == sessionID else { return }
      } catch {
        guard id == sessionID else { return }
        // Keep Apple's lines for this stretch, and let the minutes have them after all.
        Log.write("parakeet failed for \(source) \(Int(from))-\(Int(to)) s: \(error)")
        refinedUntil[source] = to
        for segment in segments
        where segment.source == source && !refinedIDs.contains(segment.id) && (segment.start + segment.end) / 2 < to {
          refinedIDs.insert(segment.id)
          if entries.contains(where: { $0.id == segment.id }) {
            minutesRunner?.add(line: Self.minutesLine(segment, speaker: entries.first { $0.id == segment.id }?.speaker))
          }
        }
      }
      history.discard(before: to)
      refining[source] = nil
      if !final { refine(source) }
    }
  }

  /// A stretch without lines from Apple's recognizer is redone once it is this long, leaving
  /// this much before the live edge (Apple's lines can come that late in mixed-language meetings).
  private static let unheardSeconds = 60.0
  private static let unheardMargin = 30.0

  /// The middle of the quietest 100 ms between `from` and `to` (a pause between words), on the track's timeline.
  private static func quietPoint(_ history: AudioHistory, from: Double, to: Double) -> Double? {
    guard to - from > 0.2 else { return nil }
    let samples = history.samples(from: from, to: to)
    let window = Int(sessionAudioFormat.sampleRate / 10)
    guard samples.count > window else { return nil }
    var best = (energy: Float.infinity, index: 0)
    var i = 0
    while i + window <= samples.count {
      var energy: Float = 0
      for k in i..<(i + window) { energy += samples[k] * samples[k] }
      if energy < best.energy { best = (energy, i) }
      i += window / 2
    }
    return from + Double(best.index + window / 2) / sessionAudioFormat.sampleRate
  }

  /// Swaps Apple's lines up to `to` for Parakeet's sentences, in the window, the draft file and the minutes.
  private func replaceWithRefined(
    _ words: [TimedWord], source: AudioSourceKind, from: Double, to: Double, session: UUID
  ) async {
    refinedUntil[source] = to
    let replaced = segments.filter {
      $0.source == source && !refinedIDs.contains($0.id) && ($0.start + $0.end) / 2 < to
    }
    let ids = Set(replaced.map(\.id))
    segments.removeAll { ids.contains($0.id) }
    entries.removeAll { ids.contains($0.id) }
    let spoken = config?.spokenLocale.identifier ?? ""
    let languages = [spoken] + [config?.secondLocale?.identifier, config?.targetLanguage?.minimalIdentifier].compactMap { $0 }
    let refined = ParakeetRecognizer.segments(words, source: source, languages: languages) { start, end in
      // The language Apple heard there, from the line overlapping it most.
      func overlap(_ s: FinalSegment) -> Double { max(0, min(end, s.end) - max(start, s.start)) }
      return replaced.max { overlap($0) < overlap($1) }.map { overlap($0) > 0 ? $0.locale : spoken } ?? spoken
    }
    // All of Parakeet's sentences go into `segments` at once, so there is no moment (the
    // translations below take a while) in which the stretch is missing from the transcript.
    for segment in refined {
      refinedIDs.insert(segment.id)
      segments.append(segment)
    }
    for segment in refined {
      let entry = await liveEntry(for: segment)
      guard session == sessionID else { return }  // the meeting ended meanwhile
      if insert(entry, for: segment) {
        minutesRunner?.add(line: Self.minutesLine(segment, speaker: entry.speaker))
      }
    }
    if parakeetOnly || failedRecognition.contains(source), !refined.isEmpty {
      lastSpeechAt = Date()  // there are no Apple results to count as speech
      autoStopIn = nil
    }
    writeDraftSoon()
    Log.write("parakeet \(source) \(Int(from))-\(Int(to)) s: \(replaced.count) lines became \(refined.count)")
  }

  /// Returns false when the line was dropped as loudspeaker echo.
  @discardableResult
  private func insert(_ entry: LiveEntry, for segment: FinalSegment) -> Bool {
    // Hide microphone lines that are just loudspeaker echo of what someone else said. An echo
    // comes at the same moment as the original, so only lines around this one are compared:
    // the meeting audio within a minute, and the microphone lines of the last 30 s before it
    // (comparing the whole meeting for every new line got slower the longer it ran).
    let system = segments.filter {
      $0.source == .system && $0.end > segment.start - 60 && $0.start < segment.end + 60
    }
    if segment.source == .microphone, TranscriptBuilder.isEcho(segment, among: system) {
      return false
    }
    if segment.source == .system {
      let echoes = Set(
        segments.filter {
          $0.source == .microphone && $0.end > segment.start - 30 && $0.start < segment.end + 30
            && TranscriptBuilder.isEcho($0, among: system)
        }.map(\.id))
      if !echoes.isEmpty { entries.removeAll { echoes.contains($0.id) } }
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
    noteWritten(writeFinal(f), f.document)
  }

  /// Writes a meeting's final file; false when that failed.
  private func writeFinal(_ f: FinishedMeeting) -> Bool {
    let qa = questions.compactMap { item in item.answer.map { (question: item.question, answer: $0) } }
    return f.document.writeFinal(
      turns: f.turns, endedAt: f.endedAt, notes: f.notes, audioFiles: f.audioFiles, minutes: f.minutes,
      bookmarks: f.bookmarks, qa: qa)
  }

  /// Says so in the window while the transcript file can't be written, and stops saying it once it can.
  private func noteWritten(_ written: Bool, _ document: TranscriptDocument) {
    let folder = (document.url.deletingLastPathComponent().path as NSString).abbreviatingWithTildeInPath
    setWarning(
      .writing,
      written
        ? nil
        : "The transcript can't be written to “\(folder)” (\(document.writeError ?? "unknown error")). "
          + (isIdle ? "Your last change isn't in the file." : "The recording goes on."))
  }

  /// The draft file with everything so far, written at most every 3 s: rewriting the whole file
  /// for every line costs more the longer the meeting gets (and more still in a synced folder).
  private func writeDraftSoon() {
    guard document != nil, !finalWritten else { return }
    let wait = 3 - Date().timeIntervalSince(lastDraftWrite)
    guard wait > 0 else { return writeDraftNow() }
    guard draftTask == nil else { return }
    draftTask = Task { [weak self] in
      try? await Task.sleep(for: .seconds(wait))
      guard !Task.isCancelled, let self else { return }
      self.draftTask = nil
      self.writeDraftNow()
    }
  }

  /// Writes the draft right away, also one that was waiting (at stop).
  private func writeDraftNow() {
    draftTask?.cancel()
    draftTask = nil
    guard let document, !finalWritten else { return }
    lastDraftWrite = Date()
    noteWritten(document.writeDraft(entries: entries, minutes: minutes?.text), document)
  }

  /// Renames a speaker throughout the finished meeting (file, window, minutes) and, if asked,
  /// remembers the voice so the name is used automatically next time.
  func renameSpeaker(_ old: String, to newName: String, remember: Bool) {
    let new = newName.trimmingCharacters(in: .whitespacesAndNewlines)
    // Only a speaker of this meeting can be renamed: a link could otherwise replace any word of
    // the minutes (or, with an empty name, put text between all of them).
    guard var f = finished, !new.isEmpty, new != old, f.turns.contains(where: { $0.speaker == old }) else { return }
    for i in f.turns.indices where f.turns[i].speaker == old { f.turns[i].speaker = new }
    f.minutes = f.minutes?.replacingOccurrences(
      of: Self.wholeNamePattern(old), with: NSRegularExpression.escapedTemplate(for: new), options: .regularExpression)
    if let voice = f.voices.removeValue(forKey: old) {
      // The old name was remembered from this meeting and is now being corrected: the voice
      // library forgets that again, or the wrong name would come back in the next meeting.
      if let undo = f.remembered.removeValue(forKey: old) { VoiceLibrary.restore(old, to: undo.previous) }
      // Renaming onto a name already in this meeting merges the two; that person's voice stays.
      if f.voices[new] == nil {
        f.voices[new] = voice
        if remember { f.remembered[new] = RememberedVoice(previous: VoiceLibrary.remember(new, embedding: voice)) }
      }
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

  /// `name` where it stands on its own, not inside a longer word or number ("Speaker 1" not in
  /// "Speaker 10"). Not `\b`: that needs a letter or digit at each end, so a name ending in "."
  /// or ")" would never be found.
  static func wholeNamePattern(_ name: String) -> String {
    let wordCharacter = "[\\p{L}\\p{N}_]"
    return "(?<!\(wordCharacter))" + NSRegularExpression.escapedPattern(for: name) + "(?!\(wordCharacter))"
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
    var item = QAItem(question: question)
    guard let key = APIKeyStore.key(for: settings.provider), !settings.minutesModel.isEmpty else {
      item.error = MinutesError.noKey.localizedDescription
      questions.append(item)
      return
    }
    questions.append(item)
    let id = item.id
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
        let result = try await client.answer(
          question: question, transcript: transcript, language: language, context: context.promptText)
        Log.write("question answered, usage in=\(result.usage.input) out=\(result.usage.output)")
        // The question is looked up again: another meeting may have started since, with its own
        // (or no) questions, and the answer doesn't belong there.
        guard let index = questions.firstIndex(where: { $0.id == id }) else { return }
        questions[index].answer = result.text
        if isIdle { rewriteFinished() }
      } catch {
        guard let index = questions.firstIndex(where: { $0.id == id }) else { return }
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
    writeDraftSoon()
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
      // Also while the meeting is being finished: the saved file is still the previous meeting's then.
      if !isIdle, let document { return document.draftMarkdown(entries: entries, minutes: minutes?.text) }
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
    if textChanged, phase == .recording { writeDraftSoon() }
  }

  /// Settings → Minutes was switched off for translated or transcribed meetings. If that is the
  /// kind that is running, nothing more is sent for this meeting: neither the updates nor the
  /// final minutes. What has been written stays in the pane as your notes, and "AI Minutes"
  /// there switches them on again.
  func minutesSwitchedOff(for mode: MeetingMode) {
    guard isRecording, let config, config.mode == mode, let runner = minutesRunner else { return }
    finishEditingMinutes()
    runner.discard()
    minutesRunner = nil
    self.config?.minutes = nil
    var notes = MinutesState(usesAI: false, model: "", provider: AppSettings.shared.provider)
    notes.text = minutes?.text ?? ""
    minutes = notes
    Log.write("minutes switched off in Settings: nothing more is sent for this meeting")
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
    guard canStartAIMinutes, let meetingConfig, let document = isRecording ? self.document : finished?.document,
      let minutesConfig = AppSettings.shared.minutesConfig(
        for: meetingConfig.mode, evenIfOff: true, spoken: meetingConfig.spokenLocale.identifier(.bcp47))
    else { return }
    finishEditingMinutes()  // notes still being typed count too
    let notes = minutes?.text ?? ""
    let runner = MinutesRunner(config: minutesConfig) { [weak self] state in
      // Not once another meeting has started: its pane and file aren't this meeting's.
      guard let self, self.isCurrent(document) else { return }
      self.minutesChanged(state)
    }
    runner.meetingContext = context.promptText
    if !notes.isEmpty { runner.finishEditing(text: notes) }
    minutes = runner.state
    setWarning(.minutes, nil)
    Log.write("AI minutes switched on \(isRecording ? "during" : "after") the meeting, notes: \(notes.isEmpty ? "none" : "yes")")

    if isRecording {
      config?.minutes = minutesConfig
      document.usesAIMinutes = true
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
      let used = context
      Task {
        let text = await runner.finalize(transcript: Self.minutesTranscript(turns: f.turns, bookmarks: f.bookmarks))
        guard finished?.document === document else { return }  // another meeting has started
        guard let text else {
          finished?.minutesFailed = true  // the error shows in the pane, with "Try Again"; your notes stay as they were
          return
        }
        document.usesAIMinutes = true
        finished?.minutes = text
        finished?.minutesFailed = false
        minutesContext = used
        rewriteFinished()
      }
    }
  }

  // MARK: Context

  private func contextChanged() {
    contextPending = true
    contextTask?.cancel()
    contextTask = Task { [weak self] in
      try? await Task.sleep(for: .seconds(1))
      guard !Task.isCancelled else { return }
      await self?.applyContext()
    }
  }

  /// Hands the Context tab to the minutes, the recognizers and the file. Questions read it when asked.
  private func applyContext() async {
    let context = self.context
    if isRecording, let config {
      contextPending = false
      minutesRunner?.meetingContext = context.promptText
      document?.context = context
      writeDraftSoon()
      if context.names != recognizerNames {
        recognizerNames = context.names
        let vocabulary = Self.vocabulary(config.vocabulary, recognizerNames)
        for pipeline in Array(pipelines.values) + Array(secondaryPipelines.values) {
          await pipeline.setVocabulary(vocabulary)
        }
      }
    } else if isIdle, let f = finished {
      contextPending = false
      f.document.context = context
      rewriteFinished()
    } else {
      return  // starting or finishing: picked up when that's done
    }
    Log.write("context: \(context.people.count) participants, \(context.aboutText.count) characters about the meeting")
  }

  /// What would otherwise be lost when the app quits or the next meeting takes over the window:
  /// a minutes edit that isn't finished ("Done") and Context changes that are still waiting to
  /// be applied go into the finished meeting's file now.
  func commitPendingEdits() {
    guard isIdle else { return }
    finishEditingMinutes()
    guard contextPending, let f = finished else { return }
    contextTask?.cancel()
    contextPending = false
    f.document.context = context
    rewriteFinished()
  }

  /// Settings' vocabulary plus the participants' names, without repeats.
  private static func vocabulary(_ terms: [String], _ names: [String]) -> [String] {
    (terms + names).reduce(into: [String]()) { if !$0.contains($1) { $0.append($1) } }
  }

  /// After the meeting: once the context has changed since the minutes were written, or when
  /// writing them failed (then to try again).
  var canRedoMinutes: Bool {
    guard isIdle, let f = finished, let minutes, minutes.usesAI, !minutes.isUpdating, canAsk else { return false }
    if f.minutesFailed { return true }
    guard f.minutes?.isEmpty == false, let minutesContext else { return false }
    return minutesContext != context
  }

  /// "Redo Minutes" in the Context tab: final minutes again, from the whole transcript and the
  /// current minutes, with the new context.
  func redoMinutes() {
    guard canRedoMinutes, let f = finished,
      let minutesConfig = AppSettings.shared.minutesConfig(
        for: f.config.mode, evenIfOff: true, spoken: f.config.spokenLocale.identifier(.bcp47))
    else { return }
    finishEditingMinutes()
    let draft = finished?.minutes
    let document = f.document
    let runner = MinutesRunner(config: minutesConfig, draft: draft) { [weak self] state in
      // Not once another meeting has started.
      guard let self, self.finished?.document === document else { return }
      self.minutesChanged(state)
    }
    runner.meetingContext = context.promptText
    let used = context
    minutes = runner.state
    Log.write("minutes redone \(f.minutesFailed ? "after they couldn't be written" : "with a changed context")")
    Task {
      let text = await runner.finalize(transcript: Self.minutesTranscript(turns: f.turns, bookmarks: f.bookmarks))
      guard finished?.document === document else { return }  // another meeting has started
      guard let text else {
        finished?.minutesFailed = true  // the error shows in the pane; the minutes stay as they were
        return
      }
      document.usesAIMinutes = true
      finished?.minutes = text
      finished?.minutesFailed = false
      finished?.notes.removeAll { $0.hasPrefix(Self.minutesFailedNote) }
      minutesContext = used
      rewriteFinished()
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
    // After the meeting, not while minutes are being written: their arrival would replace the edit.
    if isIdle, minutes.isUpdating { return }
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
      writeDraftSoon()
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
        // Parakeet follows Apple's lines, but also has to move on by itself where there are none
        // (always in Parakeet-only mode, otherwise a source nobody speaks on).
        for source in Array(self.histories.keys) { self.refine(source) }
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
          self.setWarning(
            .audio,
            "No meeting audio received yet. If people are talking, allow Kurzschrift under "
              + "System Settings → Privacy & Security → Screen & System Audio Recording, then restart the recording.")
        } else if warnedAboutSilence, self.warnings[.audio] != nil, self.tracks[.system]?.hasSignal == true {
          self.setWarning(.audio, nil)  // only this warning: the others stay
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
    let folder = Recovery.recordingsFolder.appendingPathComponent(UUID().uuidString)
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
