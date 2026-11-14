using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

/// One audio track (meeting audio by loopback, or a microphone), delivered as 16 kHz mono floats.
/// Loopback capture sends nothing while nothing plays, so silence is filled in from the clock to
/// keep the track's sample count in step with real time.
sealed class TrackCapture : IDisposable {
  public const int Rate = 16000;
  public readonly string Name;
  public readonly string DeviceName;
  public readonly string Format;
  readonly WasapiCapture capture;
  readonly QueueSampleProvider queue;
  readonly ISampleProvider resampled;
  readonly Action<float[], int> deliver;
  readonly Stopwatch clock;
  readonly object gate = new();
  readonly float[] outBuf = new float[Rate];
  readonly bool isFloat;
  readonly int channels, bytesPerSample;
  Timer? gapTimer;
  long lastDataTicks;

  public double StartedAt { get; private set; } = double.NaN;  // clock seconds of the first sample
  public long Delivered;      // 16 kHz samples handed on, including filled-in silence
  public long PaddedSamples;  // of which filled-in silence

  public TrackCapture(string name, MMDevice device, bool loopback, Stopwatch clock, Action<float[], int> deliver) {
    Name = name;
    DeviceName = device.FriendlyName;
    this.clock = clock;
    this.deliver = deliver;
    capture = loopback ? new WasapiLoopbackCapture(device) : new WasapiCapture(device, true, 100);
    var wf = capture.WaveFormat;
    channels = wf.Channels;
    bytesPerSample = wf.BitsPerSample / 8;
    var floatGuid = new Guid("00000003-0000-0010-8000-00aa00389b71");
    isFloat = wf.Encoding == WaveFormatEncoding.IeeeFloat ||
              (wf.Encoding == WaveFormatEncoding.Extensible && ((WaveFormatExtensible)wf).SubFormat == floatGuid);
    Format = $"{wf.SampleRate} Hz, {wf.Channels} ch, {wf.BitsPerSample} bit {(isFloat ? "float" : "PCM")}";
    queue = new QueueSampleProvider(wf.SampleRate);
    resampled = wf.SampleRate == Rate ? queue : new WdlResamplingSampleProvider(queue, Rate);
    capture.DataAvailable += OnData;
  }

  public void Start() {
    capture.StartRecording();
    gapTimer = new Timer(_ => FillGap(), null, 100, 100);
  }

  public void Stop() {
    gapTimer?.Dispose();
    capture.StopRecording();
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
      if (double.IsNaN(StartedAt)) StartedAt = clock.Elapsed.TotalSeconds - frames / (double)capture.WaveFormat.SampleRate;
      lastDataTicks = clock.ElapsedTicks;
      queue.Add(mono);
      int n;
      while ((n = resampled.Read(outBuf, 0, outBuf.Length)) > 0) {
        deliver(outBuf, n);
        Delivered += n;
      }
    }
  }

  void FillGap() {
    lock (gate) {
      if (double.IsNaN(StartedAt)) return;
      double now = clock.Elapsed.TotalSeconds;
      double sinceData = (clock.ElapsedTicks - lastDataTicks) / (double)Stopwatch.Frequency;
      long expected = (long)((now - StartedAt) * Rate);
      long missing = expected - Delivered - Rate / 20;  // keep 50 ms of slack for buffered audio
      if (sinceData < 0.2 || missing <= 0) return;
      var zeros = new float[missing];
      deliver(zeros, zeros.Length);
      Delivered += missing;
      PaddedSamples += missing;
    }
  }

  public void Dispose() {
    gapTimer?.Dispose();
    capture.Dispose();
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

/// Plays a WAV file to one output device (for tests: the meeting audio, or the "microphone" via a
/// virtual cable).
static class Player {
  public static WasapiOut Play(string wav, MMDevice device) {
    var reader = new WaveFileReader(wav);
    ISampleProvider sp = reader.ToSampleProvider();
    if (sp.WaveFormat.Channels == 2) sp = new StereoToMonoSampleProvider(sp);
    var mix = device.AudioClient.MixFormat;
    if (sp.WaveFormat.SampleRate != mix.SampleRate) sp = new WdlResamplingSampleProvider(sp, mix.SampleRate);
    if (mix.Channels >= 2) {
      var m = new MultiplexingSampleProvider(new[] { sp }, mix.Channels);
      for (int c = 0; c < mix.Channels; c++) m.ConnectInputToOutput(0, c);
      sp = m;
    }
    var output = new WasapiOut(device, AudioClientShareMode.Shared, true, 100);
    output.Init(new SampleToWaveProvider(sp));
    output.Play();
    return output;
  }
}
