using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kurzschrift;

/// Talks to the Anthropic Messages API or OpenAI Chat Completions over plain HTTPS.
/// Safe to use from any thread, also for several requests at once.
public sealed class MinutesClient {
  /// One connection pool for the whole app, like URLSession.shared; timeouts are set per request.
  static readonly HttpClient Http = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }) {
    Timeout = Timeout.InfiniteTimeSpan,
  };
  static readonly JsonSerializerOptions BodyJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
  static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement.Clone();
  static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(3);

  public MinutesProvider Provider { get; }
  public string Model { get; }
  readonly string apiKey;
  /// The account turned the fallback beta down once; later requests go without it.
  volatile bool fallbacksRejected;

  public MinutesClient(MinutesProvider provider, string model, string apiKey) {
    Provider = provider;
    Model = model;
    this.apiKey = apiKey;
  }

  /// `transcriptChunks` only ever grows at the end, so the provider can cache everything but the
  /// newest chunk; the previous minutes and the request come after the cached part.
  public Task<(string Text, MinutesUsage Usage)> MinutesAsync(
    IReadOnlyList<string> transcriptChunks, string? previousMinutes, IReadOnlyList<MinutesEdit> edits,
    MinutesConfig config, bool final, bool longCache, CancellationToken cancellationToken = default) {
    var system = SystemPrompt(config.Prompt, config.Language, config.Context);
    var tail = RequestText(previousMinutes, edits, final);
    return SendWithRetryAsync(system, transcriptChunks, tail, final ? "medium" : "low", longCache, cancellationToken);
  }

  /// Answers a question about the meeting from its transcript (the "Ask" tab). `context` is what
  /// you entered in the Context tab.
  public Task<(string Text, MinutesUsage Usage)> AnswerAsync(
    string question, string transcript, string language, string context = "",
    CancellationToken cancellationToken = default) {
    var system =
      "You answer questions about a meeting from its transcript, which comes from automatic speech "
      + $"recognition, so expect misheard words and names. Answer in {language}, briefly, and cite the "
      + "times you rely on as [hh:mm:ss]. If the transcript doesn't contain the answer, say so instead of "
      + "guessing.";
    if (context.Length > 0) system += "\n\n" + context;
    return SendWithRetryAsync(system, [transcript], "Question: " + question, "low", false, cancellationToken);
  }

  /// One retry, 3 seconds later, after a rate limit, an overload, a server error or a network error.
  async Task<(string Text, MinutesUsage Usage)> SendWithRetryAsync(
    string system, IReadOnlyList<string> chunks, string tail, string effort, bool longCache, CancellationToken ct) {
    try {
      return await SendAsync(system, chunks, tail, effort, longCache, ct);
    } catch (MinutesException error) when (error.IsRetryable) {
      await Task.Delay(RetryDelay, ct);
      return await SendAsync(system, chunks, tail, effort, longCache, ct);
    }
  }

  /// Validates the key and returns the models it can use.
  public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default) {
    var url = Endpoint("v1/models");
    if (Provider == MinutesProvider.Anthropic) url = new Uri(url.AbsoluteUri + "?limit=100");
    using var request = new HttpRequestMessage(HttpMethod.Get, url);
    AddAuthorization(request);
    var json = await PerformAsync(request, TimeSpan.FromSeconds(20), cancellationToken);
    var ids = Items(Field(json, "data")).Select(model => Text(Field(model, "id"))).OfType<string>().ToList();
    if (Provider == MinutesProvider.Anthropic) return ids;
    string[] excluded =
      ["audio", "realtime", "tts", "transcribe", "image", "embedding", "search", "moderation", "whisper", "dall-e"];
    return ids
      .Where(id => (id.StartsWith("gpt-", StringComparison.Ordinal) || id.StartsWith('o'))
        && !excluded.Any(word => id.Contains(word, StringComparison.Ordinal)))
      .Order(StringComparer.Ordinal)
      .ToList();
  }

  // Requests

  Task<(string Text, MinutesUsage Usage)> SendAsync(
    string system, IReadOnlyList<string> chunks, string tail, string effort, bool longCache, CancellationToken ct) =>
    Provider == MinutesProvider.Anthropic
      ? SendAnthropicAsync(system, chunks, tail, effort, longCache, ct)
      : SendOpenAIAsync(system, chunks, tail, ct);

  async Task<(string Text, MinutesUsage Usage)> SendAnthropicAsync(
    string system, IReadOnlyList<string> chunks, string tail, string effort, bool longCache, CancellationToken ct) {
    var content = new JsonArray { TextBlock(TranscriptIntro) };
    for (var i = 0; i < chunks.Count; i++) {
      var block = TextBlock(chunks[i]);
      if (i == chunks.Count - 1) {
        block["cache_control"] = longCache
          ? new JsonObject { ["type"] = "ephemeral", ["ttl"] = "1h" }
          : new JsonObject { ["type"] = "ephemeral" };
      }
      content.Add(block);
    }
    content.Add(TextBlock(tail));

    var body = new JsonObject {
      ["model"] = Model,
      ["max_tokens"] = 16000,
      ["system"] = system,
      ["messages"] = new JsonArray { new JsonObject { ["role"] = "user", ["content"] = content } },
    };
    if (SupportsEffort(Model)) body["output_config"] = new JsonObject { ["effort"] = effort };
    var useFallbacks = SupportsFallbacks(Model) && !fallbacksRejected;
    if (useFallbacks) body["fallbacks"] = "default";

    using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint("v1/messages")) { Content = JsonBody(body) };
    AddAuthorization(request);
    if (useFallbacks) request.Headers.TryAddWithoutValidation("anthropic-beta", "server-side-fallback-2026-07-01");

    JsonElement json;
    try {
      json = await PerformAsync(request, TimeSpan.FromSeconds(300), ct);
    } catch (MinutesException error) when (
      error.Kind == MinutesErrorKind.Http && error.Status == 400 && useFallbacks
      && (error.Detail ?? "").Contains("fallback", StringComparison.Ordinal)) {
      // The account doesn't have the fallback beta: carry on without it.
      fallbacksRejected = true;
      return await SendAnthropicAsync(system, chunks, tail, effort, longCache, ct);
    }

    if (Text(Field(json, "stop_reason")) == "refusal") {
      throw MinutesException.Refused(Text(Field(Field(json, "stop_details"), "category")));
    }
    var text = string.Concat(
      Items(Field(json, "content"))
        .Where(block => Text(Field(block, "type")) == "text")
        .Select(block => Text(Field(block, "text")))
        .OfType<string>())
      .Trim();
    if (text.Length == 0) throw MinutesException.EmptyResponse();
    var u = Field(json, "usage");
    var usage = new MinutesUsage(
      Input: Int(Field(u, "input_tokens")), CacheRead: Int(Field(u, "cache_read_input_tokens")),
      CacheWrite: Int(Field(u, "cache_creation_input_tokens")), Output: Int(Field(u, "output_tokens")));
    return (text, usage);
  }

  async Task<(string Text, MinutesUsage Usage)> SendOpenAIAsync(
    string system, IReadOnlyList<string> chunks, string tail, CancellationToken ct) {
    // OpenAI caches long prompt prefixes automatically; keeping the transcript first preserves that.
    var user = string.Join("\n\n", [TranscriptIntro, .. chunks, tail]);
    var body = new JsonObject {
      ["model"] = Model,
      ["messages"] = new JsonArray {
        new JsonObject { ["role"] = "system", ["content"] = system },
        new JsonObject { ["role"] = "user", ["content"] = user },
      },
    };
    using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint("v1/chat/completions")) { Content = JsonBody(body) };
    AddAuthorization(request);

    var json = await PerformAsync(request, TimeSpan.FromSeconds(300), ct);
    var message = Field(Items(Field(json, "choices")).FirstOrDefault(), "message");
    if (Text(Field(message, "refusal")) is { Length: > 0 }) throw MinutesException.Refused(null);
    var text = (Text(Field(message, "content")) ?? "").Trim();
    if (text.Length == 0) throw MinutesException.EmptyResponse();
    var u = Field(json, "usage");
    var cached = Int(Field(Field(u, "prompt_tokens_details"), "cached_tokens"));
    var usage = new MinutesUsage(
      Input: Int(Field(u, "prompt_tokens")) - cached, CacheRead: cached, CacheWrite: 0,
      Output: Int(Field(u, "completion_tokens")));
    return (text, usage);
  }

  /// The base URL with `path` appended, like URL.appendingPathComponent.
  Uri Endpoint(string path) {
    var builder = new UriBuilder(Provider.BaseUrl());
    builder.Path = builder.Path.TrimEnd('/') + "/" + path;
    return builder.Uri;
  }

  void AddAuthorization(HttpRequestMessage request) {
    if (Provider == MinutesProvider.Anthropic) {
      request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
      request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
    } else {
      request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey);
    }
  }

  static ByteArrayContent JsonBody(JsonObject body) {
    var content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(body, BodyJson));
    content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
    return content;
  }

  static JsonObject TextBlock(string text) => new() { ["type"] = "text", ["text"] = text };

  async Task<JsonElement> PerformAsync(HttpRequestMessage request, TimeSpan timeout, CancellationToken ct) {
    byte[] data;
    int status;
    using (var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(ct)) {
      timeoutSource.CancelAfter(timeout);
      try {
        using var response = await Http.SendAsync(request, timeoutSource.Token);
        status = (int)response.StatusCode;
        data = await response.Content.ReadAsByteArrayAsync(timeoutSource.Token);
      } catch (OperationCanceledException) when (!ct.IsCancellationRequested) {
        throw MinutesException.Network("The request timed out.");
      } catch (Exception error) when (error is not OperationCanceledException) {
        throw MinutesException.Network(error.Message);
      }
    }
    var json = ParseObject(data);
    if (Provider.IsOverridden()) Log.Write($"testing override: HTTP {status} from {request.RequestUri?.Host ?? "?"}");
    if (status is < 200 or >= 300) {
      var message = Text(Field(Field(json, "error"), "message")) ?? Utf8Prefix(data, 300);
      Log.Write($"minutes HTTP {status} from {Provider.RawValue()}");
      // Some providers quote the key in their error message; it never goes further than this.
      if (apiKey.Length > 0) message = message.Replace(apiKey, "<key>", StringComparison.Ordinal);
      throw MinutesException.Http(status, message);
    }
    return json;
  }

  // JSON responses: missing or mistyped fields read as empty, as on the Mac.

  static JsonElement ParseObject(byte[] data) {
    try {
      using var document = JsonDocument.Parse(data);
      return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : EmptyObject;
    } catch (JsonException) {
      return EmptyObject;
    }
  }

  static JsonElement? Field(JsonElement? element, string name) =>
    element is { ValueKind: JsonValueKind.Object } e && e.TryGetProperty(name, out var value) ? value : null;

  static string? Text(JsonElement? element) =>
    element is { ValueKind: JsonValueKind.String } e ? e.GetString() : null;

  static int Int(JsonElement? element) =>
    element is { ValueKind: JsonValueKind.Number } e && e.TryGetInt32(out var value) ? value : 0;

  static IEnumerable<JsonElement> Items(JsonElement? element) =>
    element is { ValueKind: JsonValueKind.Array } e ? e.EnumerateArray() : [];

  /// The first bytes of a response that isn't JSON; empty if they cut a character in half.
  static string Utf8Prefix(byte[] data, int count) {
    try {
      return new UTF8Encoding(false, true).GetString(data, 0, Math.Min(count, data.Length));
    } catch (DecoderFallbackException) {
      return "";
    }
  }

  // Model capabilities

  static bool SupportsEffort(string model) =>
    new[] {
      "claude-opus-5", "claude-sonnet-5", "claude-haiku-5", "claude-fable-5", "claude-mythos-5",
      "claude-opus-4-8", "claude-opus-4-7", "claude-opus-4-6", "claude-sonnet-4-6",
    }.Any(prefix => model.StartsWith(prefix, StringComparison.Ordinal));

  /// Server-side refusal fallback (`fallbacks: "default"`) is offered on these models.
  static bool SupportsFallbacks(string model) =>
    model is "claude-opus-5-5" or "claude-opus-5" or "claude-fable-5-1" or "claude-sonnet-5-5";

  // Prompt

  public const string TranscriptIntro =
    "Meeting transcript so far. Each line is [hh:mm:ss from the start] speaker: text. It comes from "
    + "automatic speech recognition, so expect misheard words, names and missing punctuation.";

  /// Shown and editable in Settings → Minutes. The same for live and final minutes, so it stays
  /// cacheable; whether the meeting is still running goes in the request at the end.
  public const string DefaultPrompt =
    "You keep the minutes of a meeting. You get its transcript, possibly while the meeting is still "
    + "running, and the current version of the minutes, if there is one. Return the complete minutes in "
    + "Markdown.\n"
    + "\n"
    + "Use these sections, in this order, and leave out any that would be empty:\n"
    + "### Summary\n"
    + "Two to four sentences: what the meeting is about and where it stands.\n"
    + "### Decisions\n"
    + "### Action items\n"
    + "One line each: \"- [ ] Owner: task (due date if one was mentioned)\". Use \"?\" when no owner was named.\n"
    + "### Open questions\n"
    + "### Notes\n"
    + "Key points, grouped under short bold topic names.\n"
    + "\n"
    + "Keep items that are still accurate, revise the ones the conversation has changed, and never "
    + "invent names, numbers, dates or commitments that aren't in the transcript. Speaker labels such "
    + "as \"Speaker 2\" or \"Others\" are placeholders; use real names only when people are addressed or "
    + "introduced by name. Return only the minutes, with no preamble.";

  public static string SystemPrompt(string prompt, string language, string context) {
    var system = prompt.Trim();
    system += $"\n\nWrite the minutes in {language}, whatever language the meeting is held in.";
    context = context.Trim();
    if (context.Length > 0) system += "\n\n" + context;
    return system;
  }

  /// After the cached transcript: the current minutes, everything you changed in them during the
  /// meeting, and whether the meeting is still running.
  public static string RequestText(string? previousMinutes, IReadOnlyList<MinutesEdit> edits, bool final) {
    var parts = new List<string>();
    if (previousMinutes != null) {
      parts.Add(
        (edits.Count == 0 ? "Current minutes:" : "Current minutes, including the user's own edits:") + "\n\n"
        + previousMinutes);
    } else {
      parts.Add("There are no minutes yet.");
    }
    if (edits.Count > 0) {
      var lines = new List<string> { "The user has edited the minutes during this meeting (oldest first):" };
      for (var index = 0; index < edits.Count; index++) {
        lines.Add($"Edit {index + 1}:");
        lines.AddRange(edits[index].Removed.Select(line => "Removed: " + line));
        lines.AddRange(edits[index].Added.Select(line => "Added: " + line));
      }
      lines.Add(
        "The user's edits take precedence over your own earlier versions. Work what they added into the "
        + "minutes, in the section where it belongs: keep its facts and intent, but tidy the wording to "
        + "match the rest (complete shorthand, fix typos and misheard names) and fill it in with details "
        + "from the transcript. Correct it only where the transcript clearly says otherwise. Apply their "
        + "corrections throughout (a corrected name or term applies everywhere), and don't bring back "
        + "what they removed unless the conversation returns to it later.");
      parts.Add(string.Join("\n", lines));
    }
    parts.Add(
      final
        ? "The meeting has ended and the transcript above is complete, with speakers identified. Write the final minutes."
        : "The meeting is still going on, so don't treat the last topic as a conclusion. Update the minutes to cover the transcript so far.");
    return string.Join("\n\n", parts);
  }
}
