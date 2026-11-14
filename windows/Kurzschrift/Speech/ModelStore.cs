using System.Net.Http;

namespace Kurzschrift;

/// The speech, translation and speaker models, downloaded on first use into
/// %LOCALAPPDATA%\Kurzschrift\Models. Each file comes straight from Hugging Face or GitHub; no archives.
public static class ModelStore {
  public sealed record ModelFile(string Url, string Path, long ApproxBytes);

  public sealed record Model(string Id, string Title, ModelFile[] Files) {
    public bool Installed => Files.All(f => File.Exists(System.IO.Path.Combine(Paths.Models, f.Path)));
    public long ApproxBytes => Files.Sum(f => f.ApproxBytes);
  }

  const string HF = "https://huggingface.co";
  const string ParakeetDir = "parakeet-tdt-0.6b-v3-int8";

  public static readonly Model Parakeet = new("parakeet", "Speech recognition (NVIDIA Parakeet)", new[] {
    new ModelFile($"{HF}/csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8/resolve/main/encoder.int8.onnx", $"{ParakeetDir}/encoder.int8.onnx", 622_000_000),
    new ModelFile($"{HF}/csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8/resolve/main/decoder.int8.onnx", $"{ParakeetDir}/decoder.int8.onnx", 12_000_000),
    new ModelFile($"{HF}/csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8/resolve/main/joiner.int8.onnx", $"{ParakeetDir}/joiner.int8.onnx", 6_400_000),
    new ModelFile($"{HF}/csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8/resolve/main/tokens.txt", $"{ParakeetDir}/tokens.txt", 94_000),
  });

  public static readonly Model Vad = new("vad", "Speech detection (Silero VAD)", new[] {
    new ModelFile("https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/silero_vad.onnx", "silero_vad.onnx", 650_000),
  });

  public static readonly Model Speakers = new("speakers", "Speaker separation (pyannote, WeSpeaker)", new[] {
    new ModelFile($"{HF}/csukuangfj/sherpa-onnx-pyannote-segmentation-3-0/resolve/main/model.onnx", "speakers/segmentation-3.0.onnx", 6_000_000),
    new ModelFile("https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-recongition-models/wespeaker_en_voxceleb_resnet34_LM.onnx", "speakers/wespeaker_en_voxceleb_resnet34_LM.onnx", 27_000_000),
  });

  public static Model Opus(string pair) => new($"opus-{pair}", $"Translation {Languages.Name(pair[..2])} → {Languages.Name(pair[3..])} (OPUS-MT)", new[] {
    new ModelFile($"{HF}/Xenova/opus-mt-{pair}/resolve/main/onnx/encoder_model_quantized.onnx", $"opus-mt-{pair}/onnx/encoder_model_quantized.onnx", 52_000_000),
    new ModelFile($"{HF}/Xenova/opus-mt-{pair}/resolve/main/onnx/decoder_model_quantized.onnx", $"opus-mt-{pair}/onnx/decoder_model_quantized.onnx", 55_000_000),
    new ModelFile($"{HF}/Xenova/opus-mt-{pair}/resolve/main/config.json", $"opus-mt-{pair}/config.json", 2_000),
    new ModelFile($"{HF}/Helsinki-NLP/opus-mt-{pair}/resolve/main/source.spm", $"opus-mt-{pair}/source.spm", 800_000),
    new ModelFile($"{HF}/Helsinki-NLP/opus-mt-{pair}/resolve/main/target.spm", $"opus-mt-{pair}/target.spm", 800_000),
    new ModelFile($"{HF}/Helsinki-NLP/opus-mt-{pair}/resolve/main/vocab.json", $"opus-mt-{pair}/vocab.json", 1_300_000),
  });

  public static string ParakeetFile(string name) => Path.Combine(Paths.Models, ParakeetDir, name);
  public static string VadModel => Path.Combine(Paths.Models, "silero_vad.onnx");
  public static string SegmentationModel => Path.Combine(Paths.Models, "speakers", "segmentation-3.0.onnx");
  public static string EmbeddingModel => Path.Combine(Paths.Models, "speakers", "wespeaker_en_voxceleb_resnet34_LM.onnx");
  public static string OpusFolder(string pair) => Path.Combine(Paths.Models, $"opus-mt-{pair}");

  /// What a meeting with these settings needs that isn't on disk yet.
  public static List<Model> Missing(SessionConfig config) {
    var needed = new List<Model> { Parakeet, Vad };
    if (config.SeparateSpeakers) needed.Add(Speakers);
    if (config.Mode == MeetingMode.Translate && config.Target != null)
      foreach (var pair in Languages.Route(config.Spoken, config.Target) ?? new()) needed.Add(Opus(pair));
    return needed.Where(m => !m.Installed).ToList();
  }

  static readonly HttpClient Http = MakeClient();

  static HttpClient MakeClient() {
    var c = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    c.DefaultRequestHeaders.UserAgent.ParseAdd("Kurzschrift");
    return c;
  }

  /// Downloads the missing files of `models`; reports (bytes done, bytes expected, current title).
  public static async Task Download(IEnumerable<Model> models, IProgress<(long Done, long Total, string Title)> progress,
      CancellationToken cancel) {
    var list = models.ToList();
    var files = list.SelectMany(m => m.Files.Select(f => (m, f)))
      .Where(x => !File.Exists(Path.Combine(Paths.Models, x.f.Path))).ToList();
    long total = files.Sum(x => x.f.ApproxBytes), done = 0;
    foreach (var (model, file) in files) {
      var target = Path.Combine(Paths.Models, file.Path);
      Directory.CreateDirectory(Path.GetDirectoryName(target)!);
      var part = target + ".part";
      Log.Write($"downloading {model.Id}: {Path.GetFileName(file.Path)}");
      using var response = await Http.GetAsync(file.Url, HttpCompletionOption.ResponseHeadersRead, cancel);
      response.EnsureSuccessStatusCode();
      long expected = response.Content.Headers.ContentLength ?? file.ApproxBytes;
      total += expected - file.ApproxBytes;
      await using (var input = await response.Content.ReadAsStreamAsync(cancel))
      await using (var output = File.Create(part)) {
        var buffer = new byte[1 << 16];
        int n;
        long lastReport = 0;
        while ((n = await input.ReadAsync(buffer, cancel)) > 0) {
          await output.WriteAsync(buffer.AsMemory(0, n), cancel);
          done += n;
          if (done - lastReport > 1 << 20) {
            progress.Report((done, total, model.Title));
            lastReport = done;
          }
        }
      }
      if (new FileInfo(part).Length == 0) throw new IOException($"{file.Url} came back empty.");
      File.Move(part, target, true);
    }
    progress.Report((total, total, ""));
    Log.Write($"models ready: {string.Join(", ", list.Select(m => m.Id))}");
  }
}
