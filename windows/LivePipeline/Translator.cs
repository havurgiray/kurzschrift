using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.ML.OnnxRuntime;

/// OPUS-MT (Marian) German to English with ONNX Runtime: SentencePiece unigram tokenizer read straight
/// from source.spm, beam search with Hugging Face's default stop rule, one sentence at a time.
sealed class Translator : IDisposable {
  readonly InferenceSession encoder, decoder;
  readonly RunOptions run = new();
  readonly Dictionary<string, float> pieces = new();
  readonly int maxPieceLength;
  readonly float unknownScore;
  readonly Dictionary<string, int> vocab;
  readonly string[] idToPiece;
  readonly int eos, pad, unk, start;
  const int Beams = 4;

  public Translator(string dir, int threads) {
    var o = new SessionOptions { IntraOpNumThreads = threads, InterOpNumThreads = 1 };
    encoder = new InferenceSession(Path.Combine(dir, "onnx", "encoder_model_quantized.onnx"), o);
    decoder = new InferenceSession(Path.Combine(dir, "onnx", "decoder_model_quantized.onnx"), o);
    foreach (var (piece, score, type) in ReadSentencePieceModel(Path.Combine(dir, "source.spm"))) {
      if (type is 1 or 4) {  // NORMAL, USER_DEFINED
        pieces[piece] = score;
      }
    }
    maxPieceLength = pieces.Keys.Max(p => p.Length);
    unknownScore = pieces.Values.Min() - 10f;  // SentencePiece's unknown-piece penalty
    vocab = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(Path.Combine(dir, "vocab.json")))!;
    idToPiece = new string[vocab.Values.Max() + 1];
    foreach (var (p, id) in vocab) idToPiece[id] = p;
    using var cfg = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "config.json")));
    eos = cfg.RootElement.GetProperty("eos_token_id").GetInt32();
    pad = cfg.RootElement.GetProperty("pad_token_id").GetInt32();
    start = cfg.RootElement.GetProperty("decoder_start_token_id").GetInt32();
    unk = vocab["<unk>"];
  }

  public int ShortOutputs;  // sentences whose English is under half the German word count
  public int CacheHits;
  readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> cache = new();

  /// Translates sentence by sentence; returns the English text.
  public string Translate(string german) => string.Join(" ", TranslateAll(german).Select(x => x.en));

  public List<(string de, string en)> TranslateAll(string german) =>
    SplitSentences(german).Select(s => (s, TranslateSentence(s))).ToList();

  /// One sentence, remembered: provisional text often translates a sentence before its final version.
  public string TranslateSentence(string s) {
    if (cache.TryGetValue(s, out var cached)) {
      Interlocked.Increment(ref CacheHits);
      return cached;
    }
    var en = TranslateOne(s);
    int deWords = s.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
    int enWords = en.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
    if (deWords >= 6 && enWords * 2 < deWords) Interlocked.Increment(ref ShortOutputs);
    if (cache.Count > 5000) cache.Clear();
    cache[s] = en;
    return en;
  }

  static readonly HashSet<string> Abbreviations = new(StringComparer.OrdinalIgnoreCase) {
    "z.B", "bzw", "ca", "Dr", "Nr", "usw", "etc", "ggf", "inkl", "evtl", "Mio", "Mrd", "Prof", "Hr", "Fr", "St",
    "vgl", "d.h", "u.a", "z.T", "Abs", "Bd", "S", "Mag", "Ing", "DI",
  };

  /// Splits at . ! ? followed by a space and a capital letter, but not after an ordinal ("30.")
  /// or a known abbreviation.
  public static List<string> SplitSentences(string text) {
    var result = new List<string>();
    int begin = 0;
    for (int i = 0; i < text.Length - 2; i++) {
      char c = text[i];
      if (c is not ('.' or '!' or '?') || text[i + 1] != ' ' || !char.IsUpper(text[i + 2])) continue;
      if (c == '.') {
        int w = i;
        while (w > begin && text[w - 1] != ' ') w--;
        var word = text[w..i];
        if (word.Length > 0 && (word.All(char.IsDigit) || Abbreviations.Contains(word) || word.Length == 1)) continue;
      }
      result.Add(text[begin..(i + 1)].Trim());
      begin = i + 2;
    }
    if (begin < text.Length && text[begin..].Trim().Length > 0) result.Add(text[begin..].Trim());
    return result;
  }

  public int[] Encode(string text) {
    var s = Regex.Replace(text.Normalize(NormalizationForm.FormKC), @"\s+", " ").Trim();
    s = "▁" + s.Replace(' ', '▁');
    int n = s.Length;
    var best = new float[n + 1];
    var from = new int[n + 1];
    Array.Fill(best, float.NegativeInfinity);
    best[0] = 0;
    for (int i = 0; i < n; i++) {
      if (float.IsNegativeInfinity(best[i])) continue;
      bool any = false;
      for (int len = 1; len <= maxPieceLength && i + len <= n; len++) {
        if (pieces.TryGetValue(s.Substring(i, len), out var score)) {
          any |= len == 1;
          if (best[i] + score > best[i + len]) { best[i + len] = best[i] + score; from[i + len] = i; }
        }
      }
      if (!any && best[i] + unknownScore > best[i + 1]) { best[i + 1] = best[i] + unknownScore; from[i + 1] = i; }
    }
    var ids = new List<int>();
    for (int j = n; j > 0; j = from[j]) ids.Add(vocab.TryGetValue(s[from[j]..j], out var id) ? id : unk);
    ids.Reverse();
    ids.Add(eos);
    return ids.ToArray();
  }

  public string Decode(IEnumerable<int> ids) {
    var sb = new StringBuilder();
    foreach (var id in ids) if (id != eos && id != pad && id < idToPiece.Length) sb.Append(idToPiece[id]);
    return sb.ToString().Replace('▁', ' ').Trim();
  }

  public string TranslateOne(string german) {
    if (string.IsNullOrWhiteSpace(german)) return "";
    var src = Encode(german);
    int n = src.Length;
    using var ids = OrtValue.CreateTensorValueFromMemory(src.Select(x => (long)x).ToArray(), new long[] { 1, n });
    using var mask = OrtValue.CreateTensorValueFromMemory(Enumerable.Repeat(1L, n).ToArray(), new long[] { 1, n });
    using var encOut = encoder.Run(run, new[] { "input_ids", "attention_mask" }, new[] { ids, mask },
      new[] { "last_hidden_state" });
    var hidden = encOut[0].GetTensorDataAsSpan<float>().ToArray();
    int dModel = hidden.Length / n;
    int maxLen = Math.Min(512, 3 * n + 10);

    var alive = new List<(List<int> seq, double score)> { (new List<int> { start }, 0) };
    var done = new List<(List<int> seq, double norm)>();
    while (true) {
      int b = alive.Count, t = alive[0].seq.Count;
      var logProbs = Step(hidden, n, dModel, alive.Select(a => a.seq).ToList(), b, t);
      var cands = new List<(List<int> seq, double score)>();
      for (int i = 0; i < b; i++) {
        foreach (var (tok, lp) in TopK(logProbs, i, 2 * Beams)) {
          var s = new List<int>(alive[i].seq) { tok };
          cands.Add((s, alive[i].score + lp));
        }
      }
      cands.Sort((x, y) => y.score.CompareTo(x.score));
      alive = new();
      for (int rank = 0; rank < cands.Count && alive.Count < Beams; rank++) {
        var (s, sc) = cands[rank];
        if (s[^1] == eos) {
          if (rank < Beams) done.Add((s, sc / (s.Count - 1)));
        } else {
          alive.Add((s, sc));
        }
      }
      done = done.OrderByDescending(d => d.norm).Take(Beams).ToList();
      int cur = alive.Count > 0 ? alive[0].seq.Count : 0;
      if (alive.Count == 0) break;
      if (done.Count >= Beams && done[^1].norm >= alive[0].score / cur) break;
      if (cur >= maxLen) break;
    }
    var pool = done.Concat(alive.Select(a => (a.seq, norm: a.score / Math.Max(1, a.seq.Count - 1))));
    var best = pool.OrderByDescending(p => p.norm).First().seq;
    return Decode(best.Skip(1));
  }

  float[][] Step(float[] hidden, int n, int dModel, List<List<int>> seqs, int b, int t) {
    var inIds = new long[b * t];
    for (int i = 0; i < b; i++) for (int j = 0; j < t; j++) inIds[i * t + j] = seqs[i][j];
    var enc = new float[b * n * dModel];
    for (int i = 0; i < b; i++) Array.Copy(hidden, 0, enc, i * n * dModel, n * dModel);
    using var vIds = OrtValue.CreateTensorValueFromMemory(inIds, new long[] { b, t });
    using var vMask = OrtValue.CreateTensorValueFromMemory(Enumerable.Repeat(1L, b * n).ToArray(), new long[] { b, n });
    using var vEnc = OrtValue.CreateTensorValueFromMemory(enc, new long[] { b, n, dModel });
    using var outs = decoder.Run(run, new[] { "input_ids", "encoder_attention_mask", "encoder_hidden_states" },
      new[] { vIds, vMask, vEnc }, new[] { "logits" });
    var logits = outs[0].GetTensorDataAsSpan<float>();
    int v = logits.Length / (b * t);
    var result = new float[b][];
    for (int i = 0; i < b; i++) {
      var row = logits.Slice((i * t + t - 1) * v, v).ToArray();
      row[pad] = float.NegativeInfinity;
      float max = row.Max();
      double sum = 0;
      foreach (var x in row) sum += Math.Exp(x - max);
      float logZ = max + (float)Math.Log(sum);
      for (int k = 0; k < v; k++) row[k] -= logZ;
      result[i] = row;
    }
    return result;
  }

  static IEnumerable<(int tok, float lp)> TopK(float[][] lp, int beam, int k) {
    var row = lp[beam];
    var top = new List<(int, float)>(k + 1);
    for (int i = 0; i < row.Length; i++) {
      if (top.Count < k || row[i] > top[^1].Item2) {
        int at = top.FindIndex(x => x.Item2 < row[i]);
        top.Insert(at < 0 ? top.Count : at, (i, row[i]));
        if (top.Count > k) top.RemoveAt(k);
      }
    }
    return top;
  }

  /// Minimal protobuf reader for SentencePiece's ModelProto: field 1 = repeated SentencePiece
  /// { 1: piece, 2: score (float), 3: type }.
  static IEnumerable<(string piece, float score, int type)> ReadSentencePieceModel(string path) {
    var data = File.ReadAllBytes(path);
    int pos = 0;
    while (pos < data.Length) {
      ulong key = Varint(data, ref pos);
      int field = (int)(key >> 3), wire = (int)(key & 7);
      if (wire == 2) {
        int len = (int)Varint(data, ref pos);
        if (field == 1) yield return ParsePiece(data, pos, pos + len);
        pos += len;
      } else Skip(data, ref pos, wire);
    }
  }

  static (string, float, int) ParsePiece(byte[] d, int pos, int end) {
    string piece = ""; float score = 0; int type = 1;
    while (pos < end) {
      ulong key = Varint(d, ref pos);
      int field = (int)(key >> 3), wire = (int)(key & 7);
      if (field == 1 && wire == 2) { int len = (int)Varint(d, ref pos); piece = Encoding.UTF8.GetString(d, pos, len); pos += len; }
      else if (field == 2 && wire == 5) { score = BitConverter.ToSingle(d, pos); pos += 4; }
      else if (field == 3 && wire == 0) type = (int)Varint(d, ref pos);
      else Skip(d, ref pos, wire);
    }
    return (piece, score, type);
  }

  static ulong Varint(byte[] d, ref int pos) {
    ulong r = 0; int shift = 0;
    while (true) { byte b = d[pos++]; r |= (ulong)(b & 0x7F) << shift; if (b < 0x80) return r; shift += 7; }
  }

  static void Skip(byte[] d, ref int pos, int wire) {
    switch (wire) {
      case 0: Varint(d, ref pos); break;
      case 1: pos += 8; break;
      case 2: pos += (int)Varint(d, ref pos); break;
      case 5: pos += 4; break;
      default: throw new InvalidDataException($"protobuf wire type {wire}");
    }
  }

  public void Dispose() { encoder.Dispose(); decoder.Dispose(); run.Dispose(); }
}
