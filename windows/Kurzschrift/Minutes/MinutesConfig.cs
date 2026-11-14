namespace Kurzschrift;

/// What a meeting's minutes are written with, frozen when the meeting starts.
/// Language: what the minutes are written in, e.g. "English".
/// Prompt: the prompt from Settings → Minutes (the default unless edited).
/// Context: what the app adds below the prompt: the vocabulary from Settings.
public sealed record MinutesConfig(
  MinutesProvider Provider,
  string Model,
  string ApiKey,
  TimeSpan Interval,
  string Language,
  string Prompt,
  string Context) {

  public MinutesConfig Adding(string context) =>
    this with { Context = Context.Length == 0 ? context : Context + "\n" + context };

  /// Settings → Minutes as a config, like AppSettings.minutesConfig on the Mac: null without an API
  /// key or a model. Whether minutes are on for the meeting's mode is the caller's check (skipped
  /// when they're switched on by hand). `language` is the English name, e.g. "German"; `prompt` is
  /// the setting as stored ("" for the default).
  public static MinutesConfig? FromSettings(
    MinutesProvider provider, string model, int intervalMinutes, string language, string prompt,
    IEnumerable<string> vocabularyTerms) {
    var key = ApiKeyStore.Key(provider);
    if (key == null || model.Length == 0) return null;
    var terms = vocabularyTerms.ToList();
    var context = terms.Count == 0
      ? "" : "Names and terms that may come up (the transcript may misspell them): " + string.Join(", ", terms);
    return new MinutesConfig(
      provider, model, key, TimeSpan.FromSeconds(Math.Max(1, intervalMinutes) * 60), language,
      prompt.Trim().Length == 0 ? MinutesClient.DefaultPrompt : prompt, context);
  }

  /// Why minutes that are on can't run, like AppSettings.minutesProblem; null when they're off or ready.
  public static string? Problem(bool enabled, MinutesProvider provider, string model) {
    if (!enabled) return null;
    if (ApiKeyStore.Key(provider) == null) {
      return $"Live minutes are on, but no {provider.DisplayName()} API key is set (Settings → Minutes).";
    }
    if (model.Length == 0) return "Live minutes are on, but no model is chosen (Settings → Minutes).";
    return null;
  }

  /// Leaves the key out, so a config can't put it in a log by accident.
  public override string ToString() =>
    $"MinutesConfig {{ Provider = {Provider}, Model = {Model}, Interval = {Interval}, Language = {Language} }}";
}
