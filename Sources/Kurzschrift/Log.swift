import Foundation

/// Plain-text log at ~/Library/Logs/Kurzschrift.log, for figuring out what went wrong in a meeting.
enum Log {
  static let url = FileManager.default.urls(for: .libraryDirectory, in: .userDomainMask)[0]
    .appendingPathComponent("Logs/Kurzschrift.log")

  private static let queue = DispatchQueue(label: "Kurzschrift.log")
  private static let formatter = ISO8601DateFormatter()

  static func write(_ message: String) {
    let line = "\(formatter.string(from: Date())) \(message)\n"
    queue.async {
      if let handle = try? FileHandle(forWritingTo: url) {
        handle.seekToEndOfFile()
        handle.write(Data(line.utf8))
        try? handle.close()
      } else {
        try? FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try? Data(line.utf8).write(to: url)
      }
    }
  }
}
