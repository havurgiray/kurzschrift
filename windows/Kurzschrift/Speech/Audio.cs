using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Kurzschrift;

public enum AudioSourceKind { System, Microphone }

public static class AudioSourceKindExtensions {
  /// The Mac version's names, used in speaker labels ("system:3").
  public static string Key(this AudioSourceKind k) => k == AudioSourceKind.System ? "system" : "microphone";
}

/// One source's audio as 16 kHz mono: written to a WAV (for speaker detection and the kept
/// recording) and handed on for speech detection. Times on a track are sample counts / 16000.
public sealed class Track : IDisposable {
  public const int Rate = 16000;
  /// The WAV header is brought up to date this often, so a recording that's cut off (crash, power
  /// loss, forced quit) is a valid file up to a few seconds before the end.
  const int FlushEvery = 5 * Rate;
  public readonly AudioSourceKind Kind;
  public readonly string FilePath;
  readonly WaveFileWriter writer;
  readonly Action<float[], int> forward;
  readonly Action? flush;
  readonly object gate = new();
  long samples;
  int sinceFlush;
  bool closed, writeFailed, forwardFailed;
  double peakSinceCheck;

  /// Seconds of audio so far.
  public double Duration => Interlocked.Read(ref samples) / (double)Rate;

  /// True once anything above near-silence has come in.
  public bool HasSignal { get; private set; }

  /// The WAV couldn't be written any more (disk full, folder gone). Raised once, on the audio
  /// thread; speech recognition goes on without the recording.
  public event Action<Exception>? WriteFailed;

  /// `flush` finishes the speech detector that `forward` feeds; `Close` calls it under the track's
  /// lock, so no audio callback can still be feeding the detector at that moment.
  public Track(AudioSourceKind kind, string filePath, Action<float[], int> forward, Action? flush = null) {
    Kind = kind;
    FilePath = filePath;
    this.forward = forward;
    this.flush = flush;
    writer = new WaveFileWriter(filePath, new WaveFormat(Rate, 16, 1));
  }

  /// Never throws: it runs on capture and timer threads, where an exception would end the capture
  /// or the whole app.
  public void Append(float[] x, int n) {
    Exception? failure = null;
    lock (gate) {
      if (closed) return;
      for (int i = 0; i < n; i++) {
        var a = Math.Abs(x[i]);
        if (a > peakSinceCheck) peakSinceCheck = a;
      }
      if (peakSinceCheck > 0.003) HasSignal = true;
      if (!writeFailed) {
        try {
          writer.WriteSamples(x, 0, n);
          sinceFlush += n;
          if (sinceFlush >= FlushEvery) {
            writer.Flush();
            sinceFlush = 0;
          }
        } catch (Exception e) {
          writeFailed = true;
          failure = e;
        }
      }
      Interlocked.Add(ref samples, n);
      try {
        forward(x, n);
      } catch (Exception e) {
        if (!forwardFailed) Log.Write($"speech detection failed ({Kind.Key()}): {e.Message}");
        forwardFailed = true;
      }
    }
    if (failure != null) {
      Log.Write($"writing the recording failed ({Kind.Key()}): {failure.Message}");
      try { WriteFailed?.Invoke(failure); } catch { }
    }
  }

  /// Finishes the speech detector (unless `finishSpeech` is false) and the WAV. Never throws.
  public void Close(bool finishSpeech = true) {
    lock (gate) {
      if (closed) return;
      closed = true;
      if (finishSpeech && flush != null) {
        try {
          flush();
        } catch (Exception e) {
          Log.Write($"finishing speech detection failed ({Kind.Key()}): {e.Message}");
        }
      }
      try {
        writer.Dispose();
      } catch (Exception e) {
        Log.Write($"closing the recording failed ({Kind.Key()}): {e.Message}");
      }
    }
  }

  public void Dispose() => Close(false);
}

public interface IAudioSource : IDisposable {
  void Start();
  void Stop();
}

/// WASAPI capture: the meeting audio by loopback of an output device, or a microphone. Delivers
/// 16 kHz mono. Loopback sends nothing while nothing plays, so silence is filled in from the clock.
/// When the device goes away (unplugged, switched off) or Windows' default device changes, it opens
/// the device that's current now and carries on, on the same timeline.
public sealed class WasapiSource : IAudioSource {
  readonly bool loopback;
  /// The device asked for; "" = Windows' default, which is followed when it changes.
  readonly string deviceId;
  readonly Action<float[], int> deliver;
  readonly Stopwatch clock = new();
  readonly object gate = new();
  readonly float[] outBuf = new float[Track.Rate];
  static readonly float[] Silence = new float[Track.Rate];
  static readonly Guid FloatFormat = new("00000003-0000-0010-8000-00aa00389b71");

