using System.Text;
using System.Text.Json;

namespace Kurzschrift;

/// What's left of a meeting that never finished saving (crash, power loss, forced quit): its draft
/// transcript in the transcripts folder and its WAVs in the recording folder. At the next launch the
/// WAVs are put next to the draft, so both are where you look for them. Nothing is recognized again.
public static class Recovery {
  const string SidecarName = "recording.json";

  /// Written into a meeting's recording folder when it starts: where its draft transcript is.
  public sealed record Sidecar(string Draft, string Mode, string Spoken, DateTime Started);

  public sealed record Summary(int Meetings, string Folder);

  public static void WriteSidecar(string folder, string draft, SessionConfig config, DateTime started) {
    try {
      var side = new Sidecar(draft, config.Mode.ToString().ToLowerInvariant(), config.Spoken, started);
      File.WriteAllText(Path.Combine(folder, SidecarName), JsonSerializer.Serialize(side));
    } catch (Exception e) {
      Log.Write($"writing the recording's sidecar failed: {e.Message}");
    }
  }

  /// The recording folders present right now. Call at launch, before any meeting can start.
  public static List<string> Leftovers() {
    try {
      return Directory.Exists(Paths.Recordings) ? Directory.GetDirectories(Paths.Recordings).ToList() : new();
    } catch (Exception e) {
      Log.Write($"looking for unfinished recordings failed: {e.Message}");
      return new();
    }
  }

  /// Puts each folder's recordings next to its draft and removes the folder. Returns what to tell
  /// the user, or null when there was nothing worth keeping.
  public static Summary? Recover(List<string> folders) {
    int meetings = 0;
    string where = "";
    foreach (var folder in folders) {
      try {
        if (RecoverOne(folder) is { } target) {
          meetings++;
          where = target;
        }
      } catch (Exception e) {
        Log.Write($"recovering an unfinished recording failed: {e.Message}");
      }
    }
    return meetings > 0 ? new Summary(meetings, where) : null;
  }

  /// Returns the folder the meeting's files are in now, or null when the leftover was empty.
  static string? RecoverOne(string folder) {
    Sidecar? side = null;
    try {
      var file = Path.Combine(folder, SidecarName);
      if (File.Exists(file)) side = JsonSerializer.Deserialize<Sidecar>(File.ReadAllText(file));
    } catch {
      side = null;
    }
    // The draft is where the meeting wrote it, or in the Unsaved folder if that place couldn't be written.
    string? draft = null;
    if (side is { Draft.Length: > 0 }) {
      var fallback = Path.Combine(Paths.Unsaved, Path.GetFileName(side.Draft));
      draft = File.Exists(side.Draft) ? side.Draft : File.Exists(fallback) ? fallback : null;
    }
    var name = draft != null ? Path.GetFileNameWithoutExtension(draft)
      : side is { Draft.Length: > 0 } ? Path.GetFileNameWithoutExtension(side.Draft)
      : FormattableString.Invariant($"{Directory.GetCreationTime(folder):yyyy-MM-dd HH-mm} meeting");
    var target = draft != null ? Path.GetDirectoryName(draft)! : Paths.Unsaved;

    var moved = new List<string>();
    bool failed = false;
    foreach (var (file, label) in new[] { ("system.wav", "meeting audio"), ("microphone.wav", "microphone") }) {
      var wav = Path.Combine(folder, file);
      if (!File.Exists(wav)) continue;
      long audioBytes;
      try {
        audioBytes = RepairWav(wav);
      } catch (Exception e) {
        Log.Write($"checking a leftover recording failed: {e.Message}");
        failed = true;
        continue;
      }
      if (audioBytes < Track.Rate * 2) continue;  // under a second of audio: nothing to keep
      var saved = MoveInto(wav, target, $"{name} ({label}, recovered)") ??
                  (target != Paths.Unsaved ? MoveInto(wav, Paths.Unsaved, $"{name} ({label}, recovered)") : null);
      if (saved != null) moved.Add(saved); else failed = true;
    }
    if (draft != null) MarkDraft(draft, moved);
    if (!failed) {
      try {
        Directory.Delete(folder, true);
      } catch (Exception e) {
        Log.Write($"removing a leftover recording folder failed: {e.Message}");
      }
    }
    if (draft == null && moved.Count == 0) {
      Log.Write("removed an empty leftover recording folder");
      return null;
    }
    var files = moved.Select(Path.GetFileName).ToList();
    if (draft != null) files.Insert(0, Path.GetFileName(draft));
    Log.Write($"recovered an unfinished meeting: {string.Join(", ", files)}{(failed ? " (some audio couldn't be moved)" : "")}");
    return moved.Count > 0 ? Path.GetDirectoryName(moved[0])! : target;
  }

  static string? MoveInto(string wav, string folder, string name) {
    try {
      Directory.CreateDirectory(folder);
      var target = TranscriptDocument.UniquePath(folder, name, ".wav");
      File.Move(wav, target);
      return target;
    } catch (Exception e) {
      Log.Write($"moving a leftover recording failed: {e.Message}");
      return null;
    }
  }

  /// A WAV that was cut off has a header from up to a few seconds before the end (or, from older
  /// versions, an empty one). Sets its sizes to what the file really holds; returns the audio bytes.
  static long RepairWav(string path) {
    using var f = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    var head = new byte[12];
    if (f.Length < 44 || f.Read(head, 0, 12) != 12) return 0;
    if (Encoding.ASCII.GetString(head, 0, 4) != "RIFF" || Encoding.ASCII.GetString(head, 8, 4) != "WAVE") return 0;
    long pos = 12;
    var chunk = new byte[8];
    while (pos + 8 <= f.Length) {
      f.Position = pos;
      if (f.Read(chunk, 0, 8) != 8) return 0;
      uint size = BitConverter.ToUInt32(chunk, 4);
      if (Encoding.ASCII.GetString(chunk, 0, 4) == "data") {
        long start = pos + 8;
        long audio = f.Length - start;
        audio -= audio % 2;  // whole 16-bit samples
        if (size != audio || f.Length != start + audio) {
          f.SetLength(start + audio);
          f.Position = pos + 4;
          f.Write(BitConverter.GetBytes((uint)audio));
          f.Position = 4;
          f.Write(BitConverter.GetBytes((uint)(start + audio - 8)));
        }
        return audio;
      }
      pos += 8 + size + (size & 1);
    }
    return 0;
  }

  /// The draft still says "Recording in progress"; say what happened and link the recordings instead.
  static void MarkDraft(string draft, List<string> recordings) {
    try {
      var text = File.ReadAllText(draft);
      if (!text.Contains(TranscriptDocument.DraftBanner, StringComparison.Ordinal)) return;
      var note = "_This meeting didn't finish saving: Kurzschrift was closed or stopped working while it ran. " +
                 "This is the transcript up to then, without speaker names._";
      var sameFolder = recordings.Where(r => Path.GetDirectoryName(r) == Path.GetDirectoryName(draft)).ToList();
      if (sameFolder.Count > 0)
        note += "\n\nAudio: " + string.Join(", ", sameFolder.Select(r => $"[{Path.GetFileName(r)}](<{Path.GetFileName(r)}>)"));
      var tmp = draft + ".tmp";
      File.WriteAllText(tmp, text.Replace(TranscriptDocument.DraftBanner, note, StringComparison.Ordinal), new UTF8Encoding(false));
      File.Move(tmp, draft, true);
    } catch (Exception e) {
      Log.Write($"updating the unfinished transcript failed: {e.Message}");
    }
  }
}
