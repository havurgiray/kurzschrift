import AppKit
import Carbon.HIToolbox

/// System-wide shortcuts via Carbon's RegisterEventHotKey, which needs no Accessibility permission.
@MainActor
final class HotKeys {
  static let shared = HotKeys()

  struct Shortcut {
    let id: UInt32
    let keyCode: Int
    let display: String
    let action: @MainActor () -> Void
  }

  static let modifiers = UInt32(controlKey | optionKey)

  let shortcuts: [Shortcut] = [
    Shortcut(id: 1, keyCode: kVK_ANSI_T, display: "⌃⌥T", action: { SessionController.shared.toggle(.translate) }),
    Shortcut(id: 2, keyCode: kVK_ANSI_R, display: "⌃⌥R", action: { SessionController.shared.toggle(.transcribe) }),
    Shortcut(id: 3, keyCode: kVK_ANSI_B, display: "⌃⌥B", action: { SessionController.shared.addBookmark() }),
  ]

  private var refs: [EventHotKeyRef] = []
  private var handlerInstalled = false

  func apply(enabled: Bool) {
    for ref in refs { UnregisterEventHotKey(ref) }
    refs = []
    guard enabled else { return }
    installHandlerOnce()
    for shortcut in shortcuts {
      var ref: EventHotKeyRef?
      let id = EventHotKeyID(signature: OSType(0x4B5A_5343), id: shortcut.id)  // 'KZSC'
      let status = RegisterEventHotKey(
        UInt32(shortcut.keyCode), Self.modifiers, id, GetApplicationEventTarget(), 0, &ref)
      if status == noErr, let ref {
        refs.append(ref)
      } else {
        Log.write("shortcut \(shortcut.display) unavailable (status \(status)); another app may use it")
      }
    }
  }

  fileprivate func fire(_ id: UInt32) {
    shortcuts.first { $0.id == id }?.action()
  }

  private func installHandlerOnce() {
    guard !handlerInstalled else { return }
    handlerInstalled = true
    var spec = EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed))
    InstallEventHandler(GetApplicationEventTarget(), { _, event, _ in
      var id = EventHotKeyID()
      GetEventParameter(
        event, EventParamName(kEventParamDirectObject), EventParamType(typeEventHotKeyID), nil,
        MemoryLayout<EventHotKeyID>.size, nil, &id)
      let hotKeyID = id.id
      Task { @MainActor in HotKeys.shared.fire(hotKeyID) }
      return noErr
    }, 1, &spec, nil, nil)
  }
}
