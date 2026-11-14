using SherpaOnnx;

namespace Kurzschrift;

/// A word as recognized, including its leading space and trailing punctuation; seconds on its track.
public sealed record TimedWord(string Text, double Start, double End);

/// A finished stretch of speech on one track.
public sealed class FinalSegment {
  public Guid Id { get; init; } = Guid.NewGuid();
  public AudioSourceKind Source { get; init; }
  public string Text { get; set; } = "";
  public double Start { get; init; }
  public double End { get; init; }
  public List<TimedWord> Words { get; set; } = new();
  /// The language guessed from the text ("de"), "" when unclear.
  public string Language { get; set; } = "";
}

/// Silero VAD for one track: cuts the stream into utterances (finals). While someone is speaking it
/// also hands out the utterance so far every few seconds, for provisional text.
public sealed class VadSegmenter {
  readonly VoiceActivityDetector vad;
  readonly int window;
  readonly List<float> pending = new();
  readonly float[] ring = new float[60 * Track.Rate];
  long processed;
  bool inSpeech;
  long utteranceStart, lastPartialAt, lastSegmentEnd;
  readonly Action<long, float[]> onFinal;
  readonly Action<long, float[]>? onPartial;
  readonly long partialEvery;

  public VadSegmenter(float partialSeconds, Action<long, float[]> onFinal, Action<long, float[]>? onPartial) {
    var c = new VadModelConfig();
    c.SileroVad.Model = ModelStore.VadModel;
    c.SileroVad.Threshold = 0.5f;
    c.SileroVad.MinSilenceDuration = 0.5f;
    c.SileroVad.MinSpeechDuration = 0.25f;
    c.SileroVad.MaxSpeechDuration = 15f;
    c.SampleRate = Track.Rate;
    vad = new VoiceActivityDetector(c, 60);
    window = c.SileroVad.WindowSize;
    this.onFinal = onFinal;
    this.onPartial = onPartial;
    partialEvery = (long)(partialSeconds * Track.Rate);
  }

  public void Feed(float[] x, int n) {
    for (int i = 0; i < n; i++) pending.Add(x[i]);
    int at = 0;
    while (pending.Count - at >= window) {
      var w = pending.GetRange(at, window).ToArray();
      for (int j = 0; j < w.Length; j++) ring[(processed + j) % ring.Length] = w[j];
      vad.AcceptWaveform(w);
      processed += window;
      at += window;
      bool speech = vad.IsSpeechDetected();
      if (speech && !inSpeech) {
        // Silero needs a moment to be sure; start a little earlier than the detection.
        utteranceStart = Math.Max(lastSegmentEnd, processed - (long)(0.8 * Track.Rate));
        lastPartialAt = processed;
      }
      inSpeech = speech;
      Drain();
      if (onPartial != null && partialEvery > 0 && inSpeech && processed - lastPartialAt >= partialEvery &&
          processed - utteranceStart >= Track.Rate) {
        long from = Math.Max(utteranceStart, processed - ring.Length);
        var copy = new float[processed - from];
        for (long k = 0; k < copy.Length; k++) copy[k] = ring[(from + k) % ring.Length];
        onPartial(from, copy);
        lastPartialAt = processed;
      }
    }
    pending.RemoveRange(0, at);
  }

  public void Flush() {
    vad.Flush();
    Drain();
  }

  void Drain() {
    while (!vad.IsEmpty()) {
      var s = vad.Front();
      long end = s.Start + s.Samples.Length;
      lastSegmentEnd = end;
      onFinal(s.Start, s.Samples);
      vad.Pop();
      if (inSpeech) {  // cut at the length limit: the speech goes on
        utteranceStart = end;
        lastPartialAt = processed;
      }
    }
  }
}

/// Parakeet TDT 0.6B v3 (sherpa-onnx, int8): text and word times for a clip.
public sealed class Recognizer : IDisposable {
  readonly OfflineRecognizer recognizer;

  public Recognizer(int threads) {
    var c = new OfflineRecognizerConfig();
    c.ModelConfig.Transducer.Encoder = ModelStore.ParakeetFile("encoder.int8.onnx");
    c.ModelConfig.Transducer.Decoder = ModelStore.ParakeetFile("decoder.int8.onnx");
    c.ModelConfig.Transducer.Joiner = ModelStore.ParakeetFile("joiner.int8.onnx");
    c.ModelConfig.Tokens = ModelStore.ParakeetFile("tokens.txt");
    c.ModelConfig.ModelType = "nemo_transducer";
    c.ModelConfig.NumThreads = threads;
    recognizer = new OfflineRecognizer(c);
  }

