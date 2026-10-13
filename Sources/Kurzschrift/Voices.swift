import Foundation

/// Voices you have named, so the same people get their names automatically in later meetings.
/// Each is a speaker embedding from the diarizer; stored on this Mac only.
@MainActor
enum VoiceLibrary {
  struct Voice: Codable {
    var name: String
    var embedding: [Float]
    var meetings: Int
  }

  /// Same person across two real meetings measured 0.92 to 0.97 cosine similarity; different
  /// people at most 0.32 (AMI ES2004a/b). 0.7 sits well between the two.
  static let threshold: Float = 0.7

  static let url = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
    .appendingPathComponent("Kurzschrift/voices.json")

  static func all() -> [Voice] {
    (try? JSONDecoder().decode([Voice].self, from: Data(contentsOf: url))) ?? []
  }

  private static func save(_ voices: [Voice]) {
    do {
      try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
      try JSONEncoder().encode(voices).write(to: url, options: .atomic)
      try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: url.path)
    } catch {
      Log.write("saving voices failed: \(error)")
    }
  }

  /// Adds or refines a named voice (running average of its embeddings).
  static func remember(_ name: String, embedding: [Float]) {
    var voices = all()
    let new = normalized(embedding)
    if let i = voices.firstIndex(where: { $0.name == name }) {
      let n = Float(voices[i].meetings)
      voices[i].embedding = normalized(zip(voices[i].embedding, new).map { ($0 * n + $1) / (n + 1) })
      voices[i].meetings += 1
    } else {
      voices.append(Voice(name: name, embedding: new, meetings: 1))
    }
    save(voices)
    Log.write("voice remembered (\(voices.count) known)")
  }

  static func forget(_ name: String) {
    save(all().filter { $0.name != name })
  }

  /// Names for this meeting's speakers (by diarizer id) that match known voices, each name used once.
  static func match(_ speakers: [String: [Float]]) -> [String: String] {
    let voices = all()
    var candidates: [(speaker: String, name: String, score: Float)] = []
    for (speaker, embedding) in speakers {
      let e = normalized(embedding)
      for voice in voices {
        let score = zip(e, voice.embedding).reduce(0) { $0 + $1.0 * $1.1 }
        if score >= threshold { candidates.append((speaker, voice.name, score)) }
      }
    }
    var result: [String: String] = [:]
    for c in candidates.sorted(by: { $0.score > $1.score })
    where result[c.speaker] == nil && !result.values.contains(c.name) {
      result[c.speaker] = c.name
    }
    return result
  }

  private static func normalized(_ v: [Float]) -> [Float] {
    let norm = sqrt(v.reduce(0) { $0 + $1 * $1 })
    return norm > 0 ? v.map { $0 / norm } : v
  }
}
