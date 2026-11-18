import Foundation

// What is left when the app dies mid-meeting (a crash, a force-quit, a power cut), and getting it
// back: the recording folder holds the WAVs, whose headers were never finished, and a small
// `meeting.json` saying which draft transcript they belong to.

/// A WAV whose writer never closed it has no lengths in its header (RIFF size and `data` size
/// are still what they were when the file was created), so nothing opens it although the audio
/// is all there. This writes the lengths the file really has.
enum WAVRepair {
  struct Layout {
    /// Where the `data` chunk's header starts; the audio starts 8 bytes later.
    let dataOffset: UInt64
    /// The audio length the header claims, and what the file actually holds after the header.
    let declared: UInt64
    let available: UInt64
    /// Bytes per sample frame, from the `fmt ` chunk (1 when there is none before `data`).
    let blockAlign: UInt64
  }

  /// Walks the chunks (AVAudioFile puts `JUNK`, `fmt ` and a filler before `data`, so the audio
  /// doesn't start at byte 44). nil when the file isn't a RIFF/WAVE file or has no `data` chunk.
  static func layout(of url: URL) -> Layout? {
    guard let handle = try? FileHandle(forReadingFrom: url) else { return nil }
    defer { try? handle.close() }
    guard let length = try? handle.seekToEnd(), length >= 20 else { return nil }
    func bytes(at offset: UInt64, _ count: Int) -> [UInt8]? {
      guard (try? handle.seek(toOffset: offset)) != nil, let data = try? handle.read(upToCount: count),
        data.count == count
      else { return nil }
      return [UInt8](data)
    }
    func little(_ b: ArraySlice<UInt8>) -> UInt64 {
      b.reversed().reduce(UInt64(0)) { $0 << 8 | UInt64($1) }
    }
    guard let head = bytes(at: 0, 12), Array(head[0..<4]) == Array("RIFF".utf8), Array(head[8..<12]) == Array("WAVE".utf8)
    else { return nil }
    var offset: UInt64 = 12
    var blockAlign: UInt64 = 1
    for _ in 0..<1000 {
      guard offset + 8 <= length, let chunk = bytes(at: offset, 8) else { return nil }
      let id = Array(chunk[0..<4])
      let size = little(chunk[4..<8])
      if id == Array("data".utf8) {
        return Layout(dataOffset: offset, declared: size, available: length - offset - 8, blockAlign: blockAlign)
      }
      if id == Array("fmt ".utf8), size >= 14, let format = bytes(at: offset + 8, 14) {
        blockAlign = max(1, little(format[12..<14]))
      }
      offset += 8 + size + (size & 1)
    }
    return nil
  }

  /// The header says there is no audio although the file has some, or more than the file has.
  static func needsRepair(_ url: URL) -> Bool {
    guard let layout = layout(of: url) else { return false }
    return (layout.declared == 0 && layout.available >= layout.blockAlign) || layout.declared > layout.available
  }

  /// Writes the real lengths into the header, in place (two 4-byte fields; the audio isn't
  /// touched). Returns false when there was nothing to repair.
  @discardableResult
  static func repair(_ url: URL) throws -> Bool {
    guard needsRepair(url), let layout = layout(of: url) else { return false }
    let audio = layout.available - layout.available % layout.blockAlign
    func little(_ value: UInt64) -> Data {
      let v = UInt32(min(value, UInt64(UInt32.max)))
      return Data([UInt8(v & 0xff), UInt8(v >> 8 & 0xff), UInt8(v >> 16 & 0xff), UInt8(v >> 24 & 0xff)])
    }
    let handle = try FileHandle(forUpdating: url)
    defer { try? handle.close() }
    try handle.seek(toOffset: 4)
    try handle.write(contentsOf: little(layout.dataOffset + audio))  // RIFF size: everything after its own 8 bytes
    try handle.seek(toOffset: layout.dataOffset + 4)
    try handle.write(contentsOf: little(audio))
    return true
  }
}

/// `meeting.json` in a meeting's recording folder, written when the meeting starts: enough to
/// put the recording next to its draft transcript, and to rebuild the transcript from it
/// (`--reprocess`), if the meeting is never saved.
struct MeetingSidecar: Codable {
  static let fileName = "meeting.json"

