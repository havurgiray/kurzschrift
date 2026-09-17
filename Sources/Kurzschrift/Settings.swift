import Foundation
import ServiceManagement

enum MeetingMode: String, CaseIterable, Identifiable {
  case translate
  case transcribe

  var id: String { rawValue }
}

/// All user settings, persisted in UserDefaults.
@MainActor
final class AppSettings: ObservableObject {
  static let shared = AppSettings()
  private let defaults = UserDefaults.standard

  // General
  @Published var saveFolder: String { didSet { defaults.set(saveFolder, forKey: "saveFolder") } }
  @Published var myName: String { didSet { defaults.set(myName, forKey: "myName") } }
  @Published var showLiveWindow: Bool { didSet { defaults.set(showLiveWindow, forKey: "showLiveWindow") } }
  @Published var liveFontSize: Double { didSet { defaults.set(liveFontSize, forKey: "liveFontSize") } }
  /// How often the in-progress translation line and the recording clock refresh.
  @Published var liveUpdateMilliseconds: Int { didSet { defaults.set(liveUpdateMilliseconds, forKey: "liveUpdateMilliseconds") } }
  static let liveUpdateChoices = [250, 500, 1000, 2000]
  @Published var keepAudio: Bool { didSet { defaults.set(keepAudio, forKey: "keepAudio") } }

  // Translation meetings (use case 1)
  @Published var translateSpokenLocale: String { didSet { defaults.set(translateSpokenLocale, forKey: "translateSpokenLocale") } }
  @Published var translateTarget: String { didSet { defaults.set(translateTarget, forKey: "translateTarget") } }
  @Published var translateIncludeMic: Bool { didSet { defaults.set(translateIncludeMic, forKey: "translateIncludeMic") } }
  @Published var translateSeparateSpeakers: Bool { didSet { defaults.set(translateSeparateSpeakers, forKey: "translateSeparateSpeakers") } }
  @Published var translateShowOriginalLive: Bool { didSet { defaults.set(translateShowOriginalLive, forKey: "translateShowOriginalLive") } }
  @Published var translateLayout: String { didSet { defaults.set(translateLayout, forKey: "translateLayout") } }

  // Transcription meetings (use case 2)
  @Published var transcribeLocale: String { didSet { defaults.set(transcribeLocale, forKey: "transcribeLocale") } }
  @Published var transcribeIncludeMic: Bool { didSet { defaults.set(transcribeIncludeMic, forKey: "transcribeIncludeMic") } }
  @Published var transcribeSeparateSpeakers: Bool { didSet { defaults.set(transcribeSeparateSpeakers, forKey: "transcribeSeparateSpeakers") } }

  // Microphone
  @Published var micDeviceUID: String { didSet { defaults.set(micDeviceUID, forKey: "micDeviceUID") } }
  @Published var micShared: Bool { didSet { defaults.set(micShared, forKey: "micShared") } }

  private init() {
    let d = UserDefaults.standard
    let documents = FileManager.default.urls(for: .documentDirectory, in: .userDomainMask)[0]
    d.register(defaults: [
      "saveFolder": documents.appendingPathComponent("Meeting Transcripts").path,
      "myName": "Me",
      "showLiveWindow": true,
      "liveFontSize": 15.0,
      "liveUpdateMilliseconds": 500,
      "keepAudio": false,
      "translateSpokenLocale": "de-DE",
      "translateTarget": "en-US",
      "translateIncludeMic": false,
      "translateSeparateSpeakers": true,
      "translateShowOriginalLive": true,
      "translateLayout": TranscriptLayout.interleaved.rawValue,
      "transcribeLocale": "en-US",
      "transcribeIncludeMic": true,
      "transcribeSeparateSpeakers": true,
      "micDeviceUID": "",
      "micShared": false,
    ])
    saveFolder = d.string(forKey: "saveFolder")!
    myName = d.string(forKey: "myName")!
    showLiveWindow = d.bool(forKey: "showLiveWindow")
    liveFontSize = d.double(forKey: "liveFontSize")
    liveUpdateMilliseconds = d.integer(forKey: "liveUpdateMilliseconds")
    keepAudio = d.bool(forKey: "keepAudio")
    translateSpokenLocale = d.string(forKey: "translateSpokenLocale")!
    translateTarget = d.string(forKey: "translateTarget")!
    translateIncludeMic = d.bool(forKey: "translateIncludeMic")
    translateSeparateSpeakers = d.bool(forKey: "translateSeparateSpeakers")
    translateShowOriginalLive = d.bool(forKey: "translateShowOriginalLive")
    translateLayout = d.string(forKey: "translateLayout")!
    transcribeLocale = d.string(forKey: "transcribeLocale")!
    transcribeIncludeMic = d.bool(forKey: "transcribeIncludeMic")
    transcribeSeparateSpeakers = d.bool(forKey: "transcribeSeparateSpeakers")
    micDeviceUID = d.string(forKey: "micDeviceUID")!
    micShared = d.bool(forKey: "micShared")
  }

