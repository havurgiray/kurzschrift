import AppKit
import Speech
import SwiftUI
import Translation

struct SettingsView: View {
  @ObservedObject var settings: AppSettings

  var body: some View {
    TabView {
      GeneralSettings(settings: settings)
        .tabItem { Label("General", systemImage: "gearshape") }
      MeetingsSettings(settings: settings)
        .tabItem { Label("Meetings", systemImage: "calendar.badge.clock") }
      TranslationSettings(settings: settings)
        .tabItem { Label("Translate", systemImage: "character.bubble") }
      TranscriptionSettings(settings: settings)
        .tabItem { Label("Transcribe", systemImage: "text.bubble") }
      MinutesSettings(settings: settings)
        .tabItem { Label("Minutes", systemImage: "list.bullet.clipboard") }
      MicrophoneSettings(settings: settings)
        .tabItem { Label("Microphone", systemImage: "mic") }
    }
    .frame(width: 540)
  }
}

private struct GeneralSettings: View {
  @ObservedObject var settings: AppSettings

  var body: some View {
    Form {
      Section {
        LabeledContent("Save transcripts to") {
          HStack {
            Text((settings.saveFolder as NSString).abbreviatingWithTildeInPath)
              .lineLimit(1).truncationMode(.middle)
              .foregroundStyle(.secondary)
            Button("Choose…", action: chooseFolder)
          }
        }
        Toggle("Keep the meeting recording next to each transcript", isOn: $settings.keepAudio)
        Picker("Recording quality", selection: $settings.audioBitrate) {
          ForEach(AppSettings.audioBitrateChoices, id: \.self) { Text(AppSettings.audioBitrateName($0)).tag($0) }
        }
        .disabled(!settings.keepAudio)
        Text("Saved as .m4a (AAC), which plays anywhere. Transcription and speaker separation always use the full-quality recording; this only sets the size of the copy you keep. With your microphone on, there are two files.")
          .font(.caption).foregroundStyle(.secondary)
      }
      Section {
        Toggle("Improve the transcript with Parakeet", isOn: $settings.parakeet)
        Text("About every 15 seconds, NVIDIA's Parakeet model goes over what was just said again, on this Mac, and its text replaces the first version in the transcript, the minutes and the file: fewer misheard words and whole sentences, in German and English. The live captions stay as they are. Downloads 470 MB the first time.")
          .font(.caption).foregroundStyle(.secondary)
      }
      Section {
        LabeledContent("Vocabulary") {
          TextEditor(text: $settings.vocabulary)
            .font(.body)
            .frame(height: 54)
        }
        Text("Names and terms that keep getting misheard, one per line or comma-separated (e.g. Kubernetes, OKR, Maria Huber). They're given to speech recognition and to the minutes. Applies from the next meeting.")
          .font(.caption).foregroundStyle(.secondary)
      }
      Section {
        TextField("Your name", text: $settings.myName, prompt: Text("Me"))
        Text("Used as the speaker name for your microphone.")
          .font(.caption).foregroundStyle(.secondary)
      }
      Section {
        Toggle("Show the live window when a meeting starts", isOn: $settings.showLiveWindow)
        Toggle("Minimize it to a small bar", isOn: $settings.liveWindowSmall)
        Text("The bar shows the clock, what's being said, and Bookmark and Stop. The arrows button in the window switches between the bar and the full window.")
          .font(.caption).foregroundStyle(.secondary)
        Picker("Live update interval", selection: $settings.liveUpdateMilliseconds) {
          ForEach(AppSettings.liveUpdateChoices, id: \.self) { ms in
            Text(AppSettings.intervalName(ms)).tag(ms)
          }
        }
        LabeledContent("Live text size") {
          Slider(value: $settings.liveFontSize, in: 11...28, step: 1)
            .frame(width: 180)
          Text("\(Int(settings.liveFontSize)) pt").monospacedDigit().frame(width: 40, alignment: .trailing)
        }
      }
      VoicesSection(settings: settings)
      Section {
        Toggle("Subtitles while recording", isOn: $settings.subtitles)
        Picker("Subtitle position", selection: $settings.subtitlesAtTop) {
          Text("Bottom of the screen").tag(false)
          Text("Top of the screen").tag(true)
        }
        LabeledContent("Subtitle size") {
          Slider(value: $settings.subtitleFontSize, in: 18...48, step: 2).frame(width: 180)
          Text("\(Int(settings.subtitleFontSize)) pt").monospacedDigit().frame(width: 40, alignment: .trailing)
        }
      }
      Section {
        Toggle("Launch at login", isOn: Binding(get: { settings.launchAtLogin }, set: { settings.launchAtLogin = $0 }))
      }
      Section {
        Toggle("Allow developer links", isOn: $settings.developerLinks)
        Text("For testing Kurzschrift: kurzschrift:// links that replay an audio file as a meeting, save the meeting window as an image, edit the minutes or resize the window. Leave this off otherwise, since any web page can try to open these links. Start, stop, bookmark and the other links work either way.")
          .font(.caption).foregroundStyle(.secondary)
      }
    }
    .formStyle(.grouped)
  }

