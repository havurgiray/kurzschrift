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
      UpdateChecker.shared.apply(automatic: AppSettings.shared.checkForUpdates)
      UnsavedMeetings.recoverAtLaunch()
    }
    for provider in MinutesProvider.allCases where provider.isOverridden {
      Log.write("testing override: \(provider.rawValue) requests go to \(provider.baseURL.absoluteString)")
    }
  }

  func applicationWillFinishLaunching(_ notification: Notification) {
    NSAppleEventManager.shared().setEventHandler(
      self, andSelector: #selector(handleURLEvent(_:reply:)),
      forEventClass: AEEventClass(kInternetEventClass), andEventID: AEEventID(kAEGetURL))
  }

  /// kurzschrift://start?mode=translate|transcribe, kurzschrift://stop,
  /// kurzschrift://toggle?mode=... — for Shortcuts, Raycast, hotkey tools.
  /// With mode=transcribe, language=second starts the second Transcribe entry of the menu.
  /// The developer links (start?audio=, snapshot, edit-minutes, window, context) only work while
  /// Settings → General allows them, because any web page can try to open a kurzschrift:// link.
  /// For the same reason the links that send the transcript to the AI provider or change a saved
  /// file (ask, rename, and minutes when it would switch AI minutes on or redo them) need
  /// "Allow links to ask questions…" there.
  @objc private func handleURLEvent(_ event: NSAppleEventDescriptor, reply: NSAppleEventDescriptor) {
    guard let string = event.paramDescriptor(forKeyword: keyDirectObject)?.stringValue,
      let components = URLComponents(string: string), let action = components.host
    else { return }
    let mode = components.queryItems?.first { $0.name == "mode" }?.value.flatMap(MeetingMode.init(rawValue:))
    func query(_ name: String) -> String? { components.queryItems?.first { $0.name == name }?.value }
    let isDeveloperLink = ["snapshot", "edit-minutes", "window", "context"].contains(action)
      || (action == "start" && query("audio") != nil)
    // Parameter names only: a question, a speaker's name or minutes text doesn't belong in the log.
    Log.write("url: \(action) \((components.queryItems ?? []).map(\.name).joined(separator: ","))")
    Task { @MainActor in
      guard !isDeveloperLink || AppSettings.shared.developerLinks else {
        Log.write("url ignored: developer links are off (Settings → General)")
        return
      }
      let session = SessionController.shared
      let needsAutomation = action == "ask" || action == "rename"
        || (action == "minutes" && (session.canStartAIMinutes || session.canRedoMinutes))
      guard !needsAutomation || AppSettings.shared.automationLinks else {
        Log.write("url ignored: automation links are off (Settings → General)")
        return
      }
      switch action {
      case "start":
        let audio = components.queryItems?.first { $0.name == "audio" }?.value.map { URL(fileURLWithPath: $0) }
        if let mode { await session.start(mode, secondLanguage: query("language") == "second", testAudio: audio) }
      case "stop": await session.stop()
      case "minutes":
        if session.canStartAIMinutes {
          session.startAIMinutes()
        } else if session.canRedoMinutes {
          session.redoMinutes()
        } else {
          session.updateMinutesNow()
        }
      case "bookmark": session.addBookmark()
      case "snapshot":
        if let path = query("path"), path.lowercased().hasSuffix(".png") {
          if query("window") == "subtitles" {
            SubtitleController.shared.snapshot(to: path)
          } else if query("window") == "settings" {
            SettingsView.snapshot(tab: query("tab") ?? "general", to: path)
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
      case "window":
        // Developer hook: size=small or size=full, tab=minutes, ask or context.
        if let size = query("size") { AppSettings.shared.liveWindowSmall = size == "small" }
        if let tab = query("tab") { UserDefaults.standard.set(tab, forKey: "liveSideTab") }
        LiveWindowController.shared.show()
      case "context":
        // Developer hook: fills in the Context tab; a parameter left out keeps its text.
        if let participants = query("participants") { session.context.participants = participants }
        if let about = query("about") { session.context.about = about }
      case "toggle":
        if session.isRecording {
          await session.stop()
        } else if let mode {
          await session.start(mode, secondLanguage: query("language") == "second")
        }
      default: break
      }
    }
  }

  /// Quitting mid-meeting saves the transcript first.
  func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
    let session = SessionController.shared
    guard !session.isIdle else {
      // A minutes edit that isn't finished and Context changes still waiting go into the file.
      session.commitPendingEdits()
      return .terminateNow
    }
    Task { @MainActor in
      var starting = 0.0
      while !session.isIdle {
        if session.isRecording {
          await session.stop()
        } else {
          // A meeting that is still starting has nothing to save; if it doesn't get there (a
          // model download, a permission dialog nobody answers), quitting doesn't wait for it.
          if case .starting = session.phase {
            starting += 0.2
            if starting >= 20 { break }
          }
          try? await Task.sleep(for: .milliseconds(200))
        }
      }
      sender.reply(toApplicationShouldTerminate: true)
    }
    return .terminateLater
  }
}

