import AppKit
import Foundation
import UserNotifications

/// Asks GitHub which release is the latest and compares it with this app's version. It only tells
/// you: the download and the install stay yours. One GET to api.github.com, with nothing about you
/// or your meetings in it, and only on "Check Now" or while Settings → General has the daily check on.
@MainActor
final class UpdateChecker: ObservableObject {
  static let shared = UpdateChecker()

  static let releasesPage = URL(string: "https://github.com/havurgiray/kurzschrift/releases/latest")!
  private static let repositoryPrefix = "https://github.com/havurgiray/kurzschrift/"

  enum State: Equatable {
    case idle
    case checking
    case upToDate
    case available(version: String, page: URL)
    case failed(String)
  }

  @Published private(set) var state: State = .idle
  @Published private(set) var lastChecked: Date?
  private var timer: Timer?
  private let defaults = UserDefaults.standard

  static var currentVersion: String {
    Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "0"
  }

  var available: (version: String, page: URL)? {
    if case .available(let version, let page) = state { (version, page) } else { nil }
  }

  private init() {
    lastChecked = defaults.object(forKey: "updateLastChecked") as? Date
    // A newer version found earlier stays in the menu until it is installed.
    if let known = defaults.string(forKey: "updateKnownVersion"), Self.isNewer(known, than: Self.currentVersion) {
      state = .available(version: known, page: Self.releasesPage)
    }
  }

  /// Starts or stops the daily check to match the setting.
  func apply(automatic: Bool) {
    timer?.invalidate()
    timer = nil
    guard automatic else { return }
    // The daily check tells you with a notification, so it asks for them itself (call detection,
    // which also asks, may be off) and makes sure a click on one is answered.
    let center = UNUserNotificationCenter.current()
    center.delegate = CallDetector.shared
    center.requestAuthorization(options: [.alert, .sound]) { granted, _ in
      if !granted { Log.write("notifications not allowed; a new version only shows in the menu") }
    }
    checkIfDue()
    timer = Timer.scheduledTimer(withTimeInterval: 3600, repeats: true) { _ in
      Task { @MainActor in UpdateChecker.shared.checkIfDue() }
    }
  }

  private func checkIfDue() {
    // Not during a meeting: nothing should happen there that the meeting doesn't need.
    guard SessionController.shared.isIdle else { return }
    if let lastChecked, Date().timeIntervalSince(lastChecked) < 86_400 { return }
    Task { await check(announce: true) }
  }

  /// `announce` posts a notification when a version is found that wasn't known before (daily check).
  func check(announce: Bool = false) async {
    guard state != .checking else { return }
    let before = available?.version
    state = .checking
    do {
      let latest = try await Self.latestRelease()
      lastChecked = Date()
      defaults.set(lastChecked, forKey: "updateLastChecked")
      if Self.isNewer(latest.version, than: Self.currentVersion) {
        defaults.set(latest.version, forKey: "updateKnownVersion")
        state = .available(version: latest.version, page: latest.page)
        Log.write("update check: \(latest.version) is available (this is \(Self.currentVersion))")
        if announce, before != latest.version { notify(latest.version) }
      } else {
        defaults.removeObject(forKey: "updateKnownVersion")
        state = .upToDate
        Log.write("update check: \(Self.currentVersion) is the latest")
      }
    } catch {
      state = .failed(Self.message(for: error))
      Log.write("update check failed: \(error)")
    }
  }

  /// For the menu's "Check for Updates…": checks, then says the result in a dialog.
  func checkAndTell() async {
    await check()
    let alert = NSAlert()
    switch state {
    case .available(let version, let page):
      alert.messageText = "Kurzschrift \(version) is available"
      alert.informativeText = "You have \(Self.currentVersion). The download page shows what's new."
      alert.addButton(withTitle: "Open Download Page")
      alert.addButton(withTitle: "Later")
      NSApp.activate()
      if alert.runModal() == .alertFirstButtonReturn { NSWorkspace.shared.open(page) }
      return
    case .failed(let message):
      alert.messageText = "Couldn't check for updates"
      alert.informativeText = message
    default:
      alert.messageText = "Kurzschrift is up to date"
      alert.informativeText = "\(Self.currentVersion) is the latest version."
    }
    NSApp.activate()
    alert.runModal()
  }

  private func notify(_ version: String) {
    let content = UNMutableNotificationContent()
    content.title = "Kurzschrift \(version) is available"
    content.body = "Open the Kurzschrift menu to get it."
    UNUserNotificationCenter.current().add(
      UNNotificationRequest(identifier: "update-\(version)", content: content, trigger: nil))
  }

  // MARK: GitHub

  private struct Release: Decodable {
    let tag_name: String
    let html_url: String
  }

  private static func latestRelease() async throws -> (version: String, page: URL) {
    // Hidden testing default: `defaults write local.kurzschrift updateCheckURL http://localhost:…`.
    let override = UserDefaults.standard.string(forKey: "updateCheckURL").flatMap(URL.init(string:))
    let url = override ?? URL(string: "https://api.github.com/repos/havurgiray/kurzschrift/releases/latest")!
    var request = URLRequest(url: url, cachePolicy: .reloadIgnoringLocalCacheData, timeoutInterval: 15)
    request.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept")
    request.setValue("Kurzschrift/\(currentVersion)", forHTTPHeaderField: "User-Agent")
    let (data, response) = try await URLSession(configuration: .ephemeral).data(for: request)
    guard let http = response as? HTTPURLResponse else { throw URLError(.badServerResponse) }
    guard http.statusCode == 200 else { throw UpdateError.http(http.statusCode) }
    let release = try JSONDecoder().decode(Release.self, from: data)
    let version = release.tag_name.hasPrefix("v") ? String(release.tag_name.dropFirst()) : release.tag_name
    guard version.range(of: #"^\d+(\.\d+){0,3}$"#, options: .regularExpression) != nil else { throw UpdateError.unreadable }
    // Only ever open a page of this project, whatever the answer says.
    let page = URL(string: release.html_url).flatMap { $0.absoluteString.hasPrefix(repositoryPrefix) ? $0 : nil }
    return (version, page ?? releasesPage)
  }

  private enum UpdateError: Error {
    case http(Int)
    case unreadable
  }

  private static func message(for error: Error) -> String {
    switch error {
    case UpdateError.http(let status) where status == 403 || status == 429:
      "GitHub is limiting requests from your network right now. Try again in an hour."
    case UpdateError.http(let status): "GitHub answered with an error (HTTP \(status))."
    case UpdateError.unreadable, is DecodingError: "GitHub's answer couldn't be read."
    case let error as URLError where error.code == .notConnectedToInternet: "This Mac isn't connected to the internet."
    default: "GitHub couldn't be reached. \(error.localizedDescription)"
    }
  }

  /// "1.10.0" is newer than "1.9.2": compared number by number.
  nonisolated static func isNewer(_ version: String, than current: String) -> Bool {
    let a = version.split(separator: ".").map { Int($0) ?? 0 }
    let b = current.split(separator: ".").map { Int($0) ?? 0 }
    for i in 0..<max(a.count, b.count) {
      let x = i < a.count ? a[i] : 0
      let y = i < b.count ? b[i] : 0
      if x != y { return x > y }
    }
    return false
  }
}