  private func chooseFolder() {
    let panel = NSOpenPanel()
    panel.canChooseDirectories = true
    panel.canChooseFiles = false
    panel.canCreateDirectories = true
    panel.directoryURL = URL(fileURLWithPath: (settings.saveFolder as NSString).expandingTildeInPath)
    NSApp.activate()
    if panel.runModal() == .OK, let url = panel.url { settings.saveFolder = url.path }
  }
}

/// Remembered voices: people named in earlier meetings get their names automatically.
private struct VoicesSection: View {
  @ObservedObject var settings: AppSettings
  @State private var voices: [VoiceLibrary.Voice] = []

  var body: some View {
    Section {
      Toggle("Recognize people I've named before", isOn: $settings.recognizeVoices)
      if voices.isEmpty {
        Text("After a meeting, click a speaker's name in the meeting window to rename them and remember their voice.")
          .font(.caption).foregroundStyle(.secondary)
      }
      ForEach(voices, id: \.name) { voice in
        LabeledContent(voice.name) {
          Text(voice.meetings == 1 ? "1 meeting" : "\(voice.meetings) meetings").foregroundStyle(.secondary)
          Button("Forget") {
            VoiceLibrary.forget(voice.name)
            voices = VoiceLibrary.all()
          }
        }
      }
    } header: {
      Text("Speakers")
    }
    .onAppear { voices = VoiceLibrary.all() }
  }
}

private struct MeetingsSettings: View {
  @ObservedObject var settings: AppSettings

  var body: some View {
    Form {
      Section {
        Picker("Stop after silence", selection: $settings.autoStopMinutes) {
          ForEach(AppSettings.autoStopChoices, id: \.self) { minutes in
            Text(minutes == 0 ? "Never" : "\(minutes) min without speech").tag(minutes)
          }
        }
        Text("Stops and saves a recording nobody has spoken in for that long, e.g. one left running after a call.")
          .font(.caption).foregroundStyle(.secondary)
      }
      Section {
        Toggle("Offer to record when a call starts", isOn: $settings.detectCalls)
        Toggle("Also count browser calls (Google Meet, Teams on the web…)", isOn: $settings.detectBrowserCalls)
          .disabled(!settings.detectCalls)
        Toggle("Stop and save when the call ends", isOn: $settings.stopWhenCallEnds)
          .disabled(!settings.detectCalls)
        Text("Watches which apps use the microphone (Teams, Zoom, Webex, FaceTime, Slack, Discord). It sees app names only, never audio.")
          .font(.caption).foregroundStyle(.secondary)
      }
      Section {
        Toggle("Name transcripts after the current calendar event", isOn: $settings.useCalendar)
        Text("Uses the event's title for the file and gives its attendee list to the minutes. Read on this Mac only.")
          .font(.caption).foregroundStyle(.secondary)
      }
      Section {
        Toggle("Global shortcuts", isOn: $settings.globalShortcuts)
        Text(HotKeys.shared.shortcuts.map { "\($0.display) \(Self.shortcutName($0.id))" }.joined(separator: "   ·   "))
          .font(.caption).foregroundStyle(.secondary)
      }
    }
    .formStyle(.grouped)
  }

  private static func shortcutName(_ id: UInt32) -> String {
    switch id {
    case 1: "start/stop translating"
    case 2: "start/stop transcribing"
    default: "bookmark"
    }
  }
}

private struct TranslationSettings: View {
  @ObservedObject var settings: AppSettings
  @State private var speechLocales: [String] = []
  @State private var targetLanguages: [String] = []
  @State private var installed: Bool?

