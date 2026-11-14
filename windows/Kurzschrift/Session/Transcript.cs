using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Kurzschrift;

/// One speaker's uninterrupted stretch of speech in the final transcript.
public sealed class Turn {
  public string? Speaker { get; set; }
  public double Start { get; set; }
  public double End { get; set; }
  /// Original text in pieces as recognized; translated piece by piece.
  public List<string> Pieces { get; } = new();
  /// The language of each piece (text already in the target language isn't translated).
  public List<string> Languages { get; } = new();
  public string? Translation { get; set; }
  public string Text => string.Join(" ", Pieces);
}

/// A line in the live window and the draft file.
public sealed class LiveEntry : Observable {
  string? speaker;
  string original = "";
  string? translation;
  public Guid Id { get; init; } = Guid.NewGuid();
  public AudioSourceKind Source { get; init; }
  public string? Speaker { get => speaker; set { if (Set(ref speaker, value)) Raise(nameof(Heading)); } }
  public double Start { get; init; }
  public string Original { get => original; set => Set(ref original, value); }
  public string? Translation { get => translation; set => Set(ref translation, value); }
  public bool IsBookmark { get; init; }
  public string Clock => TranscriptDocument.Clock(Start);
  public string Heading => IsBookmark ? $"⭐ Bookmark · {Clock}" : Speaker != null ? $"{Speaker} · {Clock}" : Clock;
}

public static class TranscriptBuilder {
  /// Speech that speaker detection didn't cover (very quiet or very short).
  const string UnknownSpeaker = "?";

  /// Merges both sources, drops microphone echo, assigns speakers and groups speech into turns.
  /// `knownNames`: "source:speakerId" → a remembered person's name. `sameSpeakers`: microphone
  /// speakers who are really someone in the meeting audio. Returns the turns and the name each
  /// diarized speaker ended up with.
  public static (List<Turn> Turns, Dictionary<string, string> SpeakerNames) Build(
      List<FinalSegment> segmentsIn, Dictionary<AudioSourceKind, List<SpeakerSpan>> speakers, SessionConfig config,
      Dictionary<string, string> knownNames, Dictionary<string, string>? sameSpeakers = null) {
    sameSpeakers ??= new();
    var segments = RemovingEcho(segmentsIn.OrderBy(s => s.Start).ToList());
    bool bothSources = segments.Select(s => s.Source).Distinct().Count() > 1 || config.IncludeMic;

    // 1. Label every word, then cut segments where the speaker changes.
    var pieces = new List<(string? Label, double Start, double End, string Text, string Language)>();
    foreach (var segment in segments) {
      if (segment.Source == AudioSourceKind.Microphone && !(config.MicShared && speakers.ContainsKey(AudioSourceKind.Microphone))) {
        pieces.Add((config.MyName, segment.Start, segment.End, segment.Text, segment.Language));
        continue;
      }
      if (!speakers.TryGetValue(segment.Source, out var spans) || spans.Count == 0 || segment.Words.Count == 0) {
        string? label = segment.Source == AudioSourceKind.Microphone ? config.MyName : (bothSources ? "Others" : null);
        pieces.Add((label, segment.Start, segment.End, segment.Text, segment.Language));
        continue;
      }
      // Word start times swallow the silence before a sentence, so a point just before the word's
      // end is the reliable one.
      var labels = Smooth(segment.Words.Select(w => SpeakerAt(Math.Max(w.Start, w.End - 0.1), spans)).ToList());
      string? openLabel = null;
      var openWords = new List<TimedWord>();
      for (int i = 0; i < segment.Words.Count; i++) {
        var own = $"{segment.Source.Key()}:{labels[i] ?? UnknownSpeaker}";
        var label = sameSpeakers.GetValueOrDefault(own) ?? own;
        if (openWords.Count > 0 && openLabel == label) {
          openWords.Add(segment.Words[i]);
        } else {
          if (openWords.Count > 0) pieces.Add(Piece(openLabel, openWords, segment.Language));
          openLabel = label;
          openWords = new() { segment.Words[i] };
        }
      }
      if (openWords.Count > 0) pieces.Add(Piece(openLabel, openWords, segment.Language));
    }
    pieces = pieces.OrderBy(p => p.Start).ToList();

    // 2. Name diarized speakers "Speaker 1, 2, ..." in order of first appearance.
    var names = new Dictionary<string, string>();
    int numbered = 0;
    foreach (var p in pieces) {
      if (p.Label == null || !p.Label.Contains(':') || names.ContainsKey(p.Label)) continue;
      if (knownNames.TryGetValue(p.Label, out var known)) names[p.Label] = known;
      else if (p.Label.EndsWith(":" + UnknownSpeaker)) names[p.Label] = "Unknown speaker";
      else names[p.Label] = $"Speaker {++numbered}";
    }

    // 3. Merge consecutive pieces from the same speaker.
    var turns = new List<Turn>();
    foreach (var p in pieces.Where(p => p.Text.Length > 0)) {
      var name = p.Label == null ? null : names.GetValueOrDefault(p.Label) ?? p.Label;
      var last = turns.LastOrDefault();
      if (last != null && last.Speaker == name && p.Start - last.End < 30) {
        last.Pieces.Add(p.Text);
        last.Languages.Add(p.Language);
        last.End = Math.Max(last.End, p.End);
      } else {
        var t = new Turn { Speaker = name, Start = p.Start, End = p.End };
        t.Pieces.Add(p.Text);
        t.Languages.Add(p.Language);
        turns.Add(t);
      }
    }
    return (turns, names);
  }

