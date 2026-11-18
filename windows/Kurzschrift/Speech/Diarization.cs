using System.Diagnostics;
using System.Text.Json;
using NAudio.Wave;
using SherpaOnnx;

namespace Kurzschrift;

/// Who spoke when, in seconds on the recording's timeline.
public sealed record SpeakerSpan(string Speaker, double Start, double End);

/// Speaker separation (sherpa-onnx: pyannote segmentation 3.0 + WeSpeaker embeddings). Runs in a
/// child process (this same program with `--diarize`), like the Mac version, so a crash in the native
/// code can never take the app, and the transcript it is about to save, down with it.
public static class Diarization {
  public const string Flag = "--diarize";

  public sealed record Result(List<SpeakerSpan> Spans, Dictionary<string, float[]> Voices);

  sealed record Output(List<SpeakerSpan> Spans, Dictionary<string, float[]> Voices);

  // Parent side

  /// `timeout`: how long the child may take before it is stopped (see `Timeout`).
  public static async Task<Result> Speakers(string wav, TimeSpan timeout, CancellationToken cancel = default) {
    var output = Path.ChangeExtension(wav, ".speakers.json");
    var psi = new ProcessStartInfo(Environment.ProcessPath!) {
      UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true,
    };
    psi.ArgumentList.Add(Flag);
    psi.ArgumentList.Add(wav);
    psi.ArgumentList.Add(output);
    using var p = Process.Start(psi) ?? throw new InvalidOperationException("Speaker detection didn't start.");
    using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
    limit.CancelAfter(timeout);
    try {
      var errors = p.StandardError.ReadToEndAsync(limit.Token);
      try {
        await p.WaitForExitAsync(limit.Token);
      } catch (OperationCanceledException) when (!cancel.IsCancellationRequested) {
        throw new TimeoutException(
          $"Speaker detection took longer than {Math.Max(1, (int)Math.Round(timeout.TotalMinutes))} min and was stopped.");
      }
      if (p.ExitCode != 0) {
        string tail = "";
        try { tail = (await errors).Trim(); } catch { }
        throw new InvalidOperationException($"Speaker detection failed (exit {p.ExitCode}). {tail[Math.Max(0, tail.Length - 300)..]}".TrimEnd());
      }
      var o = JsonSerializer.Deserialize<Output>(await File.ReadAllTextAsync(output, cancel))!;
      var spans = FoldingSnippetSpeakers(MergingEdgeFragments(o.Spans));
      var kept = spans.Select(s => s.Speaker).ToHashSet();
      return new Result(spans, o.Voices.Where(kv => kept.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value));
    } finally {
      // A child that's still running (timeout, cancel) is stopped with everything it started.
      try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
      try { File.Delete(output); } catch { }
    }
  }

  /// Generous, because it's only there for a child that hangs: 2 minutes plus half the audio's
  /// length (201 s of audio took 6.6 s in the Windows VM; a slow PC may need ten times that).
  public static TimeSpan Timeout(double audioSeconds) => TimeSpan.FromSeconds(120 + Math.Max(0, audioSeconds) / 2);

  // Child side

