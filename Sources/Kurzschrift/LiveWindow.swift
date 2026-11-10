import AppKit
import SwiftUI

/// Floating, always-on-top meeting window that stays visible over full-screen Teams/Zoom:
/// the whole transcript, the live minutes, and a caption strip with what is being said right now.
@MainActor
final class LiveWindowController {
  static let shared = LiveWindowController()
  private var panel: NSPanel?

  func show() {
    if panel == nil {
      let panel = NSPanel(
        contentRect: NSRect(x: 0, y: 0, width: 880, height: 580),
        styleMask: [.titled, .closable, .resizable, .utilityWindow, .nonactivatingPanel],
        backing: .buffered, defer: false)
      panel.title = "Kurzschrift"
      panel.level = .floating
      panel.isFloatingPanel = true
      panel.hidesOnDeactivate = false
      panel.isReleasedWhenClosed = false
      panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
      // Takes keyboard focus only when you click into the question field, so typing in Teams
      // never lands here by accident.
      panel.becomesKeyOnlyIfNeeded = true
      let hosting = NSHostingView(rootView: LiveTranscriptView(session: .shared, settings: .shared))
      // The window's size is yours, not the content's: letting wrapped captions and the side pane
      // feed their size back into the window's constraints looped until AppKit gave up and crashed.
      hosting.sizingOptions = []
      panel.contentView = hosting
      panel.contentMinSize = NSSize(width: 360, height: 260)
      panel.setFrameAutosaveName("MeetingWindow")
      if panel.frame.origin == .zero { panel.center() }
      self.panel = panel
      fit(animate: false)
    }
    panel?.orderFrontRegardless()
  }

  /// Sizes the window as the full window or the small bar, whichever the setting says. Each keeps
  /// its own place: the bar goes back to the corner you left it in, the full window to its spot.
  func fit(animate: Bool) {
    guard let panel else { return }
    let defaults = UserDefaults.standard
    let small = AppSettings.shared.liveWindowSmall
    let frameIsSmall = defaults.bool(forKey: "MeetingWindowIsSmall")
    var frame = panel.frame
    if small != frameIsSmall {
      defaults.set(NSStringFromRect(frame), forKey: frameIsSmall ? "MeetingWindowSmallFrame" : "MeetingWindowFullFrame")
      defaults.set(small, forKey: "MeetingWindowIsSmall")
      if let saved = defaults.string(forKey: small ? "MeetingWindowSmallFrame" : "MeetingWindowFullFrame").map(NSRectFromString),
        NSScreen.screens.contains(where: { $0.visibleFrame.intersects(saved) })
      {
        frame = saved
      } else {
        // The first time, it grows or shrinks from its top-left corner.
        let size = small ? NSSize(width: min(frame.width, 620), height: frame.height) : NSSize(width: 880, height: 580)
        frame = NSRect(x: frame.minX, y: frame.maxY - size.height, width: size.width, height: size.height)
      }
    }
    if small {
      let height = Self.barHeight(fontSize: AppSettings.shared.liveFontSize)
      panel.contentMinSize = NSSize(width: 340, height: height)
      panel.contentMaxSize = NSSize(width: 10_000, height: height)
      let content = panel.contentRect(forFrameRect: frame)
      frame = panel.frameRect(forContentRect: NSRect(x: content.minX, y: content.maxY - height, width: content.width, height: height))
    } else {
      panel.contentMinSize = NSSize(width: 360, height: 260)
      panel.contentMaxSize = NSSize(width: 10_000, height: 10_000)
    }
    frame = panel.constrainFrameRect(frame, to: panel.screen)
    if frame != panel.frame { panel.setFrame(frame, display: true, animate: animate) }
  }

  /// One line of original text and two of translation (or two of transcript), plus padding.
  static func barHeight(fontSize: Double) -> CGFloat {
    ((fontSize - 2) * 1.3 + fontSize * 1.3 * 2 + 18).rounded(.up)
  }

  /// Developer hook (kurzschrift://snapshot?path=…): draws this window's own view into a PNG.
  /// Renders only Kurzschrift's view, never the screen, so it needs no screen-recording permission.
  func snapshot(to path: String) {
    guard let view = panel?.contentView,
      let rep = view.bitmapImageRepForCachingDisplay(in: view.bounds)
    else { return }
    view.cacheDisplay(in: view.bounds, to: rep)
    try? rep.representation(using: .png, properties: [:])?.write(to: URL(fileURLWithPath: path))
  }
}

struct LiveTranscriptView: View {
  @ObservedObject var session: SessionController
  @ObservedObject var settings: AppSettings

