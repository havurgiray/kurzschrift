import AppKit
import SwiftUI

struct KurzschriftApp: App {
  @NSApplicationDelegateAdaptor(AppDelegate.self) private var appDelegate
  @StateObject private var session = SessionController.shared
  @StateObject private var settings = AppSettings.shared

  var body: some Scene {
    MenuBarExtra {
      MenuContent(session: session, settings: settings)
    } label: {
      MenuBarLabel(session: session)
    }
    .menuBarExtraStyle(.menu)

    Settings {
      SettingsView(settings: settings)
    }
  }
}

final class AppDelegate: NSObject, NSApplicationDelegate {
  func applicationDidFinishLaunching(_ notification: Notification) {
    MainActor.assumeIsolated {
      HotKeys.shared.apply(enabled: AppSettings.shared.globalShortcuts)
      CallDetector.shared.apply()
    }
  }

  func applicationWillFinishLaunching(_ notification: Notification) {
    NSAppleEventManager.shared().setEventHandler(
      self, andSelector: #selector(handleURLEvent(_:reply:)),
      forEventClass: AEEventClass(kInternetEventClass), andEventID: AEEventID(kAEGetURL))
  }

  /// kurzschrift://start?mode=translate|transcribe, kurzschrift://stop,
  /// kurzschrift://toggle?mode=... — for Shortcuts, Raycast, hotkey tools.
  @objc private func handleURLEvent(_ event: NSAppleEventDescriptor, reply: NSAppleEventDescriptor) {
    guard let string = event.paramDescriptor(forKeyword: keyDirectObject)?.stringValue,
      let components = URLComponents(string: string), let action = components.host
    else { return }
    let mode = components.queryItems?.first { $0.name == "mode" }?.value.flatMap(MeetingMode.init(rawValue:))
    func query(_ name: String) -> String? { components.queryItems?.first { $0.name == name }?.value }
    Log.write("url: \(string)")
    Task { @MainActor in
      let session = SessionController.shared
      switch action {
      case "start":
        let audio = components.queryItems?.first { $0.name == "audio" }?.value.map { URL(fileURLWithPath: $0) }
        if let mode { await session.start(mode, testAudio: audio) }
      case "stop": await session.stop()
      case "minutes": session.updateMinutesNow()
      case "bookmark": session.addBookmark()
      case "snapshot":
        if let path = query("path") {
          if query("window") == "subtitles" {
            SubtitleController.shared.snapshot(to: path)
          } else {
            LiveWindowController.shared.snapshot(to: path)
          }
        }
      case "rename":
        if let from = query("from"), let to = query("to") {
          session.renameSpeaker(from, to: to, remember: query("remember") != "no")
        }
      case "ask":
        if let question = query("q") { session.ask(question) }
      case "edit-minutes":
        // Developer hook: step=begin, step=cancel, or text=… to edit the minutes in one go.
        switch query("step") {
        case "begin": session.beginEditingMinutes()
        case "cancel": session.cancelEditingMinutes()
        default:
          session.beginEditingMinutes()
          if let text = query("text") { session.minutesDraft = text }
          session.finishEditingMinutes()
        }
      case "toggle":
        if session.isRecording { await session.stop() } else if let mode { await session.start(mode) }
      default: break
      }
    }
  }

  /// Quitting mid-meeting saves the transcript first.
  func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
    let session = SessionController.shared
    guard !session.isIdle else { return .terminateNow }
    Task { @MainActor in
      while !session.isIdle {
        if session.isRecording {
          await session.stop()
        } else {
          try? await Task.sleep(for: .milliseconds(200))
        }
      }
      sender.reply(toApplicationShouldTerminate: true)
    }
    return .terminateLater
  }
}

private struct MenuBarLabel: View {
  @ObservedObject var session: SessionController

  var body: some View {
    switch session.phase {
    case .idle:
      Image(systemName: "captions.bubble")
    case .starting, .finishing:
      Image(systemName: "hourglass")
    case .recording:
      // Driven by a once-a-second counter: a TimelineView in a status item label re-renders endlessly.
      HStack(spacing: 3) {
        Image(systemName: "record.circle")
        Text(Self.short(session.elapsedSeconds)).monospacedDigit()
      }
    }
  }

  static func short(_ seconds: Int) -> String {
    let s = seconds
    return s >= 3600 ? String(format: "%d:%02d:%02d", s / 3600, s / 60 % 60, s % 60) : String(format: "%d:%02d", s / 60, s % 60)
  }
}

private struct MenuContent: View {
  @ObservedObject var session: SessionController
  @ObservedObject var settings: AppSettings
  @Environment(\.openSettings) private var openSettings

  var body: some View {
    switch session.phase {
    case .idle:
      Button(settings.menuTitle(for: .translate)) { Task { await session.start(.translate) } }
        .keyboardShortcut("1")
      Button(settings.menuTitle(for: .transcribe)) { Task { await session.start(.transcribe) } }
        .keyboardShortcut("2")
    case .starting(let message), .finishing(let message):
      Text(message)
    case .recording:
      Text(session.config?.mode == .translate ? "Translating…" : "Transcribing…")
      Button("Stop and Save") { Task { await session.stop() } }
        .keyboardShortcut("s")
      Button("Bookmark This Moment") { session.addBookmark() }
        .keyboardShortcut("b")
    }

    Divider()
    Button("Show Live Window") { LiveWindowController.shared.show() }
      .keyboardShortcut("l")
    if session.canExport {
      Menu("Export Transcript") {
        Button("Save as Markdown…") { session.exportWithSavePanel(.markdown) }
        Button("Save as Text…") { session.exportWithSavePanel(.text) }
        Button("Copy to Clipboard") { session.copyToClipboard() }
      }
    }
    Button("Open Last Transcript") {
      if let url = session.lastTranscript { NSWorkspace.shared.open(url) }
    }
    .disabled(session.lastTranscript == nil)
    Toggle("Subtitles", isOn: $settings.subtitles)
      .keyboardShortcut("t")
    Picker("Live Update Interval", selection: $settings.liveUpdateMilliseconds) {
      ForEach(AppSettings.liveUpdateChoices, id: \.self) { ms in
        Text(AppSettings.intervalName(ms)).tag(ms)
      }
    }
    Button("Open Transcripts Folder") {
      let folder = URL(fileURLWithPath: (settings.saveFolder as NSString).expandingTildeInPath)
      try? FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
      NSWorkspace.shared.open(folder)
    }

    Divider()
    Button("Settings…") {
      NSApp.activate()
      openSettings()
    }
    .keyboardShortcut(",")
    Button("Quit Kurzschrift") { NSApp.terminate(nil) }
      .keyboardShortcut("q")
  }
}
