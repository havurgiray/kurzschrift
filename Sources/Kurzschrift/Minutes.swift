import Foundation

// Live meeting minutes written by an LLM (Anthropic or OpenAI) from the growing transcript.
// This is the only part of Kurzschrift that sends anything off the Mac, and only when switched on.

enum MinutesProvider: String, CaseIterable, Identifiable {
  case anthropic
  case openai

  var id: String { rawValue }

  var displayName: String {
    switch self {
    case .anthropic: "Anthropic (Claude)"
    case .openai: "OpenAI"
    }
  }

  var defaultModel: String {
    switch self {
    case .anthropic: "claude-opus-5-5"
    case .openai: ""  // picked from the account's model list ("Test key")
    }
  }

  var keysPage: URL {
    switch self {
    case .anthropic: URL(string: "https://console.anthropic.com/settings/keys")!
    case .openai: URL(string: "https://platform.openai.com/api-keys")!
    }
  }

  /// Hidden override (`defaults write local.kurzschrift minutesBaseURL.<provider> …`) for testing
  /// against a local mock server.
  var isOverridden: Bool { UserDefaults.standard.string(forKey: "minutesBaseURL.\(rawValue)") != nil }

  var baseURL: URL {
    if let override = UserDefaults.standard.string(forKey: "minutesBaseURL.\(rawValue)"),
      let url = URL(string: override)
    {
      return url
    }
    switch self {
    case .anthropic: return URL(string: "https://api.anthropic.com")!
    case .openai: return URL(string: "https://api.openai.com")!
    }
  }
}

/// API keys are kept in a file only this user can read, not in the keychain: the app is signed ad
/// hoc, so after every rebuild the keychain would ask for the login password to read the key again.
enum APIKeyStore {
  static let url = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
    .appendingPathComponent("Kurzschrift/api-keys.json")

  static func key(for provider: MinutesProvider) -> String? {
    guard let data = try? Data(contentsOf: url),
      let keys = try? JSONDecoder().decode([String: String].self, from: data),
      let key = keys[provider.rawValue]?.trimmingCharacters(in: .whitespacesAndNewlines), !key.isEmpty
    else { return nil }
    return key
  }

  static func setKey(_ key: String, for provider: MinutesProvider) {
    var keys = (try? JSONDecoder().decode([String: String].self, from: Data(contentsOf: url))) ?? [:]
    let trimmed = key.trimmingCharacters(in: .whitespacesAndNewlines)
    keys[provider.rawValue] = trimmed.isEmpty ? nil : trimmed
    do {
      try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
      try JSONEncoder().encode(keys).write(to: url, options: .atomic)
      try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: url.path)
    } catch {
      Log.write("saving API key failed: \(error)")
    }
  }
}

struct MinutesConfig {
  let provider: MinutesProvider
  let model: String
  let apiKey: String
  let interval: TimeInterval
  /// Language the minutes are written in, e.g. "English".
  let language: String
  /// The prompt from Settings → Minutes (the default unless edited).
  let prompt: String
  /// Context the app adds below the prompt: vocabulary, calendar attendees.
  let context: String

  func adding(context extra: String) -> MinutesConfig {
    MinutesConfig(
      provider: provider, model: model, apiKey: apiKey, interval: interval, language: language, prompt: prompt,
      context: context.isEmpty ? extra : context + "\n" + extra)
  }
}

struct MinutesUsage: Equatable {
  var input = 0
  var cacheRead = 0
  var cacheWrite = 0
  var output = 0

  static func + (a: MinutesUsage, b: MinutesUsage) -> MinutesUsage {
    MinutesUsage(
      input: a.input + b.input, cacheRead: a.cacheRead + b.cacheRead, cacheWrite: a.cacheWrite + b.cacheWrite,
      output: a.output + b.output)
  }

  /// Rough cost in USD for the Claude models whose list prices are known here; nil otherwise.
  func estimatedCost(model: String, longCache: Bool) -> Double? {
    let prices: [String: (input: Double, output: Double, cacheRead: Double)] = [
      "claude-opus-5-5": (4, 20, 0.20),
      "claude-sonnet-5-5": (2, 10, 0.20),
      "claude-haiku-5-5": (0.10, 0.50, 0.01),
    ]
    guard let p = prices[model] else { return nil }
    let writeFactor = longCache ? 2.0 : 1.25
    let dollars =
      Double(input) * p.input + Double(cacheWrite) * p.input * writeFactor + Double(cacheRead) * p.cacheRead
      + Double(output) * p.output
    return dollars / 1_000_000
  }
}