  private var translating: Bool { session.config?.mode == .translate }
  private var showsSidePane: Bool { session.minutes != nil || session.canAsk || !session.isIdle || session.finished != nil }

  var body: some View {
    if settings.liveWindowSmall {
      SmallBar(session: session, settings: settings, translating: translating)
    } else {
      fullWindow
    }
  }

  private var fullWindow: some View {
    VStack(spacing: 0) {
      header
      Divider()
      if let warning = session.warning {
        Label(warning, systemImage: "exclamationmark.triangle.fill")
          .font(.callout)
          .foregroundStyle(.orange)
          .padding(10)
          .frame(maxWidth: .infinity, alignment: .leading)
          .background(.orange.opacity(0.1))
      }
      if showsSidePane {
        HSplitView {
          TranscriptPane(session: session, translating: translating, fontSize: settings.liveFontSize)
            .frame(minWidth: 280, maxWidth: .infinity, maxHeight: .infinity)
          SidePane(session: session, fontSize: settings.liveFontSize)
            .frame(minWidth: 240, idealWidth: 340, maxWidth: .infinity, maxHeight: .infinity)
        }
      } else {
        TranscriptPane(session: session, translating: translating, fontSize: settings.liveFontSize)
          .frame(maxWidth: .infinity, maxHeight: .infinity)
      }
      if !session.isIdle {
        Divider()
        CaptionStrip(session: session, translating: translating, fontSize: settings.liveFontSize)
      } else if let url = session.lastTranscript {
        Divider()
        savedFooter(url)
      }
    }
    .frame(minWidth: 360, minHeight: 260)
  }

  private var header: some View {
    HStack(spacing: 8) {
      switch session.phase {
      case .idle:
        Image(systemName: "circle").foregroundStyle(.secondary)
        Text("Not recording").foregroundStyle(.secondary)
      case .starting(let message), .finishing(let message):
        ProgressView().controlSize(.small)
        Text(message)
      case .recording:
        Image(systemName: "record.circle.fill").foregroundStyle(.red)
        Text(TranscriptDocument.clock(Double(session.elapsedSeconds))).monospacedDigit()
        if let config = session.config {
          Text(modeDescription(config)).foregroundStyle(.secondary)
        }
      }
      Spacer()
      if let seconds = session.autoStopIn {
        Label("No speech: stopping in \(seconds) s", systemImage: "timer")
          .foregroundStyle(.orange)
          .monospacedDigit()
        Button("Keep Recording") { session.keepRecording() }
      }
      if session.canExport {
        Menu {
          Button("Save as Markdown…") { session.exportWithSavePanel(.markdown) }
          Button("Save as Text…") { session.exportWithSavePanel(.text) }
          Divider()
          Button("Copy to Clipboard") { session.copyToClipboard() }
        } label: {
          Label("Export", systemImage: "square.and.arrow.up")
        }
        .fixedSize(horizontal: true, vertical: false)
        .menuStyle(.button)
        .help("Export the transcript, translation and minutes as they are right now")
      }
      if session.isRecording {
        BookmarkButton(session: session)
        Button("Stop & Save") { Task { await session.stop() } }
      } else if session.isIdle {
        StartMenu(session: session, settings: settings)
      }
      Button {
        settings.liveWindowSmall = true
      } label: {
        Image(systemName: "arrow.down.right.and.arrow.up.left")
      }
      .help("Shrink to a small bar. Recording, transcript and minutes carry on.")
    }
    .padding(.horizontal, 12)
    .padding(.vertical, 8)
  }

  private func modeDescription(_ config: SessionConfig) -> String {
    var spoken = AppSettings.languageName(config.spokenLocale.identifier)
    if let second = config.secondLocale { spoken += " + " + AppSettings.languageName(second.identifier) }
    if config.mode == .translate, let target = config.targetLanguage {
      return "Translating \(spoken) → \(AppSettings.languageName(target.minimalIdentifier))"
    }
    return "Transcribing \(spoken)"
  }

  private func savedFooter(_ url: URL) -> some View {
    HStack {
      Image(systemName: "checkmark.circle.fill").foregroundStyle(.green)
      Text("Saved \(url.lastPathComponent)").lineLimit(1).truncationMode(.middle)
      Spacer()
      Button("Show in Finder") { NSWorkspace.shared.activateFileViewerSelecting([url]) }
      Button("Open") { NSWorkspace.shared.open(url) }
    }
    .font(.callout)
    .padding(10)
  }
}

