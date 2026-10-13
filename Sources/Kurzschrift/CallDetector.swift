import AppKit
import CoreAudio
import UserNotifications

/// Notices when a meeting app starts using the microphone (a call began) and offers to start
/// recording; stops the recording when that app lets go of the microphone (the call ended).
/// Uses Core Audio's per-process list, which needs no permission and sees no audio.
@MainActor
final class CallDetector: NSObject, UNUserNotificationCenterDelegate {
  static let shared = CallDetector()

  private static let meetingApps: [(prefix: String, name: String)] = [
    ("com.microsoft.teams", "Teams"), ("us.zoom", "Zoom"), ("com.cisco.webex", "Webex"), ("com.webex", "Webex"),
    ("com.apple.FaceTime", "FaceTime"), ("com.tinyspeck.slackmacgap", "Slack"), ("com.hnc.Discord", "Discord"),
  ]
  private static let browsers: [(prefix: String, name: String)] = [
    ("com.google.Chrome", "Chrome"), ("com.apple.Safari", "Safari"), ("company.thebrowser.Browser", "Arc"),
    ("org.mozilla.firefox", "Firefox"), ("com.microsoft.edgemac", "Edge"), ("com.brave.Browser", "Brave"),
  ]

  private var timer: Timer?
  /// The meeting app currently in a call, and since when.
  private var callApp: String?
  private var callSince: Date?
  private var missingSince: Date?
  private var offered = false
  /// Whether the running recording overlapped the current call (so the call's end should stop it).
  private var recordingDuringCall = false

  func apply() {
    let settings = AppSettings.shared
    timer?.invalidate()
    timer = nil
    guard settings.detectCalls else { return }
    let center = UNUserNotificationCenter.current()
    center.delegate = self
    center.setNotificationCategories([
      UNNotificationCategory(
        identifier: "call", actions: [
          UNNotificationAction(identifier: "translate", title: "Translate", options: []),
          UNNotificationAction(identifier: "transcribe", title: "Transcribe", options: []),
        ], intentIdentifiers: [])
    ])
    center.requestAuthorization(options: [.alert, .sound]) { granted, _ in
      if !granted { Log.write("notifications not allowed; call detection can still stop recordings") }
    }
    timer = Timer.scheduledTimer(withTimeInterval: 2, repeats: true) { _ in
      MainActor.assumeIsolated { CallDetector.shared.poll() }
    }
  }

  private func poll() {
    let settings = AppSettings.shared
    let session = SessionController.shared
    let apps = Self.meetingApps + (settings.detectBrowserCalls ? Self.browsers : [])
    let inCall = Self.bundleIDsUsingMicrophone().lazy.compactMap { id in
      apps.first { id.hasPrefix($0.prefix) }?.name
    }.first

    if let inCall {
      missingSince = nil
      if callApp != inCall {
        callApp = inCall
        callSince = Date()
        offered = false
        Log.write("call detected: \(inCall)")
      }
      if session.isRecording { recordingDuringCall = true }
      // Offer once per call, after the microphone has been in use for a few seconds.
      if !offered, session.isIdle, let since = callSince, Date().timeIntervalSince(since) >= 3 {
        offered = true
        notify(app: inCall)
      }
    } else if let app = callApp {
      if missingSince == nil { missingSince = Date() }
      guard let missing = missingSince, Date().timeIntervalSince(missing) >= 20 else { return }
      Log.write("call ended: \(app)")
      if recordingDuringCall, settings.stopWhenCallEnds, session.isRecording {
        Task { await session.stop() }
      }
      callApp = nil
      callSince = nil
      missingSince = nil
      recordingDuringCall = false
    }
  }

  private func notify(app: String) {
    let content = UNMutableNotificationContent()
    content.title = "\(app) call started"
    content.body = "Translate or transcribe it with Kurzschrift?"
    content.categoryIdentifier = "call"
    UNUserNotificationCenter.current().add(
      UNNotificationRequest(identifier: "call-\(UUID().uuidString)", content: content, trigger: nil))
  }

  // MARK: UNUserNotificationCenterDelegate

  nonisolated func userNotificationCenter(
    _ center: UNUserNotificationCenter, willPresent notification: UNNotification
  ) async -> UNNotificationPresentationOptions {
    [.banner, .sound]
  }

  nonisolated func userNotificationCenter(_ center: UNUserNotificationCenter, didReceive response: UNNotificationResponse) async {
    let action = response.actionIdentifier
    await MainActor.run {
      let session = SessionController.shared
      switch action {
      case "translate": Task { await session.start(.translate) }
      case "transcribe": Task { await session.start(.transcribe) }
      default: LiveWindowController.shared.show()
      }
    }
  }

  // MARK: Core Audio process list

  private static func bundleIDsUsingMicrophone() -> [String] {
    var address = AudioObjectPropertyAddress(
      mSelector: kAudioHardwarePropertyProcessObjectList, mScope: kAudioObjectPropertyScopeGlobal,
      mElement: kAudioObjectPropertyElementMain)
    let system = AudioObjectID(kAudioObjectSystemObject)
    var size: UInt32 = 0
    guard AudioObjectGetPropertyDataSize(system, &address, 0, nil, &size) == noErr, size > 0 else { return [] }
    var processes = [AudioObjectID](repeating: 0, count: Int(size) / MemoryLayout<AudioObjectID>.size)
    guard AudioObjectGetPropertyData(system, &address, 0, nil, &size, &processes) == noErr else { return [] }

    return processes.compactMap { process in
      var running: UInt32 = 0
      var runningSize = UInt32(MemoryLayout<UInt32>.size)
      var runningAddress = AudioObjectPropertyAddress(
        mSelector: kAudioProcessPropertyIsRunningInput, mScope: kAudioObjectPropertyScopeGlobal,
        mElement: kAudioObjectPropertyElementMain)
      guard AudioObjectGetPropertyData(process, &runningAddress, 0, nil, &runningSize, &running) == noErr, running != 0
      else { return nil }
      var bundleAddress = AudioObjectPropertyAddress(
        mSelector: kAudioProcessPropertyBundleID, mScope: kAudioObjectPropertyScopeGlobal,
        mElement: kAudioObjectPropertyElementMain)
      var bundle: Unmanaged<CFString>?
      var bundleSize = UInt32(MemoryLayout<Unmanaged<CFString>?>.size)
      guard AudioObjectGetPropertyData(process, &bundleAddress, 0, nil, &bundleSize, &bundle) == noErr, let bundle
      else { return nil }
      return bundle.takeRetainedValue() as String
    }
  }
}