enum MinutesError: LocalizedError {
  case noKey
  case http(Int, String)
  case refused(String?)
  case emptyResponse
  case network(String)

  var errorDescription: String? {
    switch self {
    case .noKey: "No API key is set (Settings → Minutes)."
    case .http(401, _): "The API key was rejected. Check it in Settings → Minutes."
    case .http(404, let message): "Model not available to this key: \(message)"
    case .http(429, _): "Rate limited by the provider. Will retry at the next update."
    case .http(529, _), .http(503, _): "The provider is overloaded. Will retry at the next update."
    case .http(let status, let message): "HTTP \(status): \(message)"
    case .refused(let category): "The model declined to write minutes\(category.map { " (\($0))" } ?? "")."
    case .emptyResponse: "The model returned no text."
    case .network(let message): "Network error: \(message)"
    }
  }

  var isRetryable: Bool {
    switch self {
    case .http(let status, _): [408, 429, 500, 502, 503, 504, 529].contains(status)
    case .network: true
    default: false
    }
  }
}

/// Talks to the Anthropic Messages API or OpenAI Chat Completions over plain HTTPS.
final class MinutesClient: @unchecked Sendable {
  let provider: MinutesProvider
  let model: String
  private let apiKey: String
  private var fallbacksRejected = false

  init(provider: MinutesProvider, model: String, apiKey: String) {
    self.provider = provider
    self.model = model
    self.apiKey = apiKey
  }

  /// `transcriptChunks` only ever grows at the end, so the provider can cache everything but the
  /// newest chunk; the previous minutes and the request come after the cached part.
  func minutes(
    transcriptChunks: [String], previousMinutes: String?, edits: [MinutesEdit] = [], config: MinutesConfig,
    final: Bool, longCache: Bool
  ) async throws -> (text: String, usage: MinutesUsage) {
    let system = Self.systemPrompt(prompt: config.prompt, language: config.language, context: config.context)
    let tail = Self.requestText(previousMinutes: previousMinutes, edits: edits, final: final)
    return try await sendWithRetry(
      system: system, chunks: transcriptChunks, tail: tail, effort: final ? "medium" : "low", longCache: longCache)
  }

  /// Answers a question about the meeting from its transcript (the "Ask" tab).
  func answer(question: String, transcript: String, language: String) async throws -> (text: String, usage: MinutesUsage) {
    let system = """
      You answer questions about a meeting from its transcript, which comes from automatic speech \
      recognition, so expect misheard words and names. Answer in \(language), briefly, and cite the \
      times you rely on as [hh:mm:ss]. If the transcript doesn't contain the answer, say so instead of \
      guessing.
      """
    return try await sendWithRetry(
      system: system, chunks: [transcript], tail: "Question: \(question)", effort: "low", longCache: false)
  }

  private func sendWithRetry(system: String, chunks: [String], tail: String, effort: String, longCache: Bool)
    async throws -> (text: String, usage: MinutesUsage)
  {
    do {
      return try await send(system: system, chunks: chunks, tail: tail, effort: effort, longCache: longCache)
    } catch let error as MinutesError where error.isRetryable {
      try await Task.sleep(for: .seconds(3))
      return try await send(system: system, chunks: chunks, tail: tail, effort: effort, longCache: longCache)
    }
  }

  /// Validates the key and returns the models it can use.
  func listModels() async throws -> [String] {
    var request = URLRequest(url: provider.baseURL.appendingPathComponent("v1/models"))
    request.timeoutInterval = 20
    switch provider {
    case .anthropic:
      request.url = URL(string: request.url!.absoluteString + "?limit=100")
      request.setValue(apiKey, forHTTPHeaderField: "x-api-key")
      request.setValue("2023-06-01", forHTTPHeaderField: "anthropic-version")
    case .openai:
      request.setValue("Bearer \(apiKey)", forHTTPHeaderField: "Authorization")
    }
    let json = try await perform(request)
    let ids = ((json["data"] as? [[String: Any]]) ?? []).compactMap { $0["id"] as? String }
    switch provider {
    case .anthropic:
      return ids
    case .openai:
      let excluded = ["audio", "realtime", "tts", "transcribe", "image", "embedding", "search", "moderation", "whisper", "dall-e"]
      return ids.filter { id in
        (id.hasPrefix("gpt-") || id.hasPrefix("o")) && !excluded.contains { id.contains($0) }
      }.sorted()
    }
  }