private struct StartMenu: View {
  @ObservedObject var session: SessionController
  @ObservedObject var settings: AppSettings

  var body: some View {
    Menu("Start") {
      ForEach(MeetingMode.allCases) { mode in
        Button(settings.menuTitle(for: mode)) { Task { await session.start(mode) } }
      }
    }
    .fixedSize(horizontal: true, vertical: false)
    .menuStyle(.button)
  }
}

private struct BookmarkButton: View {
  @ObservedObject var session: SessionController

  var body: some View {
    Button {
      session.addBookmark()
    } label: {
      Image(systemName: "star")
    }
    .help("Bookmark this moment (⌃⌥B)")
  }
}

// MARK: - Small bar

/// The window shrunk out of the way: the clock, what is being said right now, and the buttons you
/// need mid-meeting. Transcript, minutes and the saved file keep going as in the full window.
private struct SmallBar: View {
  @ObservedObject var session: SessionController
  @ObservedObject var settings: AppSettings
  let translating: Bool

  private var fontSize: Double { settings.liveFontSize }

  var body: some View {
    HStack(spacing: 10) {
      status
      caption
        .frame(maxWidth: .infinity, alignment: .leading)
      buttons
    }
    .padding(.horizontal, 12)
    .frame(maxWidth: .infinity, maxHeight: .infinity)
  }

  /// The newest in-progress text, or the last finished line while nobody is mid-sentence.
  private var current: (label: String?, original: String, translation: String?)? {
    for source in AudioSourceKind.allCases {
      guard let partial = session.livePartials[source], !partial.original.isEmpty else { continue }
      let who = session.config?.includeMic == true ? (source == .microphone ? (session.config?.myName ?? "Me") : "Others") : nil
      let language = partial.language.map { AppSettings.languageName($0) }
      let label = [who, language].compactMap { $0 }.joined(separator: " · ")
      return (label.isEmpty ? nil : label, partial.original, partial.translation)
    }
    if let last = session.entries.last(where: { !$0.isBookmark }) {
      return (last.speaker, last.original, last.translation)
    }
    return nil
  }

  private var status: some View {
    VStack(alignment: .leading, spacing: 2) {
      switch session.phase {
      case .idle:
        Image(systemName: "circle").foregroundStyle(.secondary)
      case .starting, .finishing:
        ProgressView().controlSize(.small)
      case .recording:
        HStack(spacing: 4) {
          Image(systemName: "record.circle.fill").foregroundStyle(.red)
          Text(TranscriptDocument.clock(Double(session.elapsedSeconds))).monospacedDigit()
          if let warning = session.warning {
            Image(systemName: "exclamationmark.triangle.fill").foregroundStyle(.orange).help(warning)
          }
        }
        if let label = current?.label {
          Text(label).font(.caption).foregroundStyle(.secondary).lineLimit(1)
        }
      }
    }
    .frame(maxWidth: 150, alignment: .leading)
    .fixedSize(horizontal: true, vertical: false)
  }

  @ViewBuilder private var caption: some View {
    if let seconds = session.autoStopIn {
      Label("No speech: stopping in \(seconds) s", systemImage: "timer")
        .foregroundStyle(.orange)
        .monospacedDigit()
    } else {
      switch session.phase {
      case .idle:
        Text(session.lastTranscript.map { "Saved \($0.lastPathComponent)" } ?? "Not recording")
          .foregroundStyle(.secondary)
          .lineLimit(2)
          .truncationMode(.middle)
      case .starting(let message), .finishing(let message):
        Text(message).foregroundStyle(.secondary)
      case .recording:
        if let current {
          VStack(alignment: .leading, spacing: 2) {
            if translating {
              Text(current.original)
                .font(.system(size: fontSize - 2))
                .foregroundStyle(.secondary)
                .lineLimit(1)
                .truncationMode(.head)
              Text(current.translation ?? "…")
                .font(.system(size: fontSize, weight: .medium))
                .lineLimit(2)
                .truncationMode(.head)
            } else {
              Text(current.original)
                .font(.system(size: fontSize, weight: .medium))
                .lineLimit(2)
                .truncationMode(.head)
            }
          }
        } else {
          Text("Listening…").foregroundStyle(.tertiary)
        }
      }
    }
  }

  private var buttons: some View {
    HStack(spacing: 6) {
      if session.isRecording {
        if session.autoStopIn != nil {
          Button("Keep Recording") { session.keepRecording() }
        }
        BookmarkButton(session: session)
        Button("Stop & Save") { Task { await session.stop() } }
      } else if session.isIdle {
        StartMenu(session: session, settings: settings)
      }
      Button {
        settings.liveWindowSmall = false
      } label: {
        Image(systemName: "arrow.up.left.and.arrow.down.right")
      }
      .help("Show the full window: transcript, minutes and questions")
    }
    .fixedSize()
  }
}

