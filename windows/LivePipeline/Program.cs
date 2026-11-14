// Kurzschrift for Windows, live pipeline prototype (no UI).
//
//   LivePipeline devices
//   LivePipeline translate --models DIR --in german.json --out result.jsonl
//   LivePipeline run --models DIR --out DIR [--meeting RENDER] [--mic CAPTURE]
//                    [--play-meeting WAV] [--play-mic WAV --mic-play RENDER] [--seconds N]
//                    [--asr-threads 2] [--mt-threads 2] [--translate true] [--mute-meeting false]
//                    [--vad-max-speech 15] [--vad-min-silence 0.5]
//
// Device options match part of a device's name; "default" (or leaving it out) takes the default.
// With --play-*, the program plays test recordings itself, so playback and capture share one clock.
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using SherpaOnnx;

Console.OutputEncoding = Encoding.UTF8;
var command = args.Length > 0 ? args[0] : "";
var opt = new Dictionary<string, string>();
for (int i = 1; i + 1 < args.Length; i += 2) opt[args[i].TrimStart('-')] = args[i + 1];

try {
  return command switch {
    "devices" => Commands.Devices(),
    "translate" => Commands.Translate(opt),
    "run" => Live.Run(opt),
    "play" => Commands.Play(opt),
    _ => Commands.Usage(),
  };
} catch (Exception e) {
  Console.Error.WriteLine($"error: {e}");
  return 1;
}

static class Json {
  public static readonly JsonSerializerOptions Options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
  public static string Line(object o) => JsonSerializer.Serialize(o, Options);
}

static class Commands {
  public static int Usage() {
    Console.WriteLine("usage: LivePipeline devices | translate ... | run ...  (see Program.cs)");
    return 2;
  }

  /// play --wav FILE --device NAME-PART: plays a file to one output device and waits until it ends.
  public static int Play(Dictionary<string, string> o) {
    var en = new MMDeviceEnumerator();
    var device = en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
      .First(d => d.FriendlyName.Contains(o["device"], StringComparison.OrdinalIgnoreCase));
    using var output = Player.Play(o["wav"], device);
    Console.WriteLine($"playing to {device.FriendlyName}");
    while (output.PlaybackState != PlaybackState.Stopped) Thread.Sleep(200);
    return 0;
  }

  public static int Devices() {
    var en = new MMDeviceEnumerator();
    foreach (var flow in new[] { DataFlow.Render, DataFlow.Capture }) {
      var def = en.HasDefaultAudioEndpoint(flow, Role.Multimedia) ? en.GetDefaultAudioEndpoint(flow, Role.Multimedia).ID : "";
      foreach (var d in en.EnumerateAudioEndPoints(flow, DeviceState.Active)) {
        var mf = d.AudioClient.MixFormat;
        Console.WriteLine($"{flow,-7} {(d.ID == def ? "*" : " ")} {d.FriendlyName}  ({mf.SampleRate} Hz, {mf.Channels} ch)");
      }
    }
    return 0;
  }

  public static int Translate(Dictionary<string, string> o) {
    using var tr = new Translator(Path.Combine(o["models"], "opus-mt-de-en"), int.Parse(o.GetValueOrDefault("mt-threads", "2")));
    var lines = JsonSerializer.Deserialize<string[]>(File.ReadAllText(o["in"]))!;
    using var w = new StreamWriter(o["out"], false, new UTF8Encoding(false));
    var sw = new Stopwatch();
    foreach (var de in lines) {
      sw.Restart();
      var en = tr.Translate(de);
      w.WriteLine(Json.Line(new { de, ids = tr.Encode(de), en, ms = sw.ElapsedMilliseconds }));
    }
    Console.WriteLine($"{lines.Length} lines translated");
    return 0;
  }
}

record Segment(string Track, long StartSample, float[] Samples, double EmittedAt, int AsrQueueAtEmit);