  static (string? Label, double Start, double End, string Text, string Language) Piece(
      string? label, List<TimedWord> words, string language) =>
    (label, words.FirstOrDefault()?.Start ?? 0, words.LastOrDefault()?.End ?? 0,
      TextCleanup.Tidy(string.Concat(words.Select(w => w.Text))), language);

  /// Between two speakers' spans: speaker detection trims the tail of a turn, so a word shortly
  /// after a span ends usually still belongs to it, unless the next speaker starts right away.
  static string? SpeakerAt(double time, List<SpeakerSpan> spans) {
    var hit = spans.FirstOrDefault(s => s.Start <= time && time <= s.End);
    if (hit != null) return hit.Speaker;
    var next = spans.Where(s => s.Start > time).MinBy(s => s.Start);
    var previous = spans.Where(s => s.End < time).MaxBy(s => s.End);
    if (next != null && next.Start - time <= 0.3) return next.Speaker;
    if (previous != null && time - previous.End <= 1.0) return previous.Speaker;
    var nearest = spans.MinBy(s => Distance(time, s));
    return nearest != null && Distance(time, nearest) <= 1.5 ? nearest.Speaker : null;
  }

  static double Distance(double time, SpeakerSpan s) => time < s.Start ? s.Start - time : Math.Max(0, time - s.End);

  /// Fills unknown labels from neighbours and removes one- or two-word blips of another speaker in
  /// the middle of someone's sentence, which are almost always boundary noise.
  static List<string?> Smooth(List<string?> labels) {
    var o = labels.ToList();
    string? lastKnown = null;
    for (int i = 0; i < o.Count; i++) {
      if (o[i] == null) o[i] = lastKnown; else lastKnown = o[i];
    }
    var firstKnown = o.FirstOrDefault(x => x != null);
    if (firstKnown != null) for (int i = 0; i < o.Count; i++) o[i] ??= firstKnown;
    int a = 0;
    while (a < o.Count) {
      int j = a;
      while (j + 1 < o.Count && o[j + 1] == o[a]) j++;
      int run = j - a + 1;
      if (run <= 2 && a > 0 && j + 1 < o.Count && o[a - 1] == o[j + 1])
        for (int k = a; k <= j; k++) o[k] = o[a - 1];
      a = j + 1;
    }
    return o;
  }

  /// With loudspeakers instead of headphones the microphone also hears the other participants.
  /// Drops microphone segments that repeat what the meeting audio said at the same time.
  public static List<FinalSegment> RemovingEcho(List<FinalSegment> segments) {
    var system = segments.Where(s => s.Source == AudioSourceKind.System).ToList();
    return segments.Where(s => s.Source != AudioSourceKind.Microphone || !IsEcho(s, system)).ToList();
  }

  /// An echo happens while someone else is talking and says roughly the same thing: at least half of
  /// its real words must be close to a word the others said.
  public static bool IsEcho(FinalSegment mic, List<FinalSegment> system) {
    const double slack = 0.5;
    var nearby = system.Where(s => mic.Start < s.End + slack && s.Start < mic.End + slack).ToList();
    if (nearby.Count == 0) return false;
    double overlap = nearby.Sum(s => Math.Max(0, Math.Min(mic.End, s.End + slack) - Math.Max(mic.Start, s.Start - slack)));
    if (overlap / Math.Max(mic.End - mic.Start, 0.1) < 0.6) return false;
    var systemWords = Words(string.Join(" ", nearby.Select(s => s.Text)));
    var micWords = Words(mic.Text);
    var content = micWords.Where(w => w.Length >= 3).ToList();
    if (content.Count == 0)
      return micWords.Count > 0 && string.Join(" ", systemWords).Contains(string.Join(" ", micWords));
    int matched = content.Count(w => systemWords.Any(s => IsClose(w, s)));
    return matched / (double)content.Count >= 0.5;
  }

