import AppKit
import SwiftUI

/// Floating, always-on-top transcript window that stays visible over full-screen Teams/Zoom.
@MainActor
final class LiveWindowController {
  static let shared = LiveWindowController()
  private var panel: NSPanel?

  func show() {
    if panel == nil {
      let panel = NSPanel(
        contentRect: NSRect(x: 0, y: 0, width: 560, height: 380),
        styleMask: [.titled, .closable, .resizable, .utilityWindow, .nonactivatingPanel],
        backing: .buffered, defer: false)
      panel.title = "Kurzschrift"
      panel.level = .floating
      panel.isFloatingPanel = true
      panel.hidesOnDeactivate = false
      panel.isReleasedWhenClosed = false
      panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
      panel.contentView = NSHostingView(
        rootView: LiveTranscriptView(session: .shared, settings: .shared))
      panel.setFrameAutosaveName("LiveTranscriptWindow")
      if panel.frame.origin == .zero { panel.center() }
      self.panel = panel
    }
    panel?.orderFrontRegardless()
  }
}

struct LiveTranscriptView: View {
  @ObservedObject var session: SessionController
  @ObservedObject var settings: AppSettings

  private var translating: Bool { session.config?.mode == .translate }

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
      transcript
      if session.isIdle, let url = session.lastTranscript {
        Divider()
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
    .frame(minWidth: 320, minHeight: 180)
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
          Text(config.mode == .translate ? "Translating" : "Transcribing").foregroundStyle(.secondary)
        }
      }
      Spacer()
      if session.isRecording {
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

  private var transcript: some View {
    ScrollViewReader { proxy in
      ScrollView {
        LazyVStack(alignment: .leading, spacing: 14) {
          ForEach(session.entries) { entry in
            EntryView(
              entry: entry, translating: translating, showOriginal: settings.translateShowOriginalLive,
              fontSize: settings.liveFontSize)
          }
          ForEach(AudioSourceKind.allCases, id: \.self) { source in
            if let text = session.partials[source], !text.isEmpty {
              Text(text)
                .font(.system(size: settings.liveFontSize))
                .foregroundStyle(.secondary)
                .italic()
                .frame(maxWidth: .infinity, alignment: .leading)
            }
          }
          Color.clear.frame(height: 1).id("bottom")
        }
        .textSelection(.enabled)
        .padding(14)
      }
      .onChange(of: session.entries.count) { proxy.scrollTo("bottom", anchor: .bottom) }
      .onChange(of: session.partials) { proxy.scrollTo("bottom", anchor: .bottom) }
    }
  }
}

private struct EntryView: View {
  let entry: SessionController.LiveEntry
  let translating: Bool
  let showOriginal: Bool
  let fontSize: Double

  var body: some View {
    VStack(alignment: .leading, spacing: 4) {
      HStack(spacing: 6) {
        if let speaker = entry.speaker { Text(speaker).fontWeight(.semibold) }
        Text(TranscriptDocument.clock(entry.start)).foregroundStyle(.tertiary).monospacedDigit()
      }
      .font(.system(size: max(10, fontSize - 3)))
      if translating {
        if showOriginal {
          Text(entry.original).font(.system(size: fontSize - 1)).foregroundStyle(.secondary)
        }
        Text(entry.translation ?? "…").font(.system(size: fontSize))
      } else {
        Text(entry.original).font(.system(size: fontSize))
      }
    }
    .frame(maxWidth: .infinity, alignment: .leading)
  }
}