  // MARK: Requests

  private func send(system: String, chunks: [String], tail: String, effort: String, longCache: Bool) async throws
    -> (text: String, usage: MinutesUsage)
  {
    switch provider {
    case .anthropic: try await sendAnthropic(system: system, chunks: chunks, tail: tail, effort: effort, longCache: longCache)
    case .openai: try await sendOpenAI(system: system, chunks: chunks, tail: tail)
    }
  }

  private func sendAnthropic(system: String, chunks: [String], tail: String, effort: String, longCache: Bool)
    async throws -> (text: String, usage: MinutesUsage)
  {
    var content: [[String: Any]] = [["type": "text", "text": Self.transcriptIntro]]
    for (i, chunk) in chunks.enumerated() {
      var block: [String: Any] = ["type": "text", "text": chunk]
      if i == chunks.count - 1 {
        block["cache_control"] = longCache ? ["type": "ephemeral", "ttl": "1h"] : ["type": "ephemeral"]
      }
      content.append(block)
    }
    content.append(["type": "text", "text": tail])

    var body: [String: Any] = [
      "model": model,
      "max_tokens": 16000,
      "system": system,
      "messages": [["role": "user", "content": content]],
    ]
    if Self.supportsEffort(model) {
      body["output_config"] = ["effort": effort]
    }
    let useFallbacks = Self.supportsFallbacks(model) && !fallbacksRejected
    if useFallbacks { body["fallbacks"] = "default" }

    var request = URLRequest(url: provider.baseURL.appendingPathComponent("v1/messages"))
    request.httpMethod = "POST"
    request.timeoutInterval = 300
    request.setValue("application/json", forHTTPHeaderField: "content-type")
    request.setValue(apiKey, forHTTPHeaderField: "x-api-key")
    request.setValue("2023-06-01", forHTTPHeaderField: "anthropic-version")
    if useFallbacks { request.setValue("server-side-fallback-2026-07-01", forHTTPHeaderField: "anthropic-beta") }
    request.httpBody = try JSONSerialization.data(withJSONObject: body)

    let json: [String: Any]
    do {
      json = try await perform(request)
    } catch MinutesError.http(400, let message) where useFallbacks && message.contains("fallback") {
      // The account doesn't have the fallback beta: carry on without it.
      fallbacksRejected = true
      return try await sendAnthropic(system: system, chunks: chunks, tail: tail, effort: effort, longCache: longCache)
    }

    if json["stop_reason"] as? String == "refusal" {
      throw MinutesError.refused((json["stop_details"] as? [String: Any])?["category"] as? String)
    }
    let text = ((json["content"] as? [[String: Any]]) ?? [])
      .filter { $0["type"] as? String == "text" }
      .compactMap { $0["text"] as? String }
      .joined()
      .trimmingCharacters(in: .whitespacesAndNewlines)
    guard !text.isEmpty else { throw MinutesError.emptyResponse }
    let u = json["usage"] as? [String: Any] ?? [:]
    let usage = MinutesUsage(
      input: u["input_tokens"] as? Int ?? 0, cacheRead: u["cache_read_input_tokens"] as? Int ?? 0,
      cacheWrite: u["cache_creation_input_tokens"] as? Int ?? 0, output: u["output_tokens"] as? Int ?? 0)
    return (text, usage)
  }