// MARK: - Transcript

/// The whole meeting so far. Follows new lines; once you scroll up to read back it stays put
/// until you scroll to the bottom again or press "Jump to latest".
private struct TranscriptPane: View {
  @ObservedObject var session: SessionController
  let translating: Bool
  let fontSize: Double
  @State private var following = true
  @State private var userScrolling = false

  var body: some View {
    ScrollViewReader { proxy in
      ScrollView {
        LazyVStack(alignment: .leading, spacing: 14) {
          if session.entries.isEmpty {
            Text(session.isIdle ? "Start a meeting from the menu bar or the Start button." : "The transcript appears here as people speak.")
              .foregroundStyle(.tertiary)
          }
          ForEach(session.entries) { entry in
            EntryView(entry: entry, translating: translating, fontSize: fontSize, session: session)
          }
          Color.clear.frame(height: 1).id("bottom")
        }
        .textSelection(.enabled)
        .padding(14)
      }
      // Only scrolling done by the user changes whether the pane follows; new lines arriving and
      // the pane's own scrolling to them don't.
      .onScrollPhaseChange { _, phase in
        userScrolling = phase == .tracking || phase == .interacting || phase == .decelerating
      }
      .onScrollGeometryChange(for: Bool.self) { g in
        g.contentOffset.y + g.containerSize.height >= g.contentSize.height - 40
      } action: { _, atBottom in
        if userScrolling { following = atBottom }
      }
      .onChange(of: session.entries.count) {
        guard following else { return }
        DispatchQueue.main.async { proxy.scrollTo("bottom", anchor: .bottom) }
      }
      .overlay(alignment: .bottomTrailing) {
        if !following {
          Button {
            following = true
            proxy.scrollTo("bottom", anchor: .bottom)
          } label: {
            Label("Jump to latest", systemImage: "arrow.down")
          }
          .buttonStyle(.borderedProminent)
          .controlSize(.small)
          .shadow(radius: 2)
          .padding(10)
        }
      }
    }
  }
}

private struct EntryView: View {
  let entry: SessionController.LiveEntry
  let translating: Bool
  let fontSize: Double
  @ObservedObject var session: SessionController

  var body: some View {
    if entry.isBookmark {
      Label("Bookmark · \(TranscriptDocument.clock(entry.start))", systemImage: "star.fill")
        .font(.system(size: max(10, fontSize - 2), weight: .semibold))
        .foregroundStyle(.orange)
        .frame(maxWidth: .infinity, alignment: .leading)
    } else {
      text
    }
  }

  private var text: some View {
    VStack(alignment: .leading, spacing: 4) {
      HStack(spacing: 6) {
        if let speaker = entry.speaker { SpeakerLabel(name: speaker, session: session) }
        Text(TranscriptDocument.clock(entry.start)).foregroundStyle(.tertiary).monospacedDigit()
      }
      .font(.system(size: max(10, fontSize - 3)))
      if translating {
        Text(entry.original).font(.system(size: fontSize - 1)).foregroundStyle(.secondary)
        Text(entry.translation ?? "…").font(.system(size: fontSize))
      } else {
        Text(entry.original).font(.system(size: fontSize))
      }
    }
    .frame(maxWidth: .infinity, alignment: .leading)
  }
}

/// After a meeting, click a speaker's name to rename them everywhere (and optionally remember the
/// voice for next time).
private struct SpeakerLabel: View {
  let name: String
  @ObservedObject var session: SessionController
  @State private var editing = false
  @State private var newName = ""
  @State private var remember = true

  var body: some View {
    if let finished = session.finished {
      Button(name) {
        newName = name.hasPrefix("Speaker ") || name == "Unknown speaker" ? "" : name
        editing = true
      }
      .buttonStyle(.plain)
      .fontWeight(.semibold)
      .foregroundStyle(Color.accentColor)
      .help("Rename this speaker")
      .popover(isPresented: $editing, arrowEdge: .bottom) {
        VStack(alignment: .leading, spacing: 10) {
          Text("Rename “\(name)”").font(.headline)
          TextField("Name", text: $newName).textFieldStyle(.roundedBorder).onSubmit(apply)
          let used = Set(finished.turns.compactMap(\.speaker))
          let suggestions = session.context.names.filter { !used.contains($0) }
          if !suggestions.isEmpty {
            Text("Participants").font(.caption).foregroundStyle(.secondary)
            ForEach(suggestions.prefix(8), id: \.self) { suggestion in
              Button(suggestion) { newName = suggestion }.buttonStyle(.link)
            }
          }
          Toggle("Remember this voice for next time", isOn: $remember)
            .disabled(finished.voices[name] == nil)
          HStack {
            Spacer()
            Button("Rename", action: apply)
              .keyboardShortcut(.defaultAction)
              .disabled(newName.trimmingCharacters(in: .whitespaces).isEmpty)
          }
        }
        .padding(14)
        .frame(width: 300)
      }
    } else {
      Text(name).fontWeight(.semibold)
    }
  }