  var launchAtLogin: Bool {
    get { SMAppService.mainApp.status == .enabled }
    set {
      objectWillChange.send()
      do {
        if newValue { try SMAppService.mainApp.register() } else { try SMAppService.mainApp.unregister() }
      } catch {
        Log.write("Launch at login change failed: \(error)")
      }
    }
  }

  /// Frozen copy of the settings a session runs with, so edits mid-meeting don't half-apply.
  func snapshot(for mode: MeetingMode) -> SessionConfig {
    switch mode {
    case .translate:
      SessionConfig(
        mode: mode, spokenLocale: Locale(identifier: translateSpokenLocale),
        targetLanguage: Locale.Language(identifier: translateTarget),
        includeMic: translateIncludeMic, separateSpeakers: translateSeparateSpeakers,
        micDeviceUID: micDeviceUID, micShared: micShared, myName: myName.isEmpty ? "Me" : myName,
        saveFolder: URL(fileURLWithPath: (saveFolder as NSString).expandingTildeInPath),
        keepAudio: keepAudio, layout: TranscriptLayout(rawValue: translateLayout) ?? .interleaved)
    case .transcribe:
      SessionConfig(
        mode: mode, spokenLocale: Locale(identifier: transcribeLocale), targetLanguage: nil,
        includeMic: transcribeIncludeMic, separateSpeakers: transcribeSeparateSpeakers,
        micDeviceUID: micDeviceUID, micShared: micShared, myName: myName.isEmpty ? "Me" : myName,
        saveFolder: URL(fileURLWithPath: (saveFolder as NSString).expandingTildeInPath),
        keepAudio: keepAudio, layout: .interleaved)
    }
  }

  func menuTitle(for mode: MeetingMode) -> String {
    switch mode {
    case .translate:
      "Translate Meeting (\(Self.languageName(translateSpokenLocale)) → \(Self.languageName(translateTarget)))"
    case .transcribe:
      "Transcribe Meeting (\(Self.languageName(transcribeLocale)))"
    }
  }

  nonisolated static func intervalName(_ milliseconds: Int) -> String {
    milliseconds < 1000 ? "\(milliseconds) ms" : "\(milliseconds / 1000) s"
  }

  nonisolated static func languageName(_ identifier: String) -> String {
    let locale = Locale(identifier: "en-US")
    return locale.localizedString(forLanguageCode: Locale(identifier: identifier).language.languageCode?.identifier ?? identifier)
      ?? identifier
  }

  nonisolated static func localeName(_ identifier: String) -> String {
    Locale(identifier: "en-US").localizedString(forIdentifier: identifier) ?? identifier
  }
}

enum TranscriptLayout: String, CaseIterable, Identifiable {
  case interleaved
  case sections

  var id: String { rawValue }
}

struct SessionConfig {
  let mode: MeetingMode
  let spokenLocale: Locale
  let targetLanguage: Locale.Language?
  var includeMic: Bool
  let separateSpeakers: Bool
  let micDeviceUID: String
  let micShared: Bool
  let myName: String
  var saveFolder: URL
  var keepAudio: Bool
  let layout: TranscriptLayout
}