  var body: some View {
    Form {
      Section("Translate meeting") {
        LocalePicker(title: "People speak", selection: $settings.translateSpokenLocale, options: speechLocales)
        AlsoPicker(selection: $settings.translateAlsoLocale, options: speechLocales)
        LocalePicker(title: "Translate into", selection: $settings.translateTarget, options: targetLanguages)
        LabeledContent("Translation languages") {
          switch installed {
          case .none: ProgressView().controlSize(.small)
          case .some(true): Label("Downloaded", systemImage: "checkmark.circle.fill").foregroundStyle(.green)
          case .some(false):
            Button("Download in System Settings…") {
              NSWorkspace.shared.open(URL(string: "x-apple.systempreferences:com.apple.Localization-Settings.extension")!)
            }
          }
        }
      }
      Section {
        Toggle("Include my microphone", isOn: $settings.translateIncludeMic)
        Toggle("Identify speakers (Speaker 1, Speaker 2, …)", isOn: $settings.translateSeparateSpeakers)
        Picker("Transcript layout", selection: $settings.translateLayout) {
          Text("Each passage in both languages").tag(TranscriptLayout.interleaved.rawValue)
          Text("Full original, then full translation").tag(TranscriptLayout.sections.rawValue)
        }
      }
    }
    .formStyle(.grouped)
    .task {
      speechLocales = await SpeechTranscriber.supportedLocales.map { $0.identifier(.bcp47) }.sorted()
      let languages = await LanguageAvailability().supportedLanguages
      let identifiers = languages.compactMap { (language: Locale.Language) -> String? in
        guard let code = language.languageCode?.identifier else { return nil }
        return language.region.map { "\(code)-\($0.identifier)" } ?? code
      }
      targetLanguages = Array(Set(identifiers)).sorted()
    }
    .task(id: settings.translateSpokenLocale + settings.translateTarget) {
      installed = nil
      installed = await Translator.isInstalled(
        from: Locale(identifier: settings.translateSpokenLocale).language,
        to: Locale.Language(identifier: settings.translateTarget))
    }
  }
}

private struct TranscriptionSettings: View {
  @ObservedObject var settings: AppSettings
  @State private var speechLocales: [String] = []

  var body: some View {
    Form {
      Section("Transcribe meeting") {
        LocalePicker(title: "People speak", selection: $settings.transcribeLocale, options: speechLocales)
        AlsoPicker(selection: $settings.transcribeAlsoLocale, options: speechLocales)
        Toggle("Include my microphone", isOn: $settings.transcribeIncludeMic)
        Toggle("Identify speakers (Speaker 1, Speaker 2, …)", isOn: $settings.transcribeSeparateSpeakers)
      }
    }
    .formStyle(.grouped)
    .task {
      speechLocales = await SpeechTranscriber.supportedLocales.map { $0.identifier(.bcp47) }.sorted()
    }
  }
}

private struct MinutesSettings: View {
  @ObservedObject var settings: AppSettings
  @State private var apiKey = ""
  @State private var models: [String] = []
  @State private var testState: TestState = .idle

  private enum TestState: Equatable {
    case idle, testing, ok(Int), failed(String)
  }