  /// `Kurzschrift.exe --diarize in.wav out.json`; returns the process exit code.
  public static int RunChild(string wav, string output) {
    var samples = ReadWav(wav);
    // A 5 s window step instead of 1 s: 4.5x faster with the same speakers in a 2026-11-14 test
    // (201 s of three voices in the Windows VM: 30 s -> 6.6 s with 4 threads).
    int threads = Math.Clamp(Environment.ProcessorCount, 2, 4);
    var c = new OfflineSpeakerDiarizationConfig();
    c.Segmentation.Pyannote.Model = ModelStore.SegmentationModel;
    c.Segmentation.Pyannote.WindowShiftRatio = 0.5f;
    c.Segmentation.NumThreads = threads;
    c.Embedding.Model = ModelStore.EmbeddingModel;
    c.Embedding.NumThreads = threads;
    c.Clustering.NumClusters = -1;
    c.Clustering.Threshold = 0.5f;
    c.MinDurationOn = 0.3f;
    c.MinDurationOff = 0.5f;
    using var sd = new OfflineSpeakerDiarization(c);
    var segments = sd.Process(samples);
    var spans = segments.Select(s => new SpeakerSpan(s.Speaker.ToString(), s.Start, s.End)).ToList();

    // A voice print per speaker from up to 30 s of their longest stretches.
    var voices = new Dictionary<string, float[]>();
    var ec = new SpeakerEmbeddingExtractorConfig { Model = ModelStore.EmbeddingModel, NumThreads = threads };
    using var extractor = new SpeakerEmbeddingExtractor(ec);
    foreach (var group in spans.GroupBy(s => s.Speaker)) {
      var audio = new List<float>();
      foreach (var s in group.OrderByDescending(s => s.End - s.Start)) {
        int a = (int)(s.Start * Track.Rate), b = Math.Min(samples.Length, (int)(s.End * Track.Rate));
        if (b > a) audio.AddRange(new ArraySegment<float>(samples, a, b - a));
        if (audio.Count >= 30 * Track.Rate) break;
      }
      if (audio.Count < Track.Rate) continue;
      using var stream = extractor.CreateStream();
      stream.AcceptWaveform(Track.Rate, audio.ToArray());
      stream.InputFinished();
      if (extractor.IsReady(stream)) voices[group.Key] = extractor.Compute(stream);
    }
    File.WriteAllText(output, JsonSerializer.Serialize(new Output(spans, voices)));
    return 0;
  }

  static float[] ReadWav(string path) {
    using var reader = new WaveFileReader(path);
    var sp = reader.ToSampleProvider();
    var all = new List<float>((int)(reader.Length / 2));
    var buf = new float[Track.Rate];
    int n;
    while ((n = sp.Read(buf, 0, buf.Length)) > 0) all.AddRange(new ArraySegment<float>(buf, 0, n));
    return all.ToArray();
  }

  // Clean-up of the raw result, as on the Mac (see Diarization.swift for the measurements behind it)

  /// A "speaker" who only ever speaks in snippets and has under 10% of the talking is almost always
  /// someone else's reactions; their snippets go to the nearest real speaker in time.
  public static List<SpeakerSpan> FoldingSnippetSpeakers(List<SpeakerSpan> spans) {
    var total = new Dictionary<string, double>();
    var longest = new Dictionary<string, double>();
    foreach (var s in spans) {
      total[s.Speaker] = total.GetValueOrDefault(s.Speaker) + s.End - s.Start;
      longest[s.Speaker] = Math.Max(longest.GetValueOrDefault(s.Speaker), s.End - s.Start);
    }
    double speech = total.Values.Sum();
    var snippets = total.Keys.Where(k => longest[k] < 8 && total[k] < 0.1 * speech).ToHashSet();
    if (snippets.Count == 0 || snippets.Count >= total.Count) return spans;
    var real = spans.Where(s => !snippets.Contains(s.Speaker)).ToList();
    return spans.Select(span => {
      if (!snippets.Contains(span.Speaker)) return span;
      double Gap(SpeakerSpan o) => Math.Max(0, Math.Max(span.Start - o.End, o.Start - span.End));
      var nearest = real.MinBy(Gap)!;
      return span with { Speaker = nearest.Speaker };
    }).ToList();
  }

