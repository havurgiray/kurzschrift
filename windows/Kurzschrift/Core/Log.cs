namespace Kurzschrift;

/// The app log, one line per event with a UTC timestamp, like the Mac version's
/// ~/Library/Logs/Kurzschrift.log. Never write API keys, transcript text or full URLs here.
public static class Log {
  static readonly object Gate = new();

  public static void Write(string message) {
    var line = $"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ} {message}";
    lock (Gate) {
      try {
        Directory.CreateDirectory(Path.GetDirectoryName(Paths.LogFile)!);
        File.AppendAllText(Paths.LogFile, line + Environment.NewLine);
      } catch {
        // Logging must never take the app down.
      }
    }
    System.Diagnostics.Debug.WriteLine(line);
  }
}