  /// `offset`: the clip's start on its track, added to the word times.
  public (string Text, List<TimedWord> Words) Recognize(float[] samples, double offset) {
    using var stream = recognizer.CreateStream();
    stream.AcceptWaveform(Track.Rate, samples);
    recognizer.Decode(stream);
    var result = stream.Result;
    var words = new List<TimedWord>();
    var tokens = result.Tokens ?? Array.Empty<string>();
    var times = result.Timestamps ?? Array.Empty<float>();
    var durations = result.Durations ?? Array.Empty<float>();
    string? text = null;
    double start = 0, end = 0;
    for (int i = 0; i < tokens.Length && i < times.Length; i++) {
      var tok = tokens[i];
      double t0 = offset + times[i];
      double t1 = i < durations.Length && durations[i] > 0 ? t0 + durations[i]
        : i + 1 < times.Length ? offset + times[i + 1] : t0 + 0.2;
      bool startsWord = tok.StartsWith('▁') || text == null;
      if (startsWord && text != null) words.Add(new TimedWord(text, start, end));
      if (startsWord) {
        text = " " + tok.TrimStart('▁');
        start = t0;
      } else {
        text += tok;
      }
      end = t1;
    }
    if (text != null) words.Add(new TimedWord(text, start, end));
    return (TextCleanup.Tidy(result.Text ?? ""), words);
  }

  public void Dispose() => recognizer.Dispose();
}

/// Runs Parakeet for both tracks on one thread: finished utterances first, provisional ones when
/// there's time (only the newest per track; one that a finished utterance already covers is skipped).
/// One model run at a time keeps memory near 1.9 GB; two in parallel took 3 GB in the tests.
public sealed class SpeechEngine : IDisposable {
  public sealed record Job(AudioSourceKind Source, long StartSample, float[] Samples, bool Final);

  readonly Task<Recognizer> loading;
  Recognizer? recognizer;
  readonly object gate = new();
  readonly Queue<Job> finals = new();
  readonly Dictionary<AudioSourceKind, Job> partials = new();
  readonly Dictionary<AudioSourceKind, long> finalizedThrough = new();
  readonly Thread worker;
  bool finishing, stopped;

  /// Results arrive on the worker thread.
  public event Action<Job, string, List<TimedWord>>? Recognized;
  /// The model couldn't be loaded; nothing will be recognized.
  public event Action<Exception>? LoadFailed;

  /// Starts taking audio at once; the model loads meanwhile and catches up on what was said.
  public SpeechEngine(Task<Recognizer> loading) {
    this.loading = loading;
    worker = new Thread(Run) { IsBackground = true, Name = "speech" };
    worker.Start();
  }

  public void SubmitFinal(AudioSourceKind source, long start, float[] samples) {
    lock (gate) {
      finals.Enqueue(new Job(source, start, samples, true));
      finalizedThrough[source] = start + samples.Length;
      Monitor.Pulse(gate);
    }
  }

  public void SubmitPartial(AudioSourceKind source, long start, float[] samples) {
    lock (gate) {
      partials[source] = new Job(source, start, samples, false);
      Monitor.Pulse(gate);
    }
  }

  public int PendingFinals { get { lock (gate) return finals.Count; } }

  /// Recognizes what's queued, then stops the worker.
  public void Finish() {
    lock (gate) {
      finishing = true;
      partials.Clear();
      Monitor.Pulse(gate);
    }
    worker.Join();
  }

  void Run() {
    try {
      recognizer = loading.GetAwaiter().GetResult();
    } catch (Exception e) {
      Log.Write($"loading Parakeet failed: {e.Message}");
      LoadFailed?.Invoke(e);
      return;
    }
    while (true) {
      Job? job;
      lock (gate) {
        while (finals.Count == 0 && (partials.Count == 0 || finishing) && !stopped && !finishing) Monitor.Wait(gate);
        if (stopped) return;
        if (finals.Count > 0) {
          job = finals.Dequeue();
        } else if (!finishing && partials.Count > 0) {
          var key = partials.Keys.First();
          job = partials[key];
          partials.Remove(key);
          long through = finalizedThrough.GetValueOrDefault(job.Source);
          if (through >= job.StartSample + job.Samples.Length - Track.Rate / 2) continue;
        } else if (finishing) {
          return;
        } else {
          continue;
        }
      }
      try {
        var (text, words) = recognizer!.Recognize(job.Samples, job.StartSample / (double)Track.Rate);
        Recognized?.Invoke(job, text, words);
      } catch (Exception e) {
        Log.Write($"recognition failed: {e.Message}");
      }
    }
  }

  public void Dispose() {
    lock (gate) {
      stopped = true;
      Monitor.Pulse(gate);
    }
    worker.Join(5000);
    recognizer?.Dispose();
  }
}
