using System.Text.RegularExpressions;

namespace Kurzschrift;

/// The languages Parakeet v3 recognizes, the OPUS-MT translation models that exist for them, and a
/// small word-list language guess (to leave text alone that's already in the target language).
public static class Languages {
  /// Parakeet TDT 0.6B v3: 25 European languages, recognized without being told which.
  public static readonly (string Code, string Name)[] Spoken = {
    ("bg", "Bulgarian"), ("hr", "Croatian"), ("cs", "Czech"), ("da", "Danish"), ("nl", "Dutch"),
    ("en", "English"), ("et", "Estonian"), ("fi", "Finnish"), ("fr", "French"), ("de", "German"),
    ("el", "Greek"), ("hu", "Hungarian"), ("it", "Italian"), ("lv", "Latvian"), ("lt", "Lithuanian"),
    ("mt", "Maltese"), ("pl", "Polish"), ("pt", "Portuguese"), ("ro", "Romanian"), ("sk", "Slovak"),
    ("sl", "Slovenian"), ("es", "Spanish"), ("sv", "Swedish"), ("ru", "Russian"), ("uk", "Ukrainian"),
  };

  /// OPUS-MT pairs with a quantized ONNX export (Xenova) and SentencePiece files (Helsinki-NLP),
  /// checked on 2026-11-14.
  static readonly HashSet<string> Pairs = new() {
    "cs-en", "en-cs", "da-en", "en-da", "nl-en", "en-nl", "et-en", "fi-en", "en-fi", "fr-en", "en-fr",
    "de-en", "en-de", "hu-en", "en-hu", "it-en", "en-it", "pl-en", "en-ro", "es-en", "en-es", "sv-en",
    "en-sv", "ru-en", "en-ru", "uk-en", "en-uk",
  };

  public static string Name(string? code) {
    if (string.IsNullOrEmpty(code)) return "";
    var c = Base(code);
    foreach (var (k, n) in Spoken) if (k == c) return n;
    return code;
  }

  /// "de-AT" → "de".
  public static string Base(string code) => code.Split('-', '_')[0].ToLowerInvariant();

  public static bool Same(string? a, string? b) =>
    !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) && Base(a) == Base(b);

  /// The models to chain for from → to: one direct pair, or two through English; null if there's no way.
  public static List<string>? Route(string from, string to) {
    from = Base(from);
    to = Base(to);
    if (from == to) return new();
    if (Pairs.Contains($"{from}-{to}")) return new() { $"{from}-{to}" };
    if (from != "en" && to != "en" && Pairs.Contains($"{from}-en") && Pairs.Contains($"en-{to}"))
      return new() { $"{from}-en", $"en-{to}" };
    return null;
  }

  /// The languages a meeting can be translated into from `spoken`.
  public static IEnumerable<(string Code, string Name)> Targets(string spoken) =>
    Spoken.Where(l => !Same(l.Code, spoken) && Route(spoken, l.Code) != null);

  static readonly Dictionary<string, HashSet<string>> Stopwords = new() {
    ["en"] = Set("the and is are was were to of in that it you we they this for with not have has be on at but what"),
    ["de"] = Set("der die das und ist nicht ich wir sie es ein eine zu mit auf für den dem auch dass aber was sind haben wird"),
    ["fr"] = Set("le la les et est pas je nous vous il elle un une de des du que qui pour avec dans sur mais ce"),
    ["es"] = Set("el la los las y es no yo nosotros que de del un una para con en por pero lo se"),
    ["it"] = Set("il la le e è non io noi che di del un una per con in ma lo si sono"),
    ["nl"] = Set("de het en is niet ik wij we een van dat die voor met op maar zijn heeft wordt"),
  };

  static HashSet<string> Set(string words) => new(words.Split(' '));

  /// A best guess from common short words; null when the text is too short or unclear.
  public static string? Guess(string text) {
    var words = Regex.Matches(text.ToLowerInvariant(), @"\p{L}+").Select(m => m.Value).ToList();
    if (words.Count < 3) return null;
    var scores = Stopwords.ToDictionary(kv => kv.Key, kv => words.Count(w => kv.Value.Contains(w)));
    var best = scores.OrderByDescending(kv => kv.Value).First();
    var second = scores.Where(kv => kv.Key != best.Key).Max(kv => kv.Value);
    return best.Value >= 2 && best.Value >= 2 * second ? best.Key : null;
  }
}

/// The recognizer sometimes starts a result with the punctuation that ends the previous one
/// (". Gut, dann…") and puts spaces before punctuation ("allen .").
public static class TextCleanup {
  static readonly HashSet<char> Punctuation = new() { '.', ',', '?', '!', ';', ':', '…' };

  /// (".", "Gut, dann") for ". Gut, dann".
  public static (string Lead, string Text) SplitLeadingPunctuation(string text) {
    var trimmed = text.TrimStart();
    int n = 0;
    while (n < trimmed.Length && (Punctuation.Contains(trimmed[n]) || char.IsWhiteSpace(trimmed[n]))) n++;
    var lead = new string(trimmed[..n].Where(c => !char.IsWhiteSpace(c)).ToArray());
    return (lead, trimmed[n..]);
  }

  public static bool EndsWithPunctuation(string text) {
    var t = text.TrimEnd();
    return t.Length > 0 && Punctuation.Contains(t[^1]);
  }

  public static string Tidy(string text) => Regex.Replace(text, @"\s+([.,?!;:…])", "$1").Trim();
}