  private func sendOpenAI(system: String, chunks: [String], tail: String) async throws
    -> (text: String, usage: MinutesUsage)
  {
    // OpenAI caches long prompt prefixes automatically; keeping the transcript first preserves that.
    let user = ([Self.transcriptIntro] + chunks + [tail]).joined(separator: "\n\n")
    let body: [String: Any] = [
      "model": model,
      "messages": [["role": "system", "content": system], ["role": "user", "content": user]],
    ]
    var request = URLRequest(url: provider.baseURL.appendingPathComponent("v1/chat/completions"))
    request.httpMethod = "POST"
    request.timeoutInterval = 300
    request.setValue("application/json", forHTTPHeaderField: "content-type")
    request.setValue("Bearer \(apiKey)", forHTTPHeaderField: "Authorization")
    request.httpBody = try JSONSerialization.data(withJSONObject: body)

    let json = try await perform(request)
    let message = ((json["choices"] as? [[String: Any]])?.first?["message"] as? [String: Any]) ?? [:]
    if let refusal = message["refusal"] as? String, !refusal.isEmpty { throw MinutesError.refused(nil) }
    let text = (message["content"] as? String ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
    guard !text.isEmpty else { throw MinutesError.emptyResponse }
    let u = json["usage"] as? [String: Any] ?? [:]
    let cached = (u["prompt_tokens_details"] as? [String: Any])?["cached_tokens"] as? Int ?? 0
    let usage = MinutesUsage(
      input: (u["prompt_tokens"] as? Int ?? 0) - cached, cacheRead: cached, cacheWrite: 0,
      output: u["completion_tokens"] as? Int ?? 0)
    return (text, usage)
  }

  private func perform(_ request: URLRequest) async throws -> [String: Any] {
    let data: Data
    let response: URLResponse
    do {
      (data, response) = try await URLSession.shared.data(for: request)
    } catch {
      throw MinutesError.network(error.localizedDescription)
    }
    let status = (response as? HTTPURLResponse)?.statusCode ?? 0
    let json = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any] ?? [:]
    if provider.isOverridden { Log.write("testing override: HTTP \(status) from \(request.url?.host ?? "?")") }
    guard (200..<300).contains(status) else {
      let message = ((json["error"] as? [String: Any])?["message"] as? String)
        ?? String(data: data.prefix(300), encoding: .utf8) ?? ""
      Log.write("minutes HTTP \(status) from \(provider.rawValue)")
      throw MinutesError.http(status, message)
    }
    return json
  }

  // MARK: Model capabilities

  private static func supportsEffort(_ model: String) -> Bool {
    ["claude-opus-5", "claude-sonnet-5", "claude-haiku-5", "claude-fable-5", "claude-mythos-5",
     "claude-opus-4-8", "claude-opus-4-7", "claude-opus-4-6", "claude-sonnet-4-6"]
      .contains { model.hasPrefix($0) }
  }

  /// Server-side refusal fallback (`fallbacks: "default"`) is offered on these models.
  private static func supportsFallbacks(_ model: String) -> Bool {
    ["claude-opus-5-5", "claude-opus-5", "claude-fable-5-1", "claude-sonnet-5-5"].contains(model)
  }

  // MARK: Prompt

  static let transcriptIntro = """
    Meeting transcript so far. Each line is [hh:mm:ss from the start] speaker: text. It comes from \
    automatic speech recognition, so expect misheard words, names and missing punctuation.
    """

  /// Shown and editable in Settings → Minutes. The same for live and final minutes, so it stays
  /// cacheable; whether the meeting is still running goes in the request at the end.
  static let defaultPrompt = """
    You keep the minutes of a meeting. You get its transcript, possibly while the meeting is still \
    running, and the current version of the minutes, if there is one. Return the complete minutes in \
    Markdown.

    Use these sections, in this order, and leave out any that would be empty:
    ### Summary
    Two to four sentences: what the meeting is about and where it stands.
    ### Decisions
    ### Action items
    One line each: "- [ ] Owner: task (due date if one was mentioned)". Use "?" when no owner was named.
    ### Open questions
    ### Notes
    Key points, grouped under short bold topic names.

    Keep items that are still accurate, revise the ones the conversation has changed, and never \
    invent names, numbers, dates or commitments that aren't in the transcript. Speaker labels such \
    as "Speaker 2" or "Others" are placeholders; use real names only when people are addressed or \
    introduced by name. Return only the minutes, with no preamble.
    """

  static func systemPrompt(prompt: String, language: String, context: String) -> String {
    var system = prompt.trimmingCharacters(in: .whitespacesAndNewlines)
    system += "\n\nWrite the minutes in \(language), whatever language the meeting is held in."
    let context = context.trimmingCharacters(in: .whitespacesAndNewlines)
    if !context.isEmpty { system += "\n\n\(context)" }
    return system
  }