  /// Path of the draft transcript.
  var transcript: String
  /// "translate" or "transcribe".
  var mode: String
  var spokenLocale: String
  var secondLocale: String?
  /// Started from the second Transcribe entry of the menu.
  var secondLanguageEntry: Bool
  var startedAt: Date
  var includesMicrophone: Bool

  func write(to folder: URL) {
    let encoder = JSONEncoder()
    encoder.dateEncodingStrategy = .iso8601
    encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
    do {
      try encoder.encode(self).write(to: folder.appendingPathComponent(Self.fileName), options: .atomic)
    } catch {
      Log.write("meeting.json not written: \(error)")
    }
  }

  static func read(from folder: URL) -> MeetingSidecar? {
    let decoder = JSONDecoder()
    decoder.dateDecodingStrategy = .iso8601
    guard let data = try? Data(contentsOf: folder.appendingPathComponent(fileName)) else { return nil }
    return try? decoder.decode(MeetingSidecar.self, from: data)
  }
}

/// Recording folders that outlived their meeting: their audio is repaired and moved to where the
/// user finds it. Audio is never deleted here.
enum Recovery {
  /// One meeting that wasn't saved: its draft transcript, if it is still there, and where its
  /// recordings are now.
  struct Meeting {
    let id: String
    let transcript: URL?
    let recordings: [URL]
  }

  /// Where meetings record to while they run, one folder each.
  static var recordingsFolder: URL {
    FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
      .appendingPathComponent("Kurzschrift/Recordings")
  }

  /// The folders in `recordings`. Called while no meeting runs, they are all leftovers.
  static func leftoverFolders(in recordings: URL) -> [URL] {
    let found = (try? FileManager.default.contentsOfDirectory(
      at: recordings, includingPropertiesForKeys: [.isDirectoryKey], options: [.skipsHiddenFiles])) ?? []
    return found.filter { (try? $0.resourceValues(forKeys: [.isDirectoryKey]).isDirectory) == true }
      .sorted { $0.lastPathComponent < $1.lastPathComponent }
  }

  /// A file in `folder` changed within the last `seconds`: something is still recording into it
  /// (another copy of the app, perhaps), so it isn't a leftover yet.
  static func isBeingWritten(_ folder: URL, within seconds: TimeInterval) -> Bool {
    let files = (try? FileManager.default.contentsOfDirectory(
      at: folder, includingPropertiesForKeys: [.contentModificationDateKey])) ?? []
    return files.contains { file in
      guard let changed = try? file.resourceValues(forKeys: [.contentModificationDateKey]).contentModificationDate
      else { return false }
      return Date().timeIntervalSince(changed) < seconds
    }
  }

  /// Recovers every leftover folder in `recordings` and returns where the recordings are now.
  /// `fallback` is the transcripts folder: recordings whose draft transcript is unknown or gone
  /// go into a "Recovered <date time>" folder there.
  @discardableResult
  static func recoverUnsavedMeetings(in recordings: URL, fallback: URL) -> [URL] {
    recover(leftoverFolders(in: recordings), fallback: fallback).flatMap(\.recordings)
  }

  /// For each folder with audio: repairs the WAV headers, moves the recordings next to the draft
  /// transcript as "<draft name> (meeting audio, recovered).wav" and "… (microphone, recovered).wav"
  /// (or into `fallback`/"Recovered <date time>"), and removes the emptied folder. A folder
  /// without audio is just removed.
  static func recover(_ folders: [URL], fallback: URL) -> [Meeting] {
    folders.compactMap { recover($0, fallback: fallback) }
  }

  private static let audioExtensions: Set<String> = ["wav", "m4a", "caf", "aif", "aiff", "mp3", "flac"]

  private static func hasAudio(_ file: URL) -> Bool {
    guard audioExtensions.contains(file.pathExtension.lowercased()) else { return false }
    if file.pathExtension.lowercased() == "wav", let layout = WAVRepair.layout(of: file) {
      return layout.available >= layout.blockAlign
    }
    return ((try? file.resourceValues(forKeys: [.fileSizeKey]).fileSize) ?? 0) > 0
  }

  /// What may go when the folder does: our own JSON files, hidden files, and audio files without
  /// any audio in them (a WAV that is only its header).
  private static func isDisposable(_ file: URL) -> Bool {
    if file.lastPathComponent.hasPrefix(".") || file.pathExtension.lowercased() == "json" { return true }
    return audioExtensions.contains(file.pathExtension.lowercased()) && !hasAudio(file)
  }