  private func apply() {
    session.renameSpeaker(name, to: newName, remember: remember)
    editing = false
  }
}

// MARK: - Live captions

/// What is being said right now, per source: the recognizer's text as it comes in, and the
/// translation underneath when translating.
private struct CaptionStrip: View {
  @ObservedObject var session: SessionController
  let translating: Bool
  let fontSize: Double

  var body: some View {
    let active = AudioSourceKind.allCases.compactMap { source in
      session.livePartials[source].flatMap { $0.original.isEmpty ? nil : (source, $0) }
    }
    VStack(alignment: .leading, spacing: 8) {
      if active.isEmpty {
        Text(session.isRecording ? "Listening…" : " ")
          .font(.system(size: fontSize + 2))
          .foregroundStyle(.tertiary)
      }
      ForEach(active, id: \.0) { source, partial in
        VStack(alignment: .leading, spacing: 2) {
          let who = session.config?.includeMic == true ? (source == .microphone ? (session.config?.myName ?? "Me") : "Others") : nil
          let language = partial.language.map { AppSettings.languageName($0) }
          let label = [who, language].compactMap { $0 }.joined(separator: " · ")
          if !label.isEmpty {
            Text(label).font(.caption).foregroundStyle(.secondary)
          }
          Text(partial.original)
            .font(.system(size: translating ? fontSize + 1 : fontSize + 5, weight: translating ? .regular : .medium))
            .foregroundStyle(translating ? .secondary : .primary)
            .lineLimit(translating ? 2 : 3)
            .truncationMode(.head)
          if translating {
            Text(partial.translation ?? "…")
              .font(.system(size: fontSize + 5, weight: .medium))
              .lineLimit(3)
              .truncationMode(.head)
          }
        }
      }
    }
    .frame(maxWidth: .infinity, minHeight: (fontSize + 5) * 3.2, alignment: .bottomLeading)
    .padding(.horizontal, 14)
    .padding(.vertical, 10)
    .background(Color.primary.opacity(0.04))
  }
}

// MARK: - Minutes and questions

private struct SidePane: View {
  @ObservedObject var session: SessionController
  let fontSize: Double
  /// Kept across launches, so the window opens on the tab you used last.
  @AppStorage("liveSideTab") private var tab = Tab.minutes

  enum Tab: String { case minutes, ask, context }

  var body: some View {
    var tabs: [Tab] = []
    if session.minutes != nil { tabs.append(.minutes) }
    if session.canAsk || !session.questions.isEmpty { tabs.append(.ask) }
    if !session.isIdle || session.finished != nil { tabs.append(.context) }
    let shown = tabs.contains(tab) ? tab : tabs.first ?? .ask
    return VStack(spacing: 0) {
      if tabs.count > 1 {
        Picker("", selection: Binding(get: { shown }, set: { tab = $0 })) {
          ForEach(tabs, id: \.self) { tab in
            switch tab {
            case .minutes: Text(session.minutes?.usesAI == false ? "Notes" : "Minutes").tag(tab)
            case .ask: Text("Ask").tag(tab)
            case .context: Text("Context").tag(tab)
            }
          }
        }
        .pickerStyle(.segmented)
        .labelsHidden()
        .padding(8)
        Divider()
      }
      switch shown {
      case .minutes: MinutesPane(session: session, fontSize: fontSize)
      case .ask: AskPane(session: session, fontSize: fontSize)
      case .context: ContextPane(session: session, fontSize: fontSize)
      }
    }
  }
}

/// Who's in the meeting and what it's about. The minutes, your questions and speech recognition
/// use it, and it's saved with the transcript.
private struct ContextPane: View {
  @ObservedObject var session: SessionController
  let fontSize: Double

  private var finishing: Bool {
    if case .finishing = session.phase { return true }
    return false
  }