  /// After the cached transcript: the current minutes, everything you changed in them during the
  /// meeting, and whether the meeting is still running.
  static func requestText(previousMinutes: String?, edits: [MinutesEdit], final: Bool) -> String {
    var parts: [String] = []
    if let previousMinutes {
      parts.append((edits.isEmpty ? "Current minutes:" : "Current minutes, including the user's own edits:") + "\n\n" + previousMinutes)
    } else {
      parts.append("There are no minutes yet.")
    }
    if !edits.isEmpty {
      var lines = ["The user has edited the minutes during this meeting (oldest first):"]
      for (index, edit) in edits.enumerated() {
        lines.append("Edit \(index + 1):")
        lines += edit.removed.map { "Removed: \($0)" }
        lines += edit.added.map { "Added: \($0)" }
      }
      lines.append("""
        The user's edits take precedence over your own earlier versions. Keep what they added, in their \
        wording, in the section where it belongs; enrich it with details from the transcript, and \
        correct it only where the transcript clearly says otherwise. Apply their corrections \
        throughout (a corrected name or term applies everywhere), and don't bring back what they \
        removed unless the conversation returns to it later.
        """)
      parts.append(lines.joined(separator: "\n"))
    }
    parts.append(
      final
        ? "The meeting has ended and the transcript above is complete, with speakers identified. Write the final minutes."
        : "The meeting is still going on, so don't treat the last topic as a conclusion. Update the minutes to cover the transcript so far.")
    return parts.joined(separator: "\n\n")
  }
}

/// One round of your changes to the minutes: the lines you removed and the lines you added.
/// A changed line shows up as both, which is how the model sees a correction.
struct MinutesEdit: Equatable {
  let removed: [String]
  let added: [String]

  /// nil when nothing changed (moved lines don't count).
  init?(from old: String, to new: String) {
    func lines(_ text: String) -> [String] {
      text.components(separatedBy: "\n").map { $0.trimmingCharacters(in: .whitespaces) }.filter { !$0.isEmpty }
    }
    let difference = lines(new).difference(from: lines(old))
    var removed: [(offset: Int, line: String)] = []
    var added: [String] = []
    for change in difference {
      switch change {
      case .remove(let offset, let line, _): removed.append((offset, line))
      case .insert(_, let line, _): added.append(line)
      }
    }
    // Removals come last-first; list them in reading order.
    let removedLines = removed.sorted { $0.offset < $1.offset }.map(\.line)
    let moved = Set(removedLines).intersection(added)
    self.removed = removedLines.filter { !moved.contains($0) }
    self.added = added.filter { !moved.contains($0) }
    if self.removed.isEmpty && self.added.isEmpty { return nil }
  }
}

/// Collects transcript lines during a meeting and refreshes the minutes on a timer.
///
/// Every update sends the whole transcript so far (cached by the provider) plus the current minutes.
/// You can edit the minutes in between: your version becomes the current minutes, and the list of
/// everything you changed goes with every later update and the final minutes, so the model builds
/// on your text instead of overwriting it, and doesn't bring back what you deleted.
@MainActor
final class MinutesRunner {
  let config: MinutesConfig
  private let client: MinutesClient
  private var chunks: [String] = []
  private var pendingLines: [String] = []
  private var loop: Task<Void, Never>?
  private var inFlight = false
  private(set) var state: MinutesState
  private let publish: (MinutesState) -> Void

  private var edits: [MinutesEdit] = []
  private var editing = false
  /// Bumped by each edit, so a response to a request sent before it isn't applied over it.
  private var revision = 0
  /// An update came due while you were editing.
  private var deferred = false
  /// A response that arrived while you were editing; used if you end up changing nothing.
  private var held: String?

  /// Seconds between updates; follows "Update every" in Settings, even mid-meeting.
  private(set) var interval: TimeInterval

  init(config: MinutesConfig, publish: @escaping (MinutesState) -> Void) {
    self.config = config
    self.publish = publish
    interval = config.interval
    client = MinutesClient(provider: config.provider, model: config.model, apiKey: config.apiKey)
    state = MinutesState(model: config.model, provider: config.provider)
  }

  /// Only the 5-minute cache stays warm between updates that start less than 5 minutes apart.
  private var longCache: Bool { interval > 4 * 60 }

  func start() {
    loop?.cancel()
    schedule()
    loop = Task { [weak self] in
      while !Task.isCancelled {
        guard let interval = self?.interval else { return }
        try? await Task.sleep(for: .seconds(interval))
        if Task.isCancelled { return }
        self?.schedule()
        await self?.update()
      }
    }
  }

  /// "Update every" changed during the meeting: the next update comes one new interval from now.
  func setInterval(_ seconds: TimeInterval) {
    guard seconds != interval else { return }
    interval = seconds
    if loop != nil { start() }
  }

  func stop() {
    loop?.cancel()
    loop = nil
    state.nextUpdate = nil
    publish(state)
  }

