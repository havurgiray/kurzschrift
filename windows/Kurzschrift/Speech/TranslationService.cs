using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace Kurzschrift;

/// Translates from the spoken language to the target: one OPUS-MT model, or two chained through
/// English. Sentence by sentence (Marian models drop sentences from long passages), remembered, so
/// provisional text and the final version don't translate the same sentence twice. Thread-safe.
public sealed class TranslationService : IDisposable {
  readonly List<OpusModel> chain = new();
  readonly ConcurrentDictionary<string, string> cache = new();
  readonly object run = new();
  public readonly string From, To;

  public TranslationService(string from, string to, int threads) {
    From = Languages.Base(from);
    To = Languages.Base(to);
    var route = Languages.Route(From, To) ?? throw new InvalidOperationException(
      $"There's no translation model from {Languages.Name(From)} to {Languages.Name(To)}.");
    foreach (var pair in route) chain.Add(new OpusModel(ModelStore.OpusFolder(pair), threads));
  }

  /// Each sentence with its translation.
  public List<(string Original, string Translation)> TranslateAll(string text) =>
    OpusModel.SplitSentences(text).Select(s => (s, TranslateSentence(s))).ToList();

  public string Translate(string text) => string.Join(" ", TranslateAll(text).Select(x => x.Translation));

  public string TranslateSentence(string sentence) {
    if (cache.TryGetValue(sentence, out var known)) return known;
    string result = Run(sentence);
    if (IsJunk(sentence, result)) {
      // Seen live: "Der Termin mit … ist am 1." (cut mid-date) became "==References====External links==".
      var trimmed = Regex.Replace(sentence, @"\s*\d+\.?\s*$", "").TrimEnd(',', ' ');
      var retry = trimmed.Length > 0 && trimmed != sentence ? Run(trimmed) : "";
      Log.Write("translation looked like junk; " + (retry.Length > 0 && !IsJunk(trimmed, retry) ? "retried without the trailing number" : "kept the original"));
      result = retry.Length > 0 && !IsJunk(trimmed, retry) ? retry : sentence;
    }
    result = KeepNumbers(sentence, result);
    if (cache.Count > 5000) cache.Clear();
    cache[sentence] = result;
    return result;
  }

  string Run(string text) {
    lock (run) {  // one model run at a time: keeps memory and CPU use predictable
      foreach (var model in chain) text = model.TranslateOne(text);
    }
    return text;
  }

  /// Wikipedia-style markup or links the original doesn't have: the model made something up.
  static readonly string[] JunkMarkers = { "==", "[[", "]]", "External links", "References", "http", "www.", "©" };
  static bool IsJunk(string original, string translation) =>
    JunkMarkers.Any(m => translation.Contains(m, StringComparison.OrdinalIgnoreCase) && !original.Contains(m, StringComparison.OrdinalIgnoreCase));

  /// OPUS-MT sometimes leaves an amount out ("Die Rechnung über 45.000 Euro … ist noch nicht bezahlt"
  /// became "The invoice … is not yet paid" in the tests). When a number of the original is missing
  /// from the translation, it is added in brackets rather than silently lost.
  static string KeepNumbers(string original, string translation) {
    static string Digits(string s) => Regex.Replace(s, @"[.,:\s]", "");
    var have = Regex.Matches(translation, @"\d[\d.,:]*\d|\d").Select(m => Digits(m.Value)).ToList();
    var missing = new List<string>();
    // Times ("14 Uhr 30", "14.30 Uhr") may come back as "14:30" or "2.30 p.m.".
    original = Regex.Replace(original, @"\b(\d{1,2})(?:[.:](\d{2}))? Uhr(?: (\d{1,2}))?", m => {
      int h = int.Parse(m.Groups[1].Value);
      int min = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0;
      var forms = new[] { $"{h}{min:00}", $"{(h > 12 ? h - 12 : h)}{min:00}" }.ToList();
      if (min == 0) forms.AddRange(new[] { $"{h}", $"{(h > 12 ? h - 12 : h)}" });
      int i = have.FindIndex(forms.Contains);
      if (i >= 0) have.RemoveAt(i); else missing.Add(m.Value);
      return " ";
    });
    foreach (Match m in Regex.Matches(original, @"\d[\d.,:]*\d|\d")) {
      var d = Digits(m.Value);
      if (d.Length == 0) continue;
      int i = have.FindIndex(h => h == d || h.Contains(d) || d.Contains(h) && h.Length >= 2);
      if (i >= 0) have.RemoveAt(i); else missing.Add(m.Value.TrimEnd('.', ','));
    }
    return missing.Count == 0 ? translation : $"{translation} [{string.Join(", ", missing)}]";
  }

  public void Dispose() {
    foreach (var m in chain) m.Dispose();
  }
}