  var body: some View {
    VStack(spacing: 0) {
      HStack(spacing: 6) {
        Text("Context").font(.headline)
        Spacer()
        if session.canRedoMinutes {
          Button {
            session.redoMinutes()
          } label: {
            Label("Redo Minutes", systemImage: "arrow.clockwise")
          }
          .controlSize(.small)
          .help("Write the minutes again from the whole transcript, with this context")
        }
      }
      .padding(.horizontal, 12)
      .padding(.vertical, 8)
      Divider()
      VStack(alignment: .leading, spacing: 6) {
        Text("Participants").font(.subheadline.weight(.semibold))
        field(
          $session.context.participants,
          placeholder: "One per line, with a role if it helps:\nMaria Huber – project lead\nJonas Berger – IT")
        Text("About the meeting").font(.subheadline.weight(.semibold)).padding(.top, 6)
        field($session.context.about, placeholder: "Topic, agenda, background, abbreviations…")
        Text(footnote)
          .font(.caption)
          .foregroundStyle(.secondary)
          .fixedSize(horizontal: false, vertical: true)
          .padding(.top, 2)
      }
      .padding(12)
      .disabled(finishing)
    }
  }

  private func field(_ text: Binding<String>, placeholder: String) -> some View {
    TextEditor(text: text)
      .font(.system(size: fontSize - 1))
      .scrollContentBackground(.hidden)
      .padding(4)
      .background(Color(nsColor: .textBackgroundColor), in: RoundedRectangle(cornerRadius: 6))
      .overlay(RoundedRectangle(cornerRadius: 6).stroke(.separator))
      .overlay(alignment: .topLeading) {
        if text.wrappedValue.isEmpty {
          Text(placeholder)
            .font(.system(size: fontSize - 1))
            .foregroundStyle(.tertiary)
            .padding(.horizontal, 9)
            .padding(.vertical, 4)
            .allowsHitTesting(false)
        }
      }
      .frame(minHeight: 70, maxHeight: .infinity)
  }

  private var footnote: String {
    if !session.isIdle {
      return "The minutes and your questions use this from their next update, and speech recognition listens for the names. It's saved with the transcript."
    }
    return session.minutes?.usesAI == true
      ? "Saved with the transcript and used for your questions. After a change, Redo Minutes writes the minutes again with it."
      : "Saved with the transcript and used for your questions."
  }
}

/// Questions about the meeting, answered from the transcript so far.
private struct AskPane: View {
  @ObservedObject var session: SessionController
  let fontSize: Double
  @State private var question = ""

  var body: some View {
    VStack(spacing: 0) {
      ScrollViewReader { proxy in
        ScrollView {
          LazyVStack(alignment: .leading, spacing: 16) {
            if session.questions.isEmpty {
              Text("Ask anything about the meeting so far, for example “What did they decide about the budget?” or “What do I need to do?”")
                .foregroundStyle(.tertiary)
            }
            ForEach(session.questions) { item in
              VStack(alignment: .leading, spacing: 6) {
                Text(item.question).font(.system(size: fontSize - 1, weight: .semibold))
                if let answer = item.answer {
                  MarkdownView(text: answer, fontSize: fontSize - 1)
                } else if let error = item.error {
                  Label(error, systemImage: "exclamationmark.triangle.fill").foregroundStyle(.orange).font(.callout)
                } else {
                  ProgressView().controlSize(.small)
                }
              }
              .id(item.id)
            }
          }
          .textSelection(.enabled)
          .frame(maxWidth: .infinity, alignment: .leading)
          .padding(12)
        }
        .onChange(of: session.questions.count) {
          if let last = session.questions.last { proxy.scrollTo(last.id, anchor: .bottom) }
        }
      }
      Divider()
      HStack {
        TextField("Ask about this meeting…", text: $question)
          .textFieldStyle(.roundedBorder)
          .onSubmit(send)
        Button("Ask", action: send).disabled(question.trimmingCharacters(in: .whitespaces).isEmpty)
      }
      .padding(10)
    }
  }

  private func send() {
    session.ask(question)
    question = ""
  }
}

private struct MinutesPane: View {
  @ObservedObject var session: SessionController
  let fontSize: Double

  private var editing: Bool { session.minutesDraft != nil }
  /// Live minutes are off: only your own notes.
  private var notesOnly: Bool { session.minutes?.usesAI == false }
  /// Not while the final minutes are being written.
  private var canEdit: Bool { session.isRecording || (session.isIdle && session.finished != nil) }

