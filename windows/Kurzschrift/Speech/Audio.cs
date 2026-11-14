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
  public readonly AudioSourceKind Kind;
  public readonly string FilePath;
  readonly WaveFileWriter writer;
  readonly Action<float[], int> forward;
  readonly object gate = new();
  long samples;
  bool closed;
  double peakSinceCheck;

  /// Seconds of audio so far.
  public double Duration => Interlocked.Read(ref samples) / (double)Rate;

  /// True once anything above near-silence has come in.
  public bool HasSignal { get; private set; }

  public Track(AudioSourceKind kind, string filePath, Action<float[], int> forward) {
    Kind = kind;
    FilePath = filePath;
    this.forward = forward;
    writer = new WaveFileWriter(filePath, new WaveFormat(Rate, 16, 1));
  }

  public void Append(float[] x, int n) {
    lock (gate) {
      if (closed) return;
      for (int i = 0; i < n; i++) {
        var a = Math.Abs(x[i]);
        if (a > peakSinceCheck) peakSinceCheck = a;
      }
      if (peakSinceCheck > 0.003) HasSignal = true;
      writer.WriteSamples(x, 0, n);
      Interlocked.Add(ref samples, n);
      forward(x, n);
    }
  }

  public void Close() {
    lock (gate) {
      if (closed) return;
      closed = true;
      writer.Dispose();
    }
  }

  public void Dispose() => Close();
}

public interface IAudioSource : IDisposable {
  void Start();
  void Stop();
}

/// WASAPI capture: the meeting audio by loopback of an output device, or a microphone. Delivers
/// 16 kHz mono. Loopback sends nothing while nothing plays, so silence is filled in from the clock.
public sealed class WasapiSource : IAudioSource {
  readonly WasapiCapture capture;
  readonly QueueSampleProvider queue;
  readonly ISampleProvider resampled;
  readonly Action<float[], int> deliver;
  readonly Stopwatch clock = new();
  readonly object gate = new();
  readonly float[] outBuf = new float[Track.Rate];
  readonly bool isFloat;
  readonly int channels, bytesPerSample, deviceRate;
  readonly bool loopback;
  Timer? gapTimer;
  long lastDataTicks;
  double startedAt = double.NaN;
  long delivered;
  public readonly string DeviceName;

  WasapiSource(MMDevice device, bool loopback, Action<float[], int> deliver) {
    this.deliver = deliver;
    this.loopback = loopback;
    DeviceName = device.FriendlyName;
    capture = loopback ? new WasapiLoopbackCapture(device) : new WasapiCapture(device, true, 100);
    var wf = capture.WaveFormat;
    channels = wf.Channels;
    bytesPerSample = wf.BitsPerSample / 8;
    deviceRate = wf.SampleRate;
    var floatGuid = new Guid("00000003-0000-0010-8000-00aa00389b71");
    isFloat = wf.Encoding == WaveFormatEncoding.IeeeFloat ||
              (wf.Encoding == WaveFormatEncoding.Extensible && ((WaveFormatExtensible)wf).SubFormat == floatGuid);
    queue = new QueueSampleProvider(wf.SampleRate);
    resampled = wf.SampleRate == Track.Rate ? queue : new WdlResamplingSampleProvider(queue, Track.Rate);
    capture.DataAvailable += OnData;
    capture.RecordingStopped += (_, e) => {
      if (e.Exception != null) Log.Write($"capture stopped ({(loopback ? "meeting audio" : "microphone")}): {e.Exception.Message}");
    };
  }

  /// The meeting audio: everything an output device plays (by id, or Windows' default output).
  public static WasapiSource MeetingAudio(string deviceId, Action<float[], int> deliver) {
    var en = new MMDeviceEnumerator();
    MMDevice? device = null;
    if (!string.IsNullOrEmpty(deviceId)) {
      try { device = en.GetDevice(deviceId); } catch { device = null; }
      if (device?.State != DeviceState.Active) device = null;
    }
    device ??= en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
    return new WasapiSource(device, true, deliver);
  }

  public static List<(string Id, string Name)> Outputs() => Devices(DataFlow.Render);

  /// A microphone by device id, or the default communications microphone.
  public static WasapiSource Microphone(string deviceId, Action<float[], int> deliver) {
    var en = new MMDeviceEnumerator();
    MMDevice? device = null;
    if (!string.IsNullOrEmpty(deviceId)) {
      try { device = en.GetDevice(deviceId); } catch { device = null; }
      if (device?.State != DeviceState.Active) device = null;
    }
    device ??= en.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
    return new WasapiSource(device, false, deliver);
  }

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

  public void Start() {
    clock.Start();
    capture.StartRecording();
    if (loopback) gapTimer = new Timer(_ => FillGap(), null, 100, 100);
  }

  public void Stop() {
    gapTimer?.Dispose();
    gapTimer = null;
    try { capture.StopRecording(); } catch { }
  }

  void OnData(object? sender, WaveInEventArgs e) {
    int frames = e.BytesRecorded / (bytesPerSample * channels);
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
    lock (gate) {
      if (double.IsNaN(startedAt)) startedAt = clock.Elapsed.TotalSeconds - frames / (double)deviceRate;
      lastDataTicks = clock.ElapsedTicks;
      queue.Add(mono);
      int n;
      while ((n = resampled.Read(outBuf, 0, outBuf.Length)) > 0) {
        deliver(outBuf, n);
        delivered += n;
      }
    }
  }

  void FillGap() {
    lock (gate) {
      double now = clock.Elapsed.TotalSeconds;
      if (double.IsNaN(startedAt)) startedAt = now;  // nothing has played since the start
      double sinceData = (clock.ElapsedTicks - lastDataTicks) / (double)Stopwatch.Frequency;
      long expected = (long)((now - startedAt) * Track.Rate);
      long missing = expected - delivered - Track.Rate / 20;
      if ((lastDataTicks != 0 && sinceData < 0.2) || missing <= 0) return;
      var zeros = new float[missing];
      deliver(zeros, zeros.Length);
      delivered += missing;
    }
  }

  public void Dispose() {
    Stop();
    capture.Dispose();
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