  static List<string> Words(string text) =>
    Regex.Split(text.ToLowerInvariant(), @"[^\p{L}\p{N}]+").Where(w => w.Length > 0).ToList();

  static bool IsClose(string a, string b) {
    if (a == b) return true;
    if (Math.Abs(a.Length - b.Length) > 2) return false;
    return EditDistance(a, b) <= Math.Max(1, a.Length / 4);
  }

  static int EditDistance(string a, string b) {
    var prev = Enumerable.Range(0, b.Length + 1).ToArray();
    for (int i = 1; i <= a.Length; i++) {
      var cur = new int[b.Length + 1];
      cur[0] = i;
      for (int j = 1; j <= b.Length; j++)
        cur[j] = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
      prev = cur;
    }
    return prev[b.Length];
  }
}

/// The transcript file: rewritten as a live draft while recording, replaced by the final
/// transcript (speakers, translation) when the meeting stops. Same format as the Mac version.
public sealed class TranscriptDocument {
  public string FilePath { get; }
  readonly SessionConfig config;
  readonly DateTime startedAt;
  /// Who's in the meeting and what it's about, from the Context tab.
  public MeetingContext Context { get; set; } = new();
  /// AI minutes, or only your own notes; becomes true when AI minutes are switched on mid-meeting.
  public bool UsesAIMinutes { get; set; }
  string MinutesHeading => UsesAIMinutes ? "Minutes" : "Notes";

  public TranscriptDocument(SessionConfig config, DateTime startedAt) {
    this.config = config;
    this.startedAt = startedAt;
    UsesAIMinutes = config.Minutes != null;
    Directory.CreateDirectory(config.SaveFolder);
    var name = $"{startedAt:yyyy-MM-dd HH-mm} {KindTitle(config)}";
    var candidate = Path.Combine(config.SaveFolder, name + ".md");
    for (int n = 2; File.Exists(candidate); n++) candidate = Path.Combine(config.SaveFolder, $"{name} {n}.md");
    FilePath = candidate;
  }

  public string BaseName => Path.GetFileNameWithoutExtension(FilePath);

  public void WriteDraft(IEnumerable<LiveEntry> entries, string? minutes = null) => Write(DraftMarkdown(entries, minutes));

  public string DraftMarkdown(IEnumerable<LiveEntry> entries, string? minutes) {
    var md = new StringBuilder(Header(null, new()));
    md.Append("_Recording in progress. Speakers are identified when the recording stops._\n\n");
    md.Append(Context.Markdown);
    if (!string.IsNullOrEmpty(minutes)) md.Append($"## {MinutesHeading} (live draft)\n\n{minutes}\n\n");
    if (!Context.IsEmpty || !string.IsNullOrEmpty(minutes)) md.Append("## Transcript\n\n");
    foreach (var e in entries) {
      if (e.IsBookmark) {
        md.Append(BookmarkMarkdown(e.Start));
        continue;
      }
      md.Append(Heading(e.Speaker, e.Start));
      md.Append(config.Mode == MeetingMode.Translate ? $"> {e.Original}\n\n{e.Translation ?? ""}\n\n" : $"{e.Original}\n\n");
    }
    return md.ToString();
  }