  var body: some View {
    let state = session.minutes
    VStack(spacing: 0) {
      HStack(spacing: 6) {
        Text(notesOnly ? "Notes" : "Minutes").font(.headline)
        if state?.isUpdating == true { ProgressView().controlSize(.small) }
        Spacer()
        if editing {
          Button("Cancel") { session.cancelEditingMinutes() }
            .controlSize(.small)
          Button("Done") { session.finishEditingMinutes() }
            .controlSize(.small)
            .keyboardShortcut(.return, modifiers: .command)
        } else {
          if session.isRecording && !notesOnly {
            Button("Update now") { session.updateMinutesNow() }
              .controlSize(.small)
              .disabled(state?.isUpdating == true)
          }
          if session.canStartAIMinutes {
            Button {
              session.startAIMinutes()
            } label: {
              Label("AI Minutes", systemImage: "sparkles")
            }
            .controlSize(.small)
            .help(
              session.isRecording
                ? "Have AI write minutes from the transcript so far and keep them updated. Your notes are kept and worked in."
                : "Have AI write minutes from the whole transcript. Your notes are kept and worked in.")
          }
          Button {
            session.beginEditingMinutes()
          } label: {
            Image(systemName: "pencil")
          }
          .controlSize(.small)
          .help("Edit the minutes or add your own notes")
          .disabled(!canEdit)
          RemindersButton(session: session, minutes: state?.text ?? "")
          Button {
            NSPasteboard.general.clearContents()
            NSPasteboard.general.setString(state?.text ?? "", forType: .string)
          } label: {
            Image(systemName: "doc.on.doc")
          }
          .controlSize(.small)
          .help(notesOnly ? "Copy notes" : "Copy minutes")
          .disabled(state?.text.isEmpty ?? true)
        }
      }
      .padding(.horizontal, 12)
      .padding(.vertical, 8)
      Divider()
      if editing {
        TextEditor(text: Binding(get: { session.minutesDraft ?? "" }, set: { session.minutesDraft = $0 }))
          .font(.system(size: fontSize - 1))
          .scrollContentBackground(.hidden)
          .padding(8)
        Text(
          notesOnly
            ? "Your notes are saved with the transcript."
            : session.isRecording
              ? "Correct anything or add your own notes. The next update works your changes in, tidied up and filled in from the transcript, and won't bring back what you delete. Updates wait while you edit."
              : "Your changes go straight into the transcript file."
        )
        .font(.caption)
        .foregroundStyle(.secondary)
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(.horizontal, 12)
        .padding(.bottom, 8)
      } else {
        ScrollView {
          VStack(alignment: .leading, spacing: 0) {
            if let text = state?.text, !text.isEmpty {
              MarkdownView(text: text, fontSize: fontSize - 1)
            } else if notesOnly && session.canStartAIMinutes {
              Text(
                session.isRecording
                  ? "Press AI Minutes to have minutes written from the transcript so far and updated every \(AppSettings.shared.minutesIntervalMinutes) min, or take your own notes with the pencil button."
                  : "Press AI Minutes to have minutes written from the whole transcript, or take your own notes with the pencil button."
              )
              .foregroundStyle(.tertiary)
            } else if notesOnly {
              Text("Take notes with the pencil button; they're saved with the transcript. Add an API key in Settings → Minutes and an AI Minutes button appears here.")
                .foregroundStyle(.tertiary)
            } else {
              Text(
                "The first minutes appear after the first update interval, once enough has been said. You can also start them yourself with the pencil button."
                  + (session.context.isEmpty ? " Under Context, say who's in the meeting and what it's about, and the minutes use it." : "")
              )
              .foregroundStyle(.tertiary)
            }
          }
          .textSelection(.enabled)
          .frame(maxWidth: .infinity, alignment: .leading)
          .padding(12)
        }
      }
      if !notesOnly {
        Divider()
        MinutesStatusLine(state: state, recording: session.isRecording, editing: editing)
          .padding(.horizontal, 12)
          .padding(.vertical, 6)
      }
    }
  }
}

/// Adds the minutes' action items to Apple Reminders, after letting you pick which.
private struct RemindersButton: View {
  @ObservedObject var session: SessionController
  let minutes: String
  @State private var showing = false
  @State private var selected: Set<UUID> = []
  @State private var items: [CalendarContext.ActionItem] = []
  @State private var status: String?