  var body: some View {
    Form {
      Section {
        Toggle("Translate meetings", isOn: $settings.minutesForTranslate)
        Toggle("Transcribe meetings", isOn: $settings.minutesForTranscribe)
        Picker("Update every", selection: $settings.minutesIntervalMinutes) {
          ForEach(AppSettings.minutesIntervalChoices, id: \.self) { Text("\($0) min").tag($0) }
        }
        Text(
          "While a meeting runs, the transcript is sent to \(settings.provider.displayName) to keep the minutes up to date, and once more at the end for the final minutes, which go into the transcript file. Everything else stays on this Mac."
        )
        .font(.caption).foregroundStyle(.secondary)
      } header: {
        Text("Live minutes")
      }

      Section("Provider") {
        Picker("Provider", selection: $settings.minutesProvider) {
          ForEach(MinutesProvider.allCases) { Text($0.displayName).tag($0.rawValue) }
        }
        LabeledContent("API key") {
          HStack {
            SecureField("", text: $apiKey, prompt: Text("Paste your key"))
              .onChange(of: apiKey) { APIKeyStore.setKey(apiKey, for: settings.provider) }
            Link("Get a key", destination: settings.provider.keysPage)
          }
        }
        LabeledContent("Model") {
          HStack {
            TextField("", text: Binding(get: { settings.minutesModel }, set: { settings.minutesModel = $0 }),
              prompt: Text(settings.provider == .openai ? "Test the key to list models" : "claude-opus-5-5"))
            if !models.isEmpty {
              Menu("Choose") {
                ForEach(models, id: \.self) { id in Button(id) { settings.minutesModel = id } }
              }
              .fixedSize()
            }
          }
        }
        HStack {
          Button("Test Key") { Task { await test() } }
            .disabled(apiKey.isEmpty || testState == .testing)
          switch testState {
          case .idle: EmptyView()
          case .testing: ProgressView().controlSize(.small)
          case .ok(let count): Label("Key works (\(count) models)", systemImage: "checkmark.circle.fill").foregroundStyle(.green)
          case .failed(let message): Label(message, systemImage: "xmark.octagon.fill").foregroundStyle(.red).lineLimit(2)
          }
        }
        Text("The key is stored in ~/Library/Application Support/Kurzschrift/api-keys.json, readable only by your user account.")
          .font(.caption).foregroundStyle(.secondary)
      }

      Section {
        TextEditor(
          text: Binding(
            get: { settings.minutesPrompt.isEmpty ? MinutesClient.defaultPrompt : settings.minutesPrompt },
            set: { settings.minutesPrompt = $0 == MinutesClient.defaultPrompt ? "" : $0 }))
          .font(.callout)
          .frame(minHeight: 300)
        HStack(alignment: .top) {
          Text("Kurzschrift adds the language to write in (\(AppSettings.languageName(settings.translateTarget)) for translated meetings, the meeting's language for transcribed ones), your vocabulary, the attendees from the calendar, and whether the meeting is still running. Keep action items as “- [ ]” lines so Add to Reminders can find them.")
            .font(.caption).foregroundStyle(.secondary)
          Spacer()
          Button("Reset to Default") { settings.minutesPrompt = "" }
            .disabled(settings.minutesPrompt.isEmpty)
        }
      } header: {
        Text("Prompt")
      }
    }
    .formStyle(.grouped)
    .onAppear(perform: loadKey)
    .onChange(of: settings.minutesProvider) { loadKey() }
  }

  private func loadKey() {
    apiKey = APIKeyStore.key(for: settings.provider) ?? ""
    models = []
    testState = .idle
  }

  private func test() async {
    testState = .testing
    let client = MinutesClient(provider: settings.provider, model: settings.minutesModel, apiKey: apiKey)
    do {
      let found = try await client.listModels()
      models = found
      testState = .ok(found.count)
      if settings.minutesModel.isEmpty, let first = found.first { settings.minutesModel = first }
    } catch {
      testState = .failed(error.localizedDescription)
    }
  }
}

private struct MicrophoneSettings: View {
  @ObservedObject var settings: AppSettings
  @State private var devices: [AudioDevices.Device] = []

  var body: some View {
    Form {
      Section {
        Picker("Microphone", selection: $settings.micDeviceUID) {
          Text("System default (\(AudioDevices.defaultInputName() ?? "none"))").tag("")
          ForEach(devices) { device in
            Text(device.name).tag(device.uid)
          }
          if !settings.micDeviceUID.isEmpty, !devices.contains(where: { $0.uid == settings.micDeviceUID }) {
            Text("Disconnected microphone").tag(settings.micDeviceUID)
          }
        }
        Toggle("Several people share this microphone (meeting room)", isOn: $settings.micShared)
        Text(
          "When on, voices on your microphone are separated into speakers too. When off, everything it hears is labeled with your name."
        )
        .font(.caption).foregroundStyle(.secondary)
      }
      Section {
        Text(
          "Use headphones for the cleanest transcript. With loudspeakers your microphone also hears the other participants; Kurzschrift removes most of those duplicates, but not always all of them."
        )
        .font(.callout).foregroundStyle(.secondary)
      }
    }
    .formStyle(.grouped)
    .onAppear { devices = AudioDevices.inputDevices() }
  }
}

/// A second language people switch to; both are recognized and the better one kept per sentence.
private struct AlsoPicker: View {
  @Binding var selection: String
  let options: [String]

  var body: some View {
    Picker("Sometimes also", selection: $selection) {
      Text("Nothing else").tag("")
      ForEach(options.contains(selection) || selection.isEmpty ? options : [selection] + options, id: \.self) { id in
        Text(AppSettings.localeName(id)).tag(id)
      }
    }
    .help("Recognizes this language in parallel and keeps whichever fits each sentence. Uses more processing power.")
  }
}

private struct LocalePicker: View {
  let title: String
  @Binding var selection: String
  let options: [String]

  var body: some View {
    Picker(title, selection: $selection) {
      ForEach(options.contains(selection) ? options : [selection] + options, id: \.self) { id in
        Text(AppSettings.localeName(id)).tag(id)
      }
    }
  }
}