  public void WriteFinal(List<Turn> turns, DateTime endedAt, List<string> notes, List<string> audioFiles,
      string? minutes = null, List<double>? bookmarks = null, List<(string Question, string Answer)>? qa = null) {
    var pending = new Queue<double>((bookmarks ?? new()).OrderBy(b => b));
    string Marks(double before) {
      var s = new StringBuilder();
      while (pending.Count > 0 && pending.Peek() <= before) s.Append(BookmarkMarkdown(pending.Dequeue()));
      return s.ToString();
    }
    qa ??= new();
    var speakers = turns.Select(t => t.Speaker).OfType<string>().Where(s => s != "Unknown speaker").Distinct().ToList();
    var md = new StringBuilder(Header(endedAt, speakers));
    foreach (var note in notes) md.Append($"> [!note]\n> {note}\n\n");
    if (audioFiles.Count > 0)
      md.Append("Audio: " + string.Join(", ", audioFiles.Select(f => $"[{Path.GetFileName(f)}](<{Path.GetFileName(f)}>)")) + "\n\n");
    md.Append(Context.Markdown);
    if (!string.IsNullOrEmpty(minutes)) md.Append($"## {MinutesHeading}\n\n{minutes}\n\n");
    if (qa.Count > 0) {
      md.Append("## Questions\n\n");
      foreach (var (q, a) in qa) md.Append($"**Q: {q}**\n\n{a}\n\n");
    }
    if ((!string.IsNullOrEmpty(minutes) || qa.Count > 0 || !Context.IsEmpty) &&
        !(config.Mode == MeetingMode.Translate && config.Layout == TranscriptLayout.Sections))
      md.Append("## Transcript\n\n");
    if (turns.Count == 0) md.Append("_No speech was recognized._\n");

    if (config.Mode == MeetingMode.Transcribe) {
      foreach (var t in turns) md.Append(Marks(t.Start) + Heading(t.Speaker, t.Start) + $"{t.Text}\n\n");
    } else if (config.Layout == TranscriptLayout.Interleaved) {
      foreach (var t in turns)
        md.Append(Marks(t.Start) + Heading(t.Speaker, t.Start) + $"> {t.Text}\n\n{t.Translation ?? "_(not translated)_"}\n\n");
    } else {
      md.Append($"## {Languages.Name(config.Spoken)} (original)\n\n");
      foreach (var t in turns) md.Append(Marks(t.Start) + Heading(t.Speaker, t.Start) + $"{t.Text}\n\n");
      md.Append($"## {Languages.Name(config.Target)} (translation)\n\n");
      foreach (var t in turns) md.Append(Heading(t.Speaker, t.Start) + $"{t.Translation ?? "_(not translated)_"}\n\n");
    }
    md.Append(Marks(double.PositiveInfinity));
    Write(md.ToString());
  }

  string Header(DateTime? endedAt, List<string> speakers) {
    var f = new StringBuilder("---\n");
    f.Append($"date: {Iso(startedAt)}\n");
    if (endedAt is DateTime end) {
      f.Append($"end: {Iso(end)}\n");
      f.Append($"duration: {(int)Math.Round((end - startedAt).TotalMinutes)} min\n");
    }
    f.Append("type: meeting-transcript\n");
    f.Append($"language: {config.Spoken}\n");
    if (config.Mode == MeetingMode.Translate && config.Target != null) f.Append($"translation: {config.Target}\n");
    if (speakers.Count > 0) f.Append("speakers: [" + string.Join(", ", speakers.Select(s => $"\"{s}\"")) + "]\n");
    var participants = Context.Names;
    if (participants.Count > 0)
      f.Append("participants: [" + string.Join(", ", participants.Select(p => $"\"{p.Replace("\"", "'")}\"")) + "]\n");
    f.Append("---\n\n");
    f.Append($"# {KindTitle(config)}, {startedAt.ToString("d MMM yyyy, HH:mm", CultureInfo.GetCultureInfo("en-GB"))}\n\n");
    return f.ToString();
  }

  static string Iso(DateTime t) => new DateTimeOffset(t).ToString("yyyy-MM-ddTHH:mm:sszzz");

  static string Heading(string? speaker, double seconds) {
    var time = Clock(seconds);
    return speaker != null ? $"**{speaker}** · {time}\n\n" : $"**{time}**\n\n";
  }

  void Write(string text) {
    try {
      var tmp = FilePath + ".tmp";
      File.WriteAllText(tmp, text, new UTF8Encoding(false));
      File.Move(tmp, FilePath, true);
    } catch (Exception e) {
      Log.Write($"writing transcript failed: {e.Message}");
    }
  }

  public static string BookmarkMarkdown(double seconds) => $"**⭐ Bookmark** · {Clock(seconds)}\n\n";

  public static string Clock(double seconds) {
    int s = Math.Max(0, (int)seconds);
    return $"{s / 3600:00}:{s / 60 % 60:00}:{s % 60:00}";
  }

  static string KindTitle(SessionConfig config) {
    var spoken = Languages.Name(config.Spoken);
    return config.Mode == MeetingMode.Translate ? $"{spoken} meeting (translated)" : $"{spoken} meeting";
  }
}