  var body: some View {
    Button {
      items = CalendarContext.actionItems(in: minutes)
      selected = Set(items.map(\.id))
      status = nil
      showing = true
    } label: {
      Image(systemName: "checklist")
    }
    .controlSize(.small)
    .help("Add action items to Reminders")
    .disabled(CalendarContext.actionItems(in: minutes).isEmpty)
    .popover(isPresented: $showing, arrowEdge: .bottom) {
      VStack(alignment: .leading, spacing: 8) {
        Text("Add to Reminders").font(.headline)
        ForEach(items) { item in
          Toggle(item.line, isOn: Binding(
            get: { selected.contains(item.id) },
            set: { on in if on { selected.insert(item.id) } else { selected.remove(item.id) } }))
        }
        if let status { Text(status).font(.callout).foregroundStyle(.secondary) }
        HStack {
          Spacer()
          Button("Add \(selected.count)") { Task { await add() } }
            .keyboardShortcut(.defaultAction)
            .disabled(selected.isEmpty)
        }
      }
      .padding(14)
      .frame(width: 380)
    }
  }

  private func add() async {
    let chosen = items.filter { selected.contains($0.id) }
    let title = session.finished?.document.baseName ?? session.config?.eventTitle ?? "a meeting"
    do {
      let list = try await CalendarContext.addReminders(
        chosen, meetingTitle: title, myName: AppSettings.shared.myName)
      status = "Added \(chosen.count) to “\(list)”."
      selected = []
    } catch {
      status = error.localizedDescription
    }
  }
}

private struct MinutesStatusLine: View {
  let state: MinutesState?
  let recording: Bool
  let editing: Bool

  var body: some View {
    TimelineView(.periodic(from: .now, by: 1)) { context in
      HStack(spacing: 6) {
        if let error = state?.error {
          Image(systemName: "exclamationmark.triangle.fill").foregroundStyle(.orange)
          Text(error).lineLimit(2)
        } else if recording && editing {
          Text("Updates wait while you edit")
        } else if recording && state?.editsPending == true {
          Text("Your edits go into the next update")
        } else if let updated = state?.updatedAt {
          Text("Updated \(updated.formatted(date: .omitted, time: .shortened))")
        }
        Spacer()
        if recording, !editing, let next = state?.nextUpdate, state?.isUpdating != true {
          let seconds = max(0, Int(next.timeIntervalSince(context.date)))
          Text("next in \(seconds / 60):\(String(format: "%02d", seconds % 60))").monospacedDigit()
        }
        if let cost = state?.cost {
          Text(cost < 0.01 ? "< $0.01" : String(format: "≈ $%.2f", cost)).help("Estimated API cost of this meeting's minutes")
        }
      }
      .font(.caption)
      .foregroundStyle(.secondary)
    }
  }
}

/// Just enough Markdown for minutes: ### headings, bullets, task checkboxes and inline formatting.
struct MarkdownView: View {
  let text: String
  let fontSize: Double

  var body: some View {
    VStack(alignment: .leading, spacing: 5) {
      ForEach(Array(text.components(separatedBy: "\n").enumerated()), id: \.offset) { _, raw in
        line(raw.trimmingCharacters(in: .whitespaces))
      }
    }
  }

  @ViewBuilder
  private func line(_ line: String) -> some View {
    if line.isEmpty {
      Spacer().frame(height: 2)
    } else if let heading = Self.strip(line, prefixes: ["### ", "## ", "# "]) {
      inline(heading).font(.system(size: fontSize + 1, weight: .semibold)).padding(.top, 6)
    } else if let task = Self.strip(line, prefixes: ["- [ ] ", "* [ ] "]) {
      bullet("square", task)
    } else if let done = Self.strip(line, prefixes: ["- [x] ", "- [X] "]) {
      bullet("checkmark.square", done)
    } else if let item = Self.strip(line, prefixes: ["- ", "* ", "• "]) {
      bullet("circle.fill", item, small: true)
    } else {
      inline(line).font(.system(size: fontSize))
    }
  }

  private func bullet(_ symbol: String, _ content: String, small: Bool = false) -> some View {
    HStack(alignment: .firstTextBaseline, spacing: 6) {
      Image(systemName: symbol)
        .font(.system(size: small ? fontSize * 0.35 : fontSize * 0.8))
        .foregroundStyle(.secondary)
      inline(content).font(.system(size: fontSize))
    }
  }

  private func inline(_ string: String) -> Text {
    let options = AttributedString.MarkdownParsingOptions(interpretedSyntax: .inlineOnlyPreservingWhitespace)
    return Text((try? AttributedString(markdown: string, options: options)) ?? AttributedString(string))
  }

  private static func strip(_ line: String, prefixes: [String]) -> String? {
    prefixes.first { line.hasPrefix($0) }.map { String(line.dropFirst($0.count)) }
  }
}