/// Silero VAD for one track: cuts the stream into utterances. While someone is speaking it can also
/// hand out the utterance so far every few seconds, for provisional text.
sealed class VadTrack {
  readonly VoiceActivityDetector vad;
  readonly int window;
  readonly List<float> pending = new();
  readonly Action<long, float[]> onSegment;
  readonly float[] ring = new float[60 * TrackCapture.Rate];  // the last 60 s, by absolute sample index
  long processed;        // samples given to the VAD so far
  bool inSpeech;
  long utteranceStart, lastPartialAt, lastSegmentEnd;
  public Action<long, float[]>? OnPartial;
  public long PartialEvery;  // samples; 0 = no provisional text
  public long LastSegmentEnd => Interlocked.Read(ref lastSegmentEnd);

  public VadTrack(string models, float maxSpeech, float minSilence, Action<long, float[]> onSegment) {
    var c = new VadModelConfig();
    c.SileroVad.Model = Path.Combine(models, "silero_vad.onnx");
    c.SileroVad.Threshold = 0.5f;
    c.SileroVad.MinSilenceDuration = minSilence;
    c.SileroVad.MinSpeechDuration = 0.25f;
    c.SileroVad.MaxSpeechDuration = maxSpeech;
    c.SampleRate = TrackCapture.Rate;
    vad = new VoiceActivityDetector(c, 60);
    window = c.SileroVad.WindowSize;
    this.onSegment = onSegment;
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
        utteranceStart = Math.Max(LastSegmentEnd, processed - (long)(0.8 * TrackCapture.Rate));
        lastPartialAt = processed;
      }
      inSpeech = speech;
      Drain();
      if (OnPartial != null && PartialEvery > 0 && inSpeech && processed - lastPartialAt >= PartialEvery &&
          processed - utteranceStart >= TrackCapture.Rate) {
        long from = Math.Max(utteranceStart, processed - ring.Length);
        var copy = new float[processed - from];
        for (long k = 0; k < copy.Length; k++) copy[k] = ring[(from + k) % ring.Length];
        OnPartial(from, copy);
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
      Interlocked.Exchange(ref lastSegmentEnd, end);
      onSegment(s.Start, s.Samples);
      vad.Pop();
      if (inSpeech) {  // cut at the length limit: the speech goes on
        utteranceStart = end;
        lastPartialAt = processed;
      }
    }
  }
}

static class Live {
  public static int Run(Dictionary<string, string> o) {
    var models = o["models"];
    var outDir = o["out"];
    Directory.CreateDirectory(outDir);
    bool translate = o.GetValueOrDefault("translate", "true") == "true";
    var clock = Stopwatch.StartNew();
    double Now() => clock.Elapsed.TotalSeconds;
    var log = new StreamWriter(Path.Combine(outDir, "segments.jsonl"), false, new UTF8Encoding(false)) { AutoFlush = true };
    var stats = new StreamWriter(Path.Combine(outDir, "stats.jsonl"), false, new UTF8Encoding(false)) { AutoFlush = true };
    var info = new Dictionary<string, object?> { ["started_utc"] = DateTime.UtcNow.ToString("o"), ["options"] = o };

    // Models
    double t0 = Now();
    var p = Path.Combine(models, "sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8");
    var pc = new OfflineRecognizerConfig();
    pc.ModelConfig.Transducer.Encoder = Path.Combine(p, "encoder.int8.onnx");
    pc.ModelConfig.Transducer.Decoder = Path.Combine(p, "decoder.int8.onnx");
    pc.ModelConfig.Transducer.Joiner = Path.Combine(p, "joiner.int8.onnx");
    pc.ModelConfig.Tokens = Path.Combine(p, "tokens.txt");
    pc.ModelConfig.ModelType = "nemo_transducer";
    pc.ModelConfig.NumThreads = int.Parse(o.GetValueOrDefault("asr-threads", "2"));
    using var recognizer = new OfflineRecognizer(pc);
    info["parakeet_load_s"] = Math.Round(Now() - t0, 2);
    t0 = Now();
    using var translator = translate
      ? new Translator(Path.Combine(models, "opus-mt-de-en"), int.Parse(o.GetValueOrDefault("mt-threads", "2"))) : null;
    info["opus_load_s"] = Math.Round(Now() - t0, 2);
    Recognize(recognizer, new float[TrackCapture.Rate]);  // warm-up
    translator?.Translate("Guten Morgen.");

    // Devices
    var en = new MMDeviceEnumerator();
    MMDevice Find(DataFlow flow, string? q) => q is null or "default"
      ? en.GetDefaultAudioEndpoint(flow, Role.Multimedia)
      : en.EnumerateAudioEndPoints(flow, DeviceState.Active).First(d => d.FriendlyName.Contains(q, StringComparison.OrdinalIgnoreCase));
    var meetingDevice = Find(DataFlow.Render, o.GetValueOrDefault("meeting"));
    var micDevice = Find(DataFlow.Capture, o.GetValueOrDefault("mic"));
    // Tests: mute the meeting device so the played recording isn't heard (loopback still gets it).
    bool mute = o.GetValueOrDefault("mute-meeting", "false") == "true";
    bool wasMuted = meetingDevice.AudioEndpointVolume.Mute;
    if (mute) meetingDevice.AudioEndpointVolume.Mute = true;
    info["meeting_muted"] = mute;
    try {
      return RunPipeline(o, models, outDir, translate, clock, log, stats, info, recognizer, translator, en, Find,
        meetingDevice, micDevice);
    } finally {
      if (mute) meetingDevice.AudioEndpointVolume.Mute = wasMuted;
    }
  }