  // The open device; replaced under `gate` when the capture is reopened.
  WasapiCapture? capture;
  string openId = "";
  QueueSampleProvider queue = new(Track.Rate);
  ISampleProvider resampled;
  bool isFloat;
  int channels = 1, bytesPerSample = 4, deviceRate = Track.Rate;

  Timer? timer;
  int timerBusy;
  long ticks;
  long lastDataTicks;
  double startedAt = double.NaN;
  long delivered;
  volatile bool stopping, interrupted;
  bool failureLogged, retryLogged;
  public string DeviceName { get; private set; } = "";

  /// The capture stopped by itself (device unplugged or switched off). May fire on any thread.
  public event Action? Interrupted;
  /// A device is open again after an interruption. May fire on any thread.
  public event Action? Resumed;

  WasapiSource(bool loopback, string deviceId, Action<float[], int> deliver) {
    this.loopback = loopback;
    this.deviceId = deviceId ?? "";
    this.deliver = deliver;
    resampled = queue;
    Open();
  }

  string What => loopback ? "meeting audio" : "microphone";

  /// The meeting audio: everything an output device plays (by id, or Windows' default output).
  public static WasapiSource MeetingAudio(string deviceId, Action<float[], int> deliver) => new(true, deviceId, deliver);

  public static List<(string Id, string Name)> Outputs() => Devices(DataFlow.Render);

  /// A microphone by device id, or the default communications microphone.
  public static WasapiSource Microphone(string deviceId, Action<float[], int> deliver) => new(false, deviceId, deliver);

  public static List<(string Id, string Name)> Microphones() => Devices(DataFlow.Capture);

  static List<(string Id, string Name)> Devices(DataFlow flow) {
    var list = new List<(string, string)>();
    try {
      foreach (var d in new MMDeviceEnumerator().EnumerateAudioEndPoints(flow, DeviceState.Active))
        list.Add((d.ID, d.FriendlyName));
    } catch (Exception e) {
      Log.Write($"listing audio devices failed: {e.Message}");
    }
    return list;
  }

  /// The device to capture right now: the chosen one while it's connected, otherwise Windows' default.
  MMDevice Wanted(MMDeviceEnumerator en) {
    MMDevice? device = null;
    if (deviceId.Length > 0) {
      try { device = en.GetDevice(deviceId); } catch { device = null; }
      if (device?.State != DeviceState.Active) device = null;
    }
    return device ?? en.GetDefaultAudioEndpoint(loopback ? DataFlow.Render : DataFlow.Capture,
      loopback ? Role.Multimedia : Role.Communications);
  }

  /// Opens the wanted device (not yet recording). Throws when there is none.
  void Open() {
    var device = Wanted(new MMDeviceEnumerator());
    var c = loopback ? new WasapiLoopbackCapture(device) : new WasapiCapture(device, true, 100);
    var wf = c.WaveFormat;
    c.DataAvailable += OnData;
    c.RecordingStopped += OnStopped;
    lock (gate) {
      channels = Math.Max(1, wf.Channels);
      bytesPerSample = Math.Max(1, wf.BitsPerSample / 8);
      deviceRate = wf.SampleRate;
      isFloat = wf.Encoding == WaveFormatEncoding.IeeeFloat ||
                (wf.Encoding == WaveFormatEncoding.Extensible && ((WaveFormatExtensible)wf).SubFormat == FloatFormat);
      queue = new QueueSampleProvider(wf.SampleRate);
      resampled = wf.SampleRate == Track.Rate ? queue : new WdlResamplingSampleProvider(queue, Track.Rate);
      capture = c;
      openId = device.ID;
      DeviceName = device.FriendlyName;
    }
  }

  public void Start() {
    clock.Start();
    capture!.StartRecording();
    timer = new Timer(_ => OnTimer(), null, 100, 100);
  }

  public void Stop() {
    stopping = true;
    timer?.Dispose();
    timer = null;
    try { capture?.StopRecording(); } catch { }
  }

