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
      panel.contentView = NSHostingView(rootView: LiveTranscriptView(session: .shared, settings: .shared))
      panel.setFrameAutosaveName("MeetingWindow")
      if panel.frame.origin == .zero { panel.center() }
      self.panel = panel
    }
    panel?.orderFrontRegardless()
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
  private var showsSidePane: Bool { session.minutes != nil || session.canAsk }

  var body: some View {
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
        Button {
          session.addBookmark()
        } label: {
          Image(systemName: "star")
        }
        .help("Bookmark this moment (⌃⌥B)")
        Button("Stop & Save") { Task { await session.stop() } }
      } else if session.isIdle {
        Menu("Start") {
          ForEach(MeetingMode.allCases) { mode in
            Button(settings.menuTitle(for: mode)) { Task { await session.start(mode) } }
          }
        }
        .fixedSize(horizontal: true, vertical: false)
        .menuStyle(.button)
      }
    }
    .padding(.horizontal, 12)
    .padding(.vertical, 8)
  }

  private func modeDescription(_ config: SessionConfig) -> String {
    let spoken = AppSettings.languageName(config.spokenLocale.identifier)
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
          let suggestions = finished.config.attendees.filter { !used.contains($0) }
          if !suggestions.isEmpty {
            Text("From the calendar invite").font(.caption).foregroundStyle(.secondary)
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
          if session.config?.includeMic == true {
            Text(source == .microphone ? (session.config?.myName ?? "Me") : "Others")
              .font(.caption).foregroundStyle(.secondary)
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
  @State private var tab = 0

  var body: some View {
    let hasMinutes = session.minutes != nil
    let canAsk = session.canAsk || !session.questions.isEmpty
    VStack(spacing: 0) {
      if hasMinutes && canAsk {
        Picker("", selection: $tab) {
          Text("Minutes").tag(0)
          Text("Ask").tag(1)
        }
        .pickerStyle(.segmented)
        .labelsHidden()
        .padding(8)
        Divider()
      }
      if hasMinutes && (tab == 0 || !canAsk) {
        MinutesPane(session: session, fontSize: fontSize)
      } else {
        AskPane(session: session, fontSize: fontSize)
      }
    }
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

  var body: some View {
    let state = session.minutes
    VStack(spacing: 0) {
      HStack(spacing: 6) {
        Text("Minutes").font(.headline)
        if state?.isUpdating == true { ProgressView().controlSize(.small) }
        Spacer()
        if session.isRecording {
          Button("Update now") { session.updateMinutesNow() }
            .controlSize(.small)
            .disabled(state?.isUpdating == true)
        }
        RemindersButton(session: session, minutes: state?.text ?? "")
        Button {
          NSPasteboard.general.clearContents()
          NSPasteboard.general.setString(state?.text ?? "", forType: .string)
        } label: {
          Image(systemName: "doc.on.doc")
        }
        .controlSize(.small)
        .help("Copy minutes")
        .disabled(state?.text.isEmpty ?? true)
      }
      .padding(.horizontal, 12)
      .padding(.vertical, 8)
      Divider()
      ScrollView {
        VStack(alignment: .leading, spacing: 0) {
          if let text = state?.text, !text.isEmpty {
            MarkdownView(text: text, fontSize: fontSize - 1)
          } else {
            Text("The first minutes appear after the first update interval, once enough has been said.")
              .foregroundStyle(.tertiary)
          }
        }
        .textSelection(.enabled)
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(12)
      }
      Divider()
      MinutesStatusLine(state: state, recording: session.isRecording)
        .padding(.horizontal, 12)
        .padding(.vertical, 6)
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

  var body: some View {
    TimelineView(.periodic(from: .now, by: 1)) { context in
      HStack(spacing: 6) {
        if let error = state?.error {
          Image(systemName: "exclamationmark.triangle.fill").foregroundStyle(.orange)
          Text(error).lineLimit(2)
        } else if let updated = state?.updatedAt {
          Text("Updated \(updated.formatted(date: .omitted, time: .shortened))")
        }
        Spacer()
        if recording, let next = state?.nextUpdate, state?.isUpdating != true {
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