  static int RunPipeline(Dictionary<string, string> o, string models, string outDir, bool translate, Stopwatch clock,
      StreamWriter log, StreamWriter stats, Dictionary<string, object?> info, OfflineRecognizer recognizer,
      Translator? translator, MMDeviceEnumerator en, Func<DataFlow, string?, MMDevice> Find,
      MMDevice meetingDevice, MMDevice micDevice) {
    double Now() => clock.Elapsed.TotalSeconds;

    // Pipeline: capture -> VAD -> Parakeet -> OPUS-MT
    var asrQueue = new BlockingCollection<Segment>();
    var mtQueue = new BlockingCollection<(Segment seg, string de, double asrStart, double asrDone, int mtQueueAtAsr)>();
    var captures = new List<TrackCapture>();
    var vads = new Dictionary<string, VadTrack>();
    var pendingPartials = new ConcurrentDictionary<string, Segment>();  // only the newest per track matters
    var partialSignal = new AutoResetEvent(false);
    long partialEvery = (long)(Num(o, "partial-every", 0) * TrackCapture.Rate);
    foreach (var (name, device, loopback) in new[] { ("others", meetingDevice, true), ("you", micDevice, false) }) {
      var vad = new VadTrack(models, Num(o, "vad-max-speech", 15), Num(o, "vad-min-silence", 0.5f), (start, samples) =>
        asrQueue.Add(new Segment(name, start, samples, Now(), asrQueue.Count)));
      vad.PartialEvery = partialEvery;
      vad.OnPartial = (start, samples) => {
        pendingPartials[name] = new Segment(name, start, samples, Now(), 0);
        partialSignal.Set();
      };
      vads[name] = vad;
      captures.Add(new TrackCapture(name, device, loopback, clock, vad.Feed));
    }
    var byName = captures.ToDictionary(c => c.Name);
    info["tracks"] = captures.Select(c => new { c.Name, device = c.DeviceName, format = c.Format }).ToList();

    int segments = 0, empty = 0;
    var latencies = new List<double>();
    var asrWorker = new Thread(() => {
      foreach (var seg in asrQueue.GetConsumingEnumerable()) {
        double a0 = Now();
        var de = Recognize(recognizer, seg.Samples);
        mtQueue.Add((seg, de, a0, Now(), mtQueue.Count));
      }
      mtQueue.CompleteAdding();
    }) { Name = "asr", IsBackground = true };
    var mtWorker = new Thread(() => {
      foreach (var (seg, de, asrStart, asrDone, mtQ) in mtQueue.GetConsumingEnumerable()) {
        double m0 = Now();
        var pairs = translator != null && de.Length > 0 ? translator.TranslateAll(de) : new();
        var english = string.Join(" ", pairs.Select(x => x.en));
        double done = Now();
        var cap = byName[seg.Track];
        double start = seg.StartSample / (double)TrackCapture.Rate, end = start + seg.Samples.Length / (double)TrackCapture.Rate;
        double wallEnd = cap.StartedAt + end;
        lock (latencies) {
          segments++;
          if (de.Length == 0) empty++;
          latencies.Add(done - wallEnd);
        }
        var line = Json.Line(new {
          kind = "final", track = seg.Track, start = R(start), end = R(end), wall_end = R(wallEnd), emitted = R(seg.EmittedAt),
          asr_start = R(asrStart), asr_done = R(asrDone), mt_done = R(done),
          asr_ms = (int)((asrDone - asrStart) * 1000), mt_ms = (int)((done - m0) * 1000),
          lat_asr = R(asrDone - wallEnd), lat_total = R(done - wallEnd),
          q_asr = seg.AsrQueueAtEmit, q_mt = mtQ, de, en = english,
          shown = pairs.Select(x => new { x.de, x.en }),
        });
        lock (log) log.WriteLine(line);
      }
    }) { Name = "mt", IsBackground = true };

    // Provisional text: re-transcribe the utterance so far; translate its finished sentences at once.
    int partials = 0, skippedPartials = 0;
    var partialMs = new List<int>();
    bool stopping = false;
    var partialWorker = new Thread(() => {
      while (!Volatile.Read(ref stopping)) {
        partialSignal.WaitOne(500);
        foreach (var track in pendingPartials.Keys.ToList()) {
          if (!pendingPartials.TryRemove(track, out var job)) continue;
          long jobEnd = job.StartSample + job.Samples.Length;
          if (vads[track].LastSegmentEnd >= jobEnd - TrackCapture.Rate / 2) { skippedPartials++; continue; }
          double a0 = Now();
          var de = Recognize(recognizer, job.Samples);
          double a1 = Now();
          var sentences = Translator.SplitSentences(de);
          var shown = translator == null ? new() : sentences.Take(Math.Max(0, sentences.Count - 1))
            .Select(s => (de: s, en: translator.TranslateSentence(s))).ToList();
          double done = Now();
          var cap = byName[track];
          double start = job.StartSample / (double)TrackCapture.Rate, end = jobEnd / (double)TrackCapture.Rate;
          partials++;
          lock (partialMs) partialMs.Add((int)((a1 - a0) * 1000));
          var line = Json.Line(new {
            kind = "partial", track, start = R(start), end = R(end), wall_end = R(cap.StartedAt + end),
            emitted = R(job.EmittedAt), asr_start = R(a0), asr_done = R(a1), mt_done = R(done),
            asr_ms = (int)((a1 - a0) * 1000), mt_ms = (int)((done - a1) * 1000), de,
            shown = shown.Select(x => new { x.de, x.en }), tail = sentences.LastOrDefault() ?? "",
          });
          lock (log) log.WriteLine(line);
        }
      }
    }) { Name = "partial", IsBackground = true };
    asrWorker.Start();
    mtWorker.Start();
    partialWorker.Start();

    var proc = Process.GetCurrentProcess();
    var statsTimer = new Timer(_ => {
      proc.Refresh();
      stats.WriteLine(Json.Line(new {
        t = R(Now()), working_set_mb = proc.WorkingSet64 >> 20, private_mb = proc.PrivateMemorySize64 >> 20,
        gc_heap_mb = GC.GetTotalMemory(false) >> 20, q_asr = asrQueue.Count, q_mt = mtQueue.Count, segments,
        short_translations = translator?.ShortOutputs ?? 0,
        tracks = captures.Select(c => new {
          c.Name, behind_s = R(Now() - c.StartedAt - c.Delivered / (double)TrackCapture.Rate),
          padded_s = R(c.PaddedSamples / (double)TrackCapture.Rate),
        }),
      }));
    }, null, 30000, 30000);

    foreach (var c in captures) c.Start();
    info["capture_started"] = R(Now());

    // Test playback, if asked for
    var players = new List<WasapiOut>();
    if (o.TryGetValue("play-meeting", out var meetingWav)) {
      players.Add(Player.Play(meetingWav, meetingDevice));
      info["play_meeting_at"] = R(Now());
    }
    if (o.TryGetValue("play-mic", out var micWav)) {
      players.Add(Player.Play(micWav, Find(DataFlow.Render, o["mic-play"])));
      info["play_mic_at"] = R(Now());
    }
    File.WriteAllText(Path.Combine(outDir, "run.json"), Json.Line(info));
    Console.WriteLine($"running: {string.Join(", ", captures.Select(c => $"{c.Name} = {c.DeviceName} ({c.Format})"))}");

    double limit = double.Parse(o.GetValueOrDefault("seconds", "86400"), System.Globalization.CultureInfo.InvariantCulture);
    double began = Now();
    while (Now() - began < limit) {
      Thread.Sleep(500);
      if (players.Count > 0 && players.All(pl => pl.PlaybackState == PlaybackState.Stopped)) {
        Thread.Sleep(3000);  // let the last words through
        break;
      }
    }

    foreach (var c in captures) c.Stop();
    Thread.Sleep(300);
    foreach (var v in vads.Values) v.Flush();
    asrQueue.CompleteAdding();
    mtWorker.Join();
    Volatile.Write(ref stopping, true);
    partialWorker.Join();
    statsTimer.Dispose();
    partialMs.Sort();
    info["partials"] = new {
      every_s = partialEvery / (double)TrackCapture.Rate, shown = partials, skipped = skippedPartials,
      asr_ms_median = partialMs.Count > 0 ? partialMs[partialMs.Count / 2] : 0,
      asr_ms_p95 = partialMs.Count > 0 ? partialMs[(int)(partialMs.Count * 0.95)] : 0,
      translation_cache_hits = translator?.CacheHits ?? 0,
    };
    proc.Refresh();
    latencies.Sort();
    info["finished"] = R(Now());
    info["captures"] = captures.Select(c => new {
      c.Name, started_at = R(c.StartedAt), delivered_s = R(c.Delivered / (double)TrackCapture.Rate),
      padded_s = R(c.PaddedSamples / (double)TrackCapture.Rate),
    }).ToList();
    info["summary"] = new {
      segments, empty, short_translations = translator?.ShortOutputs ?? 0,
      latency_median = latencies.Count > 0 ? R(latencies[latencies.Count / 2]) : 0,
      latency_p95 = latencies.Count > 0 ? R(latencies[(int)(latencies.Count * 0.95)]) : 0,
      latency_max = latencies.Count > 0 ? R(latencies[^1]) : 0,
      peak_working_set_mb = proc.PeakWorkingSet64 >> 20, private_mb = proc.PrivateMemorySize64 >> 20,
    };
    File.WriteAllText(Path.Combine(outDir, "run.json"), Json.Line(info));
    Console.WriteLine(Json.Line(info["summary"]!));
    foreach (var pl in players) pl.Dispose();
    foreach (var c in captures) c.Dispose();
    return 0;
  }

  static double R(double x) => Math.Round(x, 3);

  static float Num(Dictionary<string, string> o, string key, float fallback) =>
    o.TryGetValue(key, out var v) ? float.Parse(v, System.Globalization.CultureInfo.InvariantCulture) : fallback;

  static string Recognize(OfflineRecognizer r, float[] samples) {
    using var st = r.CreateStream();
    st.AcceptWaveform(TrackCapture.Rate, samples);
    r.Decode(st);
    return st.Result.Text.Trim();
  }
}