  void OnStopped(object? sender, StoppedEventArgs e) {
    // Only flags here: this can run on the capture's own thread, which Reopen() waits for.
    if (stopping || !ReferenceEquals(sender, capture)) return;
    Log.Write($"capture stopped ({What}): {e.Exception?.Message ?? "no reason given"}");
    if (interrupted) return;
    interrupted = true;
    retryLogged = false;
    try { Interrupted?.Invoke(); } catch { }
  }

  void OnData(object? sender, WaveInEventArgs e) {
    try {
      lock (gate) {
        if (stopping || !ReferenceEquals(sender, capture)) return;
        int frames = e.BytesRecorded / (bytesPerSample * channels);
        if (frames <= 0) return;
        var mono = new float[frames];
        var b = e.Buffer;
        for (int f = 0; f < frames; f++) {
          float sum = 0;
          for (int c = 0; c < channels; c++) {
            int at = (f * channels + c) * bytesPerSample;
            sum += isFloat ? BitConverter.ToSingle(b, at)
              : bytesPerSample switch {
                2 => BitConverter.ToInt16(b, at) / 32768f,
                3 => ((b[at] << 8 | b[at + 1] << 16 | b[at + 2] << 24) >> 8) / 8388608f,
                4 => BitConverter.ToInt32(b, at) / 2147483648f,
                _ => 0,
              };
          }
          mono[f] = sum / channels;
        }
        if (double.IsNaN(startedAt)) startedAt = clock.Elapsed.TotalSeconds - frames / (double)deviceRate;
        lastDataTicks = clock.ElapsedTicks;
        queue.Add(mono);
        int n;
        while ((n = resampled.Read(outBuf, 0, outBuf.Length)) > 0) {
          deliver(outBuf, n);
          delivered += n;
        }
      }
    } catch (Exception ex) {
      LogFailure(ex);
    }
  }

  void LogFailure(Exception e) {
    if (!failureLogged) Log.Write($"audio handling failed ({What}): {e.Message}");
    failureLogged = true;
  }

  /// Every 100 ms: fills gaps with silence, and every 2 s looks whether the device should be
  /// reopened. Never throws (an exception on a timer thread would end the app).
  void OnTimer() {
    if (Interlocked.Exchange(ref timerBusy, 1) == 1) return;
    try {
      if (stopping) return;
      FillGap();
      if (++ticks % 20 != 0 || stopping) return;
      if (interrupted) {
        try {
          Reopen();
          interrupted = false;
          Log.Write($"capture resumed ({What})");
          try { Resumed?.Invoke(); } catch { }
        } catch (Exception e) {
          if (!retryLogged) Log.Write($"reopening the {What} failed, trying again: {e.Message}");
          retryLogged = true;
        }
      } else {
        FollowDevice();
      }
    } catch (Exception e) {
      LogFailure(e);
    } finally {
      Volatile.Write(ref timerBusy, 0);
    }
  }

  /// Windows' default device changed (a headset was plugged in), or the chosen device came back.
  void FollowDevice() {
    string wanted;
    try {
      using var en = new MMDeviceEnumerator();
      using var device = Wanted(en);
      wanted = device.ID;
    } catch {
      return;  // no device at all right now; the capture's own stop event handles that
    }
    if (wanted == openId) return;
    Log.Write($"the {What} device changed; switching to it");
    try {
      Reopen();
    } catch (Exception e) {
      Log.Write($"switching the {What} device failed: {e.Message}");
      if (!interrupted) {
        interrupted = true;
        retryLogged = true;
        try { Interrupted?.Invoke(); } catch { }
      }
    }
  }

  /// Closes the current capture and opens the wanted device. The clock and the sample count carry
  /// on, so the time without a device becomes silence on the track. Runs on the timer thread.
  void Reopen() {
    WasapiCapture? old;
    lock (gate) {
      old = capture;
      capture = null;
    }
    if (old != null) {
      old.DataAvailable -= OnData;
      old.RecordingStopped -= OnStopped;
      try { old.Dispose(); } catch { }
    }
    Open();
    WasapiCapture? opened;
    lock (gate) opened = capture;
    if (stopping) {
      try { opened?.Dispose(); } catch { }
      return;
    }
    opened!.StartRecording();
  }