  private static func contents(of folder: URL) -> [URL] {
    (try? FileManager.default.contentsOfDirectory(at: folder, includingPropertiesForKeys: [.fileSizeKey, .creationDateKey])) ?? []
  }

  private static func removeIfOnlyLeftovers(_ folder: URL) -> Bool {
    guard contents(of: folder).allSatisfy(isDisposable) else { return false }
    return (try? FileManager.default.removeItem(at: folder)) != nil
  }

  private static func recover(_ folder: URL, fallback: URL) -> Meeting? {
    let fm = FileManager.default
    let id = folder.lastPathComponent
    // Meeting audio first, then the microphone.
    let audio = contents(of: folder).filter(hasAudio).sorted { $0.lastPathComponent > $1.lastPathComponent }
    guard !audio.isEmpty else {
      Log.write("recovery: \(id): no audio, \(removeIfOnlyLeftovers(folder) ? "removed" : "left as it is")")
      return nil
    }

    let sidecar = MeetingSidecar.read(from: folder)
    let draft = sidecar.map { URL(fileURLWithPath: $0.transcript) }.flatMap { fm.fileExists(atPath: $0.path) ? $0 : nil }
    let started = sidecar?.startedAt
      ?? audio.compactMap { try? $0.resourceValues(forKeys: [.creationDateKey]).creationDate }.min() ?? Date()
    let stamp = stampFormatter.string(from: started)
    // Next to the draft, or in a folder of their own.
    var places: [(folder: URL, base: String)] = []
    if let draft { places.append((draft.deletingLastPathComponent(), draft.deletingPathExtension().lastPathComponent)) }
    places.append((unused(fallback.appendingPathComponent("Recovered \(stamp)")), stamp))

    var repaired = 0
    var recordings: [URL] = []
    var moved = 0
    for file in audio {
      if file.pathExtension.lowercased() == "wav", (try? WAVRepair.repair(file)) == true { repaired += 1 }
      let stem = file.deletingPathExtension().lastPathComponent
      let name = stem == "system" ? "meeting audio" : stem
      var target: URL?
      for place in places {
        let candidate = unused(
          place.folder.appendingPathComponent("\(place.base) (\(name), recovered)").appendingPathExtension(file.pathExtension))
        do {
          try fm.createDirectory(at: place.folder, withIntermediateDirectories: true)
          try fm.moveItem(at: file, to: candidate)
          target = candidate
          break
        } catch {
          // The error's code, not its text: that would name the transcript the recording belongs to.
          let reason = error as NSError
          Log.write("recovery: \(id): a recording couldn't be moved (\(reason.domain) \(reason.code))")
        }
      }
      if target != nil { moved += 1 }
      recordings.append(target ?? file)  // not moved: it stays where it is, and is still reported
    }
    let removed = moved == audio.count && removeIfOnlyLeftovers(folder)
    Log.write(
      "recovery: \(id): \(moved) of \(audio.count) recording(s) moved"
        + (draft != nil ? " next to the draft transcript" : " into a Recovered folder (no draft transcript)")
        + (repaired > 0 ? ", \(repaired) WAV header(s) repaired" : "") + (removed ? ", folder removed" : ", folder kept"))
    return Meeting(id: id, transcript: draft, recordings: recordings)
  }

  /// `url`, or the same name with " 2", " 3", … when something is already there: nothing is overwritten.
  private static func unused(_ url: URL) -> URL {
    let fm = FileManager.default
    guard fm.fileExists(atPath: url.path) else { return url }
    let ext = url.pathExtension
    let base = url.deletingPathExtension()
    for n in 2..<10_000 {
      let name = "\(base.lastPathComponent) \(n)"
      var candidate = base.deletingLastPathComponent().appendingPathComponent(name)
      if !ext.isEmpty { candidate = candidate.appendingPathExtension(ext) }
      if !fm.fileExists(atPath: candidate.path) { return candidate }
    }
    return url
  }

  /// The same "2026-09-17 14-05" as in transcript file names.
  private static let stampFormatter: DateFormatter = {
    let f = DateFormatter()
    f.locale = Locale(identifier: "en_US_POSIX")
    f.dateFormat = "yyyy-MM-dd HH-mm"
    return f
  }()
}
