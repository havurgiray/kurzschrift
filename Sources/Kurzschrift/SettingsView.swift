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
        Toggle("Also keep the audio recording (WAV) next to each transcript", isOn: $settings.keepAudio)
      }
      Section {
        TextField("Your name", text: $settings.myName, prompt: Text("Me"))
        Text("Used as the speaker name for your microphone.")
          .font(.caption).foregroundStyle(.secondary)
      }
      Section {
        Toggle("Show the live window when a meeting starts", isOn: $settings.showLiveWindow)
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
      Section {
        Toggle("Launch at login", isOn: Binding(get: { settings.launchAtLogin }, set: { settings.launchAtLogin = $0 }))
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

private struct TranslationSettings: View {
  @ObservedObject var settings: AppSettings
  @State private var speechLocales: [String] = []
  @State private var targetLanguages: [String] = []
  @State private var installed: Bool?

  var body: some View {
    Form {
      Section("Translate meeting") {
        LocalePicker(title: "People speak", selection: $settings.translateSpokenLocale, options: speechLocales)
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

      Section("Extra instructions (optional)") {
        TextEditor(text: $settings.minutesInstructions)
          .font(.body)
          .frame(height: 60)
        Text("For example: “Focus on action items for me.” or “Use the project names Atlas and Nimbus.”")
          .font(.caption).foregroundStyle(.secondary)
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