  /// A speaker with under 4 s in total whose every span touches another speaker's span is a sliver
  /// from a segmentation window edge: merged into that neighbour.
  public static List<SpeakerSpan> MergingEdgeFragments(List<SpeakerSpan> spans) {
    var result = spans.OrderBy(s => s.Start).ToList();
    var totals = result.GroupBy(s => s.Speaker).ToDictionary(g => g.Key, g => g.Sum(s => s.End - s.Start));
    if (totals.Count <= 1) return result;
    static bool Touches(SpeakerSpan a, SpeakerSpan b) => Math.Abs(a.End - b.Start) <= 0.05 || Math.Abs(b.End - a.Start) <= 0.05;
    foreach (var (speaker, total) in totals) {
      if (total >= 4) continue;
      var indices = Enumerable.Range(0, result.Count).Where(i => result[i].Speaker == speaker).ToList();
      var neighbours = indices.Select(i => Enumerable.Range(0, result.Count)
        .Where(j => result[j].Speaker != speaker && Touches(result[i], result[j]))
        .OrderByDescending(j => result[j].End - result[j].Start).Cast<int?>().FirstOrDefault()).ToList();
      if (neighbours.Any(n => n == null)) continue;
      for (int k = 0; k < indices.Count; k++)
        result[indices[k]] = result[indices[k]] with { Speaker = result[neighbours[k]!.Value].Speaker };
    }
    return result;
  }

  /// With loudspeakers the microphone hears the people in the call too. A microphone speaker whose
  /// voice matches someone in the meeting audio is that person. Keys are "source:speakerId";
  /// returns microphone label → meeting-audio label.
  public static Dictionary<string, string> SameVoices(Dictionary<string, float[]> voices) {
    var meeting = voices.Where(kv => kv.Key.StartsWith("system:")).ToDictionary(kv => kv.Key, kv => VoiceLibrary.Unit(kv.Value));
    var result = new Dictionary<string, string>();
    foreach (var (label, voice) in voices.Where(kv => kv.Key.StartsWith("microphone:"))) {
      var v = VoiceLibrary.Unit(voice);
      var best = meeting.Select(m => (m.Key, Score: VoiceLibrary.Dot(v, m.Value))).OrderByDescending(x => x.Score).FirstOrDefault();
      if (best.Key != null && best.Score >= VoiceLibrary.Threshold) result[label] = best.Key;
    }
    return result;
  }
}

/// Voices you have named, so the same people get their names automatically in later meetings.
/// Stored on this PC only (%APPDATA%\Kurzschrift\voices.json).
public static class VoiceLibrary {
  public sealed class Voice {
    public string Name { get; set; } = "";
    public float[] Embedding { get; set; } = Array.Empty<float>();
    public int Meetings { get; set; }
  }

  /// The Mac's threshold, measured there with a different voice model; not yet re-measured for WeSpeaker.
  public const float Threshold = 0.7f;
  static string FilePath => Path.Combine(Paths.Config, "voices.json");

  public static List<Voice> All() => Load() ?? new();

  /// null when the file is there but can't be read right now: then nothing may be written over it,
  /// or one bad read would cost every remembered voice. A file that isn't JSON any more is set
  /// aside under another name (not deleted) and the library starts empty.
  static List<Voice>? Load() {
    try {
      if (!File.Exists(FilePath)) return new();
      return JsonSerializer.Deserialize<List<Voice>>(File.ReadAllText(FilePath)) ?? new();
    } catch (JsonException) {
      return SafeFile.SetAside(FilePath) ? new() : null;
    } catch (Exception e) {
      Log.Write($"reading voices failed: {e.Message}");
      return null;
    }
  }

  static void Save(List<Voice> voices) {
    if (!SafeFile.Write(FilePath, JsonSerializer.Serialize(voices), out var error)) Log.Write($"saving voices failed: {error}");
  }

  /// Adds or refines a named voice (running average of its embeddings).
  public static void Remember(string name, float[] embedding) {
    if (Load() is not { } voices) {
      Log.Write("voice not remembered: the voices file couldn't be read and isn't overwritten");
      return;
    }
    var e = Unit(embedding);
    var v = voices.FirstOrDefault(x => x.Name == name);
    if (v != null && v.Embedding.Length == e.Length) {
      float n = v.Meetings;
      v.Embedding = Unit(v.Embedding.Zip(e, (a, b) => (a * n + b) / (n + 1)).ToArray());
      v.Meetings++;
    } else {
      voices.RemoveAll(x => x.Name == name);
      voices.Add(new Voice { Name = name, Embedding = e, Meetings = 1 });
    }
    Save(voices);
    Log.Write($"voice remembered ({voices.Count} known)");
  }

