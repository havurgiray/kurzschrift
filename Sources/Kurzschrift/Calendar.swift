import EventKit
import Foundation

/// On-device calendar and reminders access (EventKit). Nothing leaves the Mac.
@MainActor
enum CalendarContext {
  static let store = EKEventStore()

  struct Meeting {
    let title: String
    let attendees: [String]
  }

  /// The calendar event happening now (or starting within 15 minutes), if there is one.
  static func currentMeeting() async -> Meeting? {
    guard (try? await store.requestFullAccessToEvents()) == true else {
      Log.write("calendar access not granted")
      return nil
    }
    let now = Date()
    let predicate = store.predicateForEvents(
      withStart: now.addingTimeInterval(-12 * 3600), end: now.addingTimeInterval(15 * 60), calendars: nil)
    let candidates = store.events(matching: predicate).filter { event in
      !event.isAllDay && event.endDate > now && event.startDate <= now.addingTimeInterval(15 * 60)
        && event.attendees?.first(where: \.isCurrentUser)?.participantStatus != .declined
    }
    // Prefer real meetings (with attendees), then the one whose start is closest to now.
    guard
      let event = candidates.min(by: { a, b in
        let aScore = abs(a.startDate.timeIntervalSince(now)) + ((a.attendees?.isEmpty ?? true) ? 86_400 : 0)
        let bScore = abs(b.startDate.timeIntervalSince(now)) + ((b.attendees?.isEmpty ?? true) ? 86_400 : 0)
        return aScore < bScore
      })
    else { return nil }
    var names: [String] = []
    for participant in (event.organizer.map { [$0] } ?? []) + (event.attendees ?? []) where !participant.isCurrentUser {
      let name = participant.name ?? participant.url.absoluteString.replacingOccurrences(of: "mailto:", with: "")
      if !name.isEmpty, !names.contains(name) { names.append(name) }
    }
    Log.write("calendar event matched, \(names.count) attendees")
    return Meeting(title: event.title ?? "Meeting", attendees: names)
  }

  // MARK: Reminders

  struct ActionItem: Identifiable, Hashable {
    let id = UUID()
    let line: String
    let owner: String?
    let task: String
  }

  /// "- [ ] Owner: task (due Friday)" lines from the minutes.
  static func actionItems(in minutes: String) -> [ActionItem] {
    minutes.components(separatedBy: "\n").compactMap { raw in
      let line = raw.trimmingCharacters(in: .whitespaces)
      guard line.hasPrefix("- [ ] ") || line.hasPrefix("* [ ] ") else { return nil }
      let body = String(line.dropFirst(6)).replacingOccurrences(of: "**", with: "")
      if let colon = body.firstIndex(of: ":"), body.distance(from: body.startIndex, to: colon) <= 40 {
        let owner = body[..<colon].trimmingCharacters(in: .whitespaces)
        let task = body[body.index(after: colon)...].trimmingCharacters(in: .whitespaces)
        return ActionItem(line: body, owner: owner == "?" ? nil : owner, task: task)
      }
      return ActionItem(line: body, owner: nil, task: body)
    }
  }

  /// Adds the items to the default Reminders list. Returns the list's name.
  static func addReminders(_ items: [ActionItem], meetingTitle: String, myName: String) async throws -> String {
    guard try await store.requestFullAccessToReminders() else { throw CalendarError.noRemindersAccess }
    guard let list = store.defaultCalendarForNewReminders() else { throw CalendarError.noRemindersList }
    let detector = try? NSDataDetector(types: NSTextCheckingResult.CheckingType.date.rawValue)
    for item in items {
      let reminder = EKReminder(eventStore: store)
      let mine = item.owner == nil || item.owner == myName || item.owner == "Me"
      reminder.title = mine ? item.task : "\(item.owner!): \(item.task)"
      reminder.notes = "From \(meetingTitle)"
      reminder.calendar = list
      let range = NSRange(item.task.startIndex..., in: item.task)
      if let date = detector?.firstMatch(in: item.task, range: range)?.date {
        reminder.dueDateComponents = Calendar.current.dateComponents([.year, .month, .day], from: date)
      }
      try store.save(reminder, commit: false)
    }
    try store.commit()
    Log.write("added \(items.count) reminders")
    return list.title
  }
}

enum CalendarError: LocalizedError {
  case noRemindersAccess
  case noRemindersList

  var errorDescription: String? {
    switch self {
    case .noRemindersAccess: "Kurzschrift isn't allowed to use Reminders (System Settings → Privacy & Security → Reminders)."
    case .noRemindersList: "There's no default Reminders list."
    }
  }
}
