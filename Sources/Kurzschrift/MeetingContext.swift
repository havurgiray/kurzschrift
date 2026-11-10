import Foundation

/// What you tell Kurzschrift about a meeting in the Context tab: who's in it and what it's about.
/// The minutes and the answers to your questions get all of it, speech recognition gets the names,
/// and the transcript file keeps it.
struct MeetingContext: Equatable {
  /// One person per line, optionally with a role ("Maria Huber – project lead"), or a line of
  /// names separated by commas.
  var participants = ""
  /// Topic, agenda, background, abbreviations: anything that helps make sense of the meeting.
  var about = ""

  init(participants: String = "", about: String = "") {
    self.participants = participants
    self.about = about
  }

  /// Prefilled from the calendar invite, one attendee per line.
  init(attendees: [String]) {
    self.init(participants: attendees.joined(separator: "\n"))
  }

  var isEmpty: Bool { people.isEmpty && aboutText.isEmpty }

  var aboutText: String { about.trimmingCharacters(in: .whitespacesAndNewlines) }

  /// What separates a name from a role in "Maria Huber – project lead".
  private static let roleSeparators = [" – ", " — ", " - ", ": ", "("]

  /// The participants as typed, one entry per person.
  var people: [String] {
    participants.split(whereSeparator: \.isNewline).flatMap { raw -> [String] in
      var line = raw.trimmingCharacters(in: .whitespaces)
      for marker in ["- ", "* ", "• "] where line.hasPrefix(marker) {
        line = String(line.dropFirst(marker.count)).trimmingCharacters(in: .whitespaces)
      }
      if line.isEmpty { return [] }
      // A role can contain commas; a line without one is a list of names.
      if Self.roleSeparators.contains(where: { line.contains($0) }) || !line.contains(",") { return [line] }
      return line.split(separator: ",").map { $0.trimmingCharacters(in: .whitespaces) }.filter { !$0.isEmpty }
    }
  }

  /// Just the names, for speech recognition and for renaming speakers.
  var names: [String] {
    people.compactMap { person -> String? in
      var name = person
      for separator in Self.roleSeparators {
        if let range = name.range(of: separator) { name = String(name[..<range.lowerBound]) }
      }
      name = name.trimmingCharacters(in: .whitespaces)
      return name.isEmpty ? nil : name
    }
    .reduce(into: [String]()) { if !$0.contains($1) { $0.append($1) } }
  }

  /// Added to the instructions for the minutes and for questions.
  var promptText: String {
    var parts: [String] = []
    if !people.isEmpty {
      parts.append(
        "The people in this meeting, as the user listed them. Spell their names this way, and tie a "
          + "speaker label to one of them only when the transcript makes clear who is talking:\n"
          + people.map { "- \($0)" }.joined(separator: "\n"))
    }
    if !aboutText.isEmpty {
      parts.append("About this meeting, from the user:\n" + aboutText)
    }
    return parts.joined(separator: "\n\n")
  }

  /// The "Context" section of the transcript file; empty when there's nothing to say.
  var markdown: String {
    guard !isEmpty else { return "" }
    var md = "## Context\n\n"
    if !people.isEmpty {
      md += "**Participants**\n\n" + people.map { "- \($0)\n" }.joined() + "\n"
    }
    if !aboutText.isEmpty {
      md += "**About the meeting**\n\n\(aboutText)\n\n"
    }
    return md
  }
}