  func add(line: String) {
    pendingLines.append(line)
  }

  /// "Update now" in the minutes pane. The next timed update comes a full interval after it.
  func updateNow() {
    if loop != nil { start() }
    Task { await update() }
  }

  // MARK: Your edits

  /// Updates wait until you're done, so nothing changes under your cursor.
  func beginEditing() {
    editing = true
  }

  func finishEditing(text: String) {
    editing = false
    if let edit = MinutesEdit(from: state.text, to: text) {
      edits.append(edit)
      revision += 1
      held = nil
      state.text = text
      state.editsPending = true
      publish(state)
      Log.write("minutes edited: \(edit.removed.count) lines removed, \(edit.added.count) added")
    } else {
      applyHeld()
    }
    runIfDeferred()
  }

  func cancelEditing() {
    editing = false
    applyHeld()
    runIfDeferred()
  }

  private func applyHeld() {
    guard let held else { return }
    self.held = nil
    state.text = held
    state.updatedAt = Date()
    publish(state)
  }

  private func runIfDeferred() {
    guard deferred else { return }
    deferred = false
    Task { await update() }
  }

  // MARK: Updating

  private func schedule() {
    state.nextUpdate = Date().addingTimeInterval(interval)
    publish(state)
  }

  private func update() async {
    if editing {
      deferred = true
      return
    }
    guard !inFlight else { return }
    if !pendingLines.isEmpty {
      let newText = pendingLines.joined(separator: "\n")
      // Wait for a little substance before the first update, unless you've already written something.
      if chunks.isEmpty, newText.count < 150, !state.editsPending { return }
      chunks.append(newText)
      pendingLines = []
    } else if !state.editsPending {
      return
    }
    guard !chunks.isEmpty else { return }

    inFlight = true
    state.isUpdating = true
    publish(state)
    let sentRevision = revision
    var rerun = false
    do {
      let result = try await client.minutes(
        transcriptChunks: chunks, previousMinutes: state.text.isEmpty ? nil : state.text, edits: edits,
        config: config, final: false, longCache: longCache)
      state.usage = state.usage + result.usage
      state.cost = state.usage.estimatedCost(model: config.model, longCache: longCache)
      state.error = nil
      Log.write("minutes updated: \(chunks.count) chunks, \(edits.count) edits, usage in=\(result.usage.input) cached=\(result.usage.cacheRead) written=\(result.usage.cacheWrite) out=\(result.usage.output)")
      if revision != sentRevision {
        // You edited while this ran; redo it from your version.
        rerun = true
      } else if editing {
        held = result.text
      } else {
        state.text = result.text
        state.editsPending = false
        state.updatedAt = Date()
      }
    } catch {
      state.error = error.localizedDescription
      Log.write("minutes update failed: \(error)")
    }
    inFlight = false
    state.isUpdating = false
    publish(state)
    if rerun { await update() }
  }

  /// Final minutes from the finished transcript (with speaker names), using the live minutes as a
  /// draft and keeping your edits.
  func finalize(transcript: String) async -> String? {
    stop()
    guard !transcript.isEmpty else { return nil }
    state.isUpdating = true
    publish(state)
    defer {
      state.isUpdating = false
      publish(state)
    }
    do {
      let result = try await client.minutes(
        transcriptChunks: [transcript], previousMinutes: state.text.isEmpty ? nil : state.text, edits: edits,
        config: config, final: true, longCache: false)
      state.text = result.text
      state.editsPending = false
      state.updatedAt = Date()
      state.error = nil
      state.usage = state.usage + result.usage
      state.cost = state.usage.estimatedCost(model: config.model, longCache: longCache)
      Log.write("final minutes written, \(edits.count) edits, usage in=\(result.usage.input) out=\(result.usage.output)")
      return result.text
    } catch {
      state.error = "Final minutes failed: \(error.localizedDescription)"
      Log.write("final minutes failed: \(error)")
      return nil
    }
  }
}

struct MinutesState: Equatable {
  var text = ""
  /// false when live minutes are off: the pane then holds only your own notes.
  var usesAI = true
  /// You changed the minutes and no update has worked the changes in yet.
  var editsPending = false
  var updatedAt: Date?
  var nextUpdate: Date?
  var isUpdating = false
  var error: String?
  var usage = MinutesUsage()
  /// Rough USD cost so far (known Claude models only).
  var cost: Double?
  var model: String
  var provider: MinutesProvider
}