  public static void Forget(string name) {
    if (Load() is { } voices) Save(voices.Where(v => v.Name != name).ToList());
  }

  /// Names for this meeting's speakers that match known voices, each name used once.
  public static Dictionary<string, string> Match(Dictionary<string, float[]> speakers) {
    var voices = All();
    var candidates = new List<(string Speaker, string Name, float Score)>();
    foreach (var (speaker, embedding) in speakers) {
      var e = Unit(embedding);
      foreach (var v in voices) {
        if (v.Embedding.Length != e.Length) continue;
        var score = Dot(e, v.Embedding);
        if (score >= Threshold) candidates.Add((speaker, v.Name, score));
      }
    }
    var result = new Dictionary<string, string>();
    foreach (var c in candidates.OrderByDescending(c => c.Score))
      if (!result.ContainsKey(c.Speaker) && !result.ContainsValue(c.Name)) result[c.Speaker] = c.Name;
    return result;
  }

  public static float[] Unit(float[] v) {
    var norm = MathF.Sqrt(v.Sum(x => x * x));
    return norm > 0 ? v.Select(x => x / norm).ToArray() : v;
  }

  public static float Dot(float[] a, float[] b) {
    float s = 0;
    for (int i = 0; i < Math.Min(a.Length, b.Length); i++) s += a[i] * b[i];
    return s;
  }
}

/// The recording kept next to the transcript: compressed if Windows can, otherwise the WAV.
public static class KeptRecording {
  /// Returns the saved file, or null if it couldn't be saved.
  public static string? Save(string wav, string basePath, int kbps) {
    if (kbps > 0) {
      foreach (var (ext, encode) in new (string, Action<string>)[] {
        (".mp3", target => {
          using var reader = new WaveFileReader(wav);
          NAudio.Wave.MediaFoundationEncoder.EncodeToMp3(reader, target, kbps * 1000);
        }),
        (".m4a", target => {
          // Windows' AAC encoder takes only 44.1/48 kHz and offers 96 to 192 kbps: it gets the
          // chosen rate and uses the nearest one it has (96 kbps for every choice in Settings).
          using var reader = new WaveFileReader(wav);
          using var up = new MediaFoundationResampler(reader, new WaveFormat(48000, 16, 1));
          NAudio.Wave.MediaFoundationEncoder.EncodeToAac(up, target, kbps * 1000);
        }),
      }) {
        var target = basePath + ext;
        try {
          encode(target);
          if (new FileInfo(target).Length > 0) return target;
        } catch (Exception e) {
          Log.Write($"compressing to {ext} failed: {e.Message}");
        }
        try { File.Delete(target); } catch { }
      }
    }
    var wavTarget = basePath + ".wav";
    try {
      File.Move(wav, wavTarget, true);
      return wavTarget;
    } catch (Exception e) {
      Log.Write($"keeping the recording failed: {e.Message}");
      return null;
    }
  }

  /// When the recording can't be saved next to the transcript: moves the WAV into the Unsaved
  /// folder as "name.wav" rather than deleting it with the recording folder. Returns where it is
  /// now, or null if even that failed (the WAV then stays where it was).
  public static string? Rescue(string wav, string name) {
    try {
      if (!File.Exists(wav)) return null;
      Directory.CreateDirectory(Paths.Unsaved);
      var target = TranscriptDocument.UniquePath(Paths.Unsaved, name, ".wav");
      File.Move(wav, target);
      Log.Write($"recording kept in the Unsaved folder: {Path.GetFileName(target)}");
      return target;
    } catch (Exception e) {
      Log.Write($"moving the recording to the Unsaved folder failed: {e.Message}");
      return null;
    }
  }
}