  void FillGap() {
    lock (gate) {
      double now = clock.Elapsed.TotalSeconds;
      if (double.IsNaN(startedAt)) {
        if (!loopback && now < 1) return;  // a microphone's first samples take a moment
        startedAt = now;  // nothing has played since the start
      }
      double sinceData = (clock.ElapsedTicks - lastDataTicks) / (double)Stopwatch.Frequency;
      long expected = (long)((now - startedAt) * Track.Rate);
      long missing = expected - delivered - Track.Rate / 20;
      // Loopback is silent whenever nothing plays; a microphone only when its device is gone.
      bool flowing = loopback ? lastDataTicks != 0 && sinceData < 0.2 : sinceData < 1.0;
      if (flowing || missing <= 0) return;
      while (missing > 0 && !stopping) {
        int n = (int)Math.Min(missing, Silence.Length);
        deliver(Silence, n);
        delivered += n;
        missing -= n;
      }
    }
  }

  public void Dispose() {
    Stop();
    // A reopen that's under way finishes first (it checks `stopping` before it starts recording).
    for (int i = 0; i < 300 && Volatile.Read(ref timerBusy) == 1; i++) Thread.Sleep(10);
    WasapiCapture? c;
    lock (gate) {
      c = capture;
      capture = null;
    }
    try { c?.Dispose(); } catch { }
  }
}

/// Replays an audio file in real time instead of capturing (developer links, tests).
public sealed class FileSource : IAudioSource {
  readonly ISampleProvider provider;
  readonly IDisposable reader;
  readonly Action<float[], int> deliver;
  Thread? thread;
  volatile bool stopping;
  public event Action? Finished;

  public FileSource(string path, Action<float[], int> deliver) {
    this.deliver = deliver;
    var r = new MediaFoundationReader(path);
    reader = r;
    ISampleProvider sp = r.ToSampleProvider();
    if (sp.WaveFormat.Channels > 1) sp = Mono(sp);
    if (sp.WaveFormat.SampleRate != Track.Rate) sp = new WdlResamplingSampleProvider(sp, Track.Rate);
    provider = sp;
  }

  static ISampleProvider Mono(ISampleProvider sp) => sp.WaveFormat.Channels == 2
    ? new StereoToMonoSampleProvider(sp)
    : new DownmixProvider(sp);

  public void Start() {
    thread = new Thread(() => {
      var buf = new float[Track.Rate / 10];
      var clock = Stopwatch.StartNew();
      long sent = 0;
      while (!stopping) {
        int n = provider.Read(buf, 0, buf.Length);
        if (n <= 0) break;
        deliver(buf, n);
        sent += n;
        var ahead = sent / (double)Track.Rate - clock.Elapsed.TotalSeconds;
        if (ahead > 0) Thread.Sleep(TimeSpan.FromSeconds(ahead));
      }
      if (!stopping) Finished?.Invoke();
    }) { IsBackground = true, Name = "file audio" };
    thread.Start();
  }

  public void Stop() {
    stopping = true;
    thread?.Join(2000);
  }

  public void Dispose() {
    Stop();
    reader.Dispose();
  }

  /// Averages any number of channels into one.
  sealed class DownmixProvider : ISampleProvider {
    readonly ISampleProvider source;
    readonly int channels;
    float[] buffer = Array.Empty<float>();
    public DownmixProvider(ISampleProvider source) {
      this.source = source;
      channels = source.WaveFormat.Channels;
      WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);
    }
    public WaveFormat WaveFormat { get; }
    public int Read(float[] dest, int offset, int count) {
      int need = count * channels;
      if (buffer.Length < need) buffer = new float[need];
      int got = source.Read(buffer, 0, need) / channels;
      for (int i = 0; i < got; i++) {
        float s = 0;
        for (int c = 0; c < channels; c++) s += buffer[i * channels + c];
        dest[offset + i] = s / channels;
      }
      return got;
    }
  }
}

/// Mono float samples at the device rate, read as far as they go (no padding).
sealed class QueueSampleProvider : ISampleProvider {
  readonly Queue<float[]> chunks = new();
  float[]? current;
  int pos;
  public QueueSampleProvider(int rate) => WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(rate, 1);
  public WaveFormat WaveFormat { get; }
  public void Add(float[] x) => chunks.Enqueue(x);

  public int Read(float[] buffer, int offset, int count) {
    int written = 0;
    while (written < count) {
      if (current == null || pos >= current.Length) {
        if (!chunks.TryDequeue(out current)) { current = null; break; }
        pos = 0;
      }
      int n = Math.Min(count - written, current.Length - pos);
      Array.Copy(current, pos, buffer, offset + written, n);
      pos += n;
      written += n;
    }
    return written;
  }
}
