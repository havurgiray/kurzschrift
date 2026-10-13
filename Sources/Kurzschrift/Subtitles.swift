import AppKit
import SwiftUI

/// TV-style subtitles: a borderless bar along the bottom (or top) of the screen that clicks pass
/// through, visible over full-screen apps, shown only while recording.
@MainActor
final class SubtitleController {
  static let shared = SubtitleController()
  private var panel: NSPanel?

  /// Shows or hides the bar to match the setting and the session state.
  func update() {
    let settings = AppSettings.shared
    let session = SessionController.shared
    guard settings.subtitles, !session.isIdle else {
      panel?.orderOut(nil)
      return
    }
    if panel == nil {
      let panel = NSPanel(
        contentRect: .zero, styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
      panel.isOpaque = false
      panel.backgroundColor = .clear
      panel.hasShadow = false
      panel.ignoresMouseEvents = true
      panel.level = .screenSaver
      panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary, .ignoresCycle]
      panel.contentView = NSHostingView(rootView: SubtitleView(session: session, settings: settings))
      self.panel = panel
    }
    position()
    panel?.orderFrontRegardless()
  }

  /// Developer hook (kurzschrift://snapshot?window=subtitles&path=…): draws the bar's own view.
  func snapshot(to path: String) {
    guard let view = panel?.contentView, let rep = view.bitmapImageRepForCachingDisplay(in: view.bounds) else { return }
    view.cacheDisplay(in: view.bounds, to: rep)
    try? rep.representation(using: .png, properties: [:])?.write(to: URL(fileURLWithPath: path))
  }

  func position() {
    guard let panel, let screen = NSScreen.main else { return }
    let visible = screen.visibleFrame
    let width = min(visible.width * 0.8, 1400)
    let height: CGFloat = 220
    let x = visible.midX - width / 2
    let y = AppSettings.shared.subtitlesAtTop ? visible.maxY - height - 20 : visible.minY + 30
    panel.setFrame(NSRect(x: x, y: y, width: width, height: height), display: true)
  }
}

private struct SubtitleView: View {
  @ObservedObject var session: SessionController
  @ObservedObject var settings: AppSettings

  private var translating: Bool { session.config?.mode == .translate }

  /// The newest in-progress text, or the last finished line while nobody is mid-sentence.
  private var current: (original: String, translation: String?)? {
    if let live = AudioSourceKind.allCases.compactMap({ session.livePartials[$0] }).first(where: { !$0.original.isEmpty }) {
      return (live.original, live.translation)
    }
    if let last = session.entries.last(where: { !$0.isBookmark }) {
      return (last.original, last.translation)
    }
    return nil
  }

  var body: some View {
    VStack {
      if !settings.subtitlesAtTop { Spacer(minLength: 0) }
      if let current {
        VStack(spacing: 4) {
          if translating {
            Text(current.original)
              .font(.system(size: settings.subtitleFontSize * 0.6))
              .foregroundStyle(.white.opacity(0.75))
              .lineLimit(1)
              .truncationMode(.head)
            Text(current.translation ?? "…")
              .font(.system(size: settings.subtitleFontSize, weight: .semibold))
              .foregroundStyle(.white)
              .lineLimit(2)
              .truncationMode(.head)
          } else {
            Text(current.original)
              .font(.system(size: settings.subtitleFontSize, weight: .semibold))
              .foregroundStyle(.white)
              .lineLimit(2)
              .truncationMode(.head)
          }
        }
        .multilineTextAlignment(.center)
        .padding(.horizontal, 18)
        .padding(.vertical, 10)
        .background(.black.opacity(0.72), in: RoundedRectangle(cornerRadius: 12))
        .shadow(radius: 6)
      }
      if settings.subtitlesAtTop { Spacer(minLength: 0) }
    }
    .frame(maxWidth: .infinity, maxHeight: .infinity)
  }
}
