using System.Globalization;

namespace Kurzschrift;

/// What you tell Kurzschrift about a meeting in the Context tab: who's in it and what it's about.
/// The minutes and the answers to your questions get all of it, speech recognition gets the names,
/// and the transcript file keeps it. Immutable (change it with `with`), so the copy the minutes were
/// written with can be compared with the current one.
public sealed record MeetingContext {
  /// One person per line, optionally with a role ("Maria Huber – project lead"), or a line of
  /// names separated by commas.
  public string Participants { get; init; } = "";
  /// Topic, agenda, background, abbreviations: anything that helps make sense of the meeting.
  public string About { get; init; } = "";

  public MeetingContext(string participants = "", string about = "") {
    Participants = participants;
    About = about;
  }

  /// Prefilled from the calendar invite, one attendee per line.
  public MeetingContext(IEnumerable<string> attendees) : this(string.Join("\n", attendees)) { }

  public bool IsEmpty => People.Count == 0 && AboutText.Length == 0;

  public string AboutText => About.Trim();

  /// What separates a name from a role in "Maria Huber – project lead".
  static readonly string[] RoleSeparators = [" – ", " — ", " - ", ": ", "("];

  /// Every kind of line break (Swift's Character.isNewline).
  static readonly char[] LineBreaks = ['\n', '\r', '\v', '\f', '\u0085', '\u2028', '\u2029'];

  /// The participants as typed, one entry per person.
  public IReadOnlyList<string> People {
    get {
      var people = new List<string>();
      foreach (var raw in Participants.Split(LineBreaks, StringSplitOptions.RemoveEmptyEntries)) {
        var line = TrimSpaces(raw);
        foreach (var marker in (string[])["- ", "* ", "• "]) {
          if (line.StartsWith(marker, StringComparison.Ordinal)) line = TrimSpaces(line[marker.Length..]);
        }
        if (line.Length == 0) continue;
        // A role can contain commas; a line without one is a list of names.
        if (RoleSeparators.Any(separator => line.Contains(separator, StringComparison.Ordinal)) || !line.Contains(',')) {
          people.Add(line);
        } else {
          people.AddRange(line.Split(',').Select(TrimSpaces).Where(name => name.Length > 0));
        }
      }
      return people;
    }
  }

  /// Just the names, for speech recognition and for renaming speakers.
  public IReadOnlyList<string> Names {
    get {
      var names = new List<string>();
      foreach (var person in People) {
        var name = person;
        foreach (var separator in RoleSeparators) {
          var index = name.IndexOf(separator, StringComparison.Ordinal);
          if (index >= 0) name = name[..index];
        }
        name = TrimSpaces(name);
        if (name.Length > 0 && !names.Contains(name)) names.Add(name);
      }
      return names;
    }
  }

  /// Added to the instructions for the minutes and for questions.
  public string PromptText {
    get {
      var parts = new List<string>();
      var people = People;
      if (people.Count > 0) {
        parts.Add(
          "The people in this meeting, as the user listed them. Spell their names this way, and tie a "
          + "speaker label to one of them only when the transcript makes clear who is talking:\n"
          + string.Join("\n", people.Select(person => $"- {person}")));
      }
      if (AboutText.Length > 0) parts.Add("About this meeting, from the user:\n" + AboutText);
      return string.Join("\n\n", parts);
    }
  }

  /// The "Context" section of the transcript file; empty when there's nothing to say.
  public string Markdown {
    get {
      if (IsEmpty) return "";
      var md = "## Context\n\n";
      var people = People;
      if (people.Count > 0) md += "**Participants**\n\n" + string.Concat(people.Select(person => $"- {person}\n")) + "\n";
      if (AboutText.Length > 0) md += $"**About the meeting**\n\n{AboutText}\n\n";
      return md;
    }
  }

  /// Trims spaces and tabs but not line breaks (Swift's CharacterSet.whitespaces).
  static string TrimSpaces(string text) {
    static bool IsSpace(char c) => c == '\t' || char.GetUnicodeCategory(c) == UnicodeCategory.SpaceSeparator;
    int start = 0, end = text.Length;
    while (start < end && IsSpace(text[start])) start++;
    while (end > start && IsSpace(text[end - 1])) end--;
    return text[start..end];
  }
}