/// Meetings that were never saved (the app crashed or was force-quit): their recordings are
/// still in the recording folder. At launch they are repaired and moved next to their draft
/// transcripts (`Recovery`), and the user is told once.
@MainActor
private enum UnsavedMeetings {
  static func recoverAtLaunch() {
    // Hidden testing default (`defaults write local.kurzschrift skipRecovery -bool true`): leaves
    // what is in the recording folder alone, e.g. while a test build runs next to real leftovers.
    guard !UserDefaults.standard.bool(forKey: "skipRecovery") else { return }
    // Before any meeting of this launch exists, so none of these folders can be a running one's.
    guard SessionController.shared.isIdle else { return }
    recover(Recovery.leftoverFolders(in: Recovery.recordingsFolder), attempt: 0)
  }

  /// Folders something still writes to, or all of them while another copy of the app runs (it
  /// may be recording into one, or finishing a meeting), are left alone and looked at again later.
  private static func recover(_ folders: [URL], attempt: Int) {
    guard !folders.isEmpty else { return }
    let bundleID = Bundle.main.bundleIdentifier ?? "local.kurzschrift"
    let otherCopyRuns = NSRunningApplication.runningApplications(withBundleIdentifier: bundleID)
      .contains { $0.processIdentifier != ProcessInfo.processInfo.processIdentifier }
    let ready = otherCopyRuns ? [] : folders.filter { !Recovery.isBeingWritten($0, within: 90) }
    let waiting = folders.filter { !ready.contains($0) }
    if !waiting.isEmpty {
      Log.write("recovery: \(waiting.count) recording folder(s) still in use\(otherCopyRuns ? " (another Kurzschrift is running)" : ""); looking again later")
      if attempt < 20 {
        Task { @MainActor in
          try? await Task.sleep(for: .seconds(90))
          recover(waiting.filter { FileManager.default.fileExists(atPath: $0.path) }, attempt: attempt + 1)
        }
      }
    }
    guard !ready.isEmpty else { return }
    let transcripts = URL(fileURLWithPath: (AppSettings.shared.saveFolder as NSString).expandingTildeInPath)
    Task.detached(priority: .utility) {
      let meetings = Recovery.recover(ready, fallback: transcripts)
      guard !meetings.isEmpty else { return }
      await MainActor.run { tell(meetings) }
    }
  }

  private static func tell(_ meetings: [Recovery.Meeting]) {
    let recordings = meetings.flatMap(\.recordings)
    let transcripts = meetings.compactMap(\.transcript)
    let folders = (transcripts + recordings).map { ($0.deletingLastPathComponent().path as NSString).abbreviatingWithTildeInPath }
      .reduce(into: [String]()) { if !$0.contains($1) { $0.append($1) } }
    let alert = NSAlert()
    alert.alertStyle = .warning
    alert.messageText = meetings.count == 1
      ? "Kurzschrift didn't finish saving 1 meeting" : "Kurzschrift didn't finish saving \(meetings.count) meetings"
    let what = transcripts.count == meetings.count
      ? (meetings.count == 1 ? "The transcript so far and the recording are" : "The transcripts so far and the recordings are")
      : transcripts.isEmpty
        ? (recordings.count == 1 ? "The recording is" : "The recordings are")
        : "The transcripts so far (where there is one) and the recordings are"
    alert.informativeText =
      "\(what) in \(folders.map { "“\($0)”" }.joined(separator: " and ")). The recording's name ends in “recovered”.\n\n"
      + "Troubleshooting in the README shows how to rebuild a transcript from a recording (--reprocess)."
    alert.addButton(withTitle: "Show in Finder")
    alert.addButton(withTitle: "OK")
    NSApp.activate()
    if alert.runModal() == .alertFirstButtonReturn {
      NSWorkspace.shared.activateFileViewerSelecting(transcripts + recordings)
    }
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
  @ObservedObject var updates = UpdateChecker.shared
  @Environment(\.openSettings) private var openSettings

  var body: some View {
    switch session.phase {
    case .idle:
      Button(settings.menuTitle(for: .translate)) { Task { await session.start(.translate) } }
        .keyboardShortcut("1")
      Button(settings.menuTitle(for: .transcribe)) { Task { await session.start(.transcribe) } }
        .keyboardShortcut("2")
      if let title = settings.secondTranscribeTitle {
        Button(title) { Task { await session.start(.transcribe, secondLanguage: true) } }
          .keyboardShortcut("3")
      }
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
    Button(settings.liveWindowSmall ? "Expand Live Window" : "Minimize Live Window to a Bar") {
      settings.liveWindowSmall.toggle()
      LiveWindowController.shared.show()
    }
    .keyboardShortcut("m")
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
    if let update = updates.available {
      Button("Get Kurzschrift \(update.version)…") { NSWorkspace.shared.open(update.page) }
    }
    Button("Check for Updates…") { Task { await updates.checkAndTell() } }
      .disabled(updates.state == .checking)
    Button("Settings…") {
      NSApp.activate()
      openSettings()
    }
    .keyboardShortcut(",")
    Button("Quit Kurzschrift") { NSApp.terminate(nil) }
      .keyboardShortcut("q")
  }
}
