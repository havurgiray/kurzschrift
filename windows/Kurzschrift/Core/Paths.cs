namespace Kurzschrift;

/// Small files that must never be half written or lost to one bad read (voices, API keys).
public static class SafeFile {
  /// Writes next to the file and moves the result over it, so the file is always whole.
  public static bool Write(string path, string text, out string? error) {
    var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
    try {
      Directory.CreateDirectory(Path.GetDirectoryName(path)!);
      File.WriteAllText(temp, text);
      File.Move(temp, path, overwrite: true);
      error = null;
      return true;
    } catch (Exception e) {
      try { File.Delete(temp); } catch { }
      error = e.Message;
      return false;
    }
  }

  /// Renames a file whose content can't be used ("voices.json" → "voices.unreadable-20261118-141500.json")
  /// so a new one can be started without destroying it.
  public static bool SetAside(string path) {
    try {
      var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
      var aside = Path.Combine(Path.GetDirectoryName(path)!,
        $"{Path.GetFileNameWithoutExtension(path)}.unreadable-{stamp}{Path.GetExtension(path)}");
      File.Move(path, aside);
      Log.Write($"{Path.GetFileName(path)} couldn't be read as JSON; it was kept as {Path.GetFileName(aside)}");
      return true;
    } catch (Exception e) {
      Log.Write($"setting {Path.GetFileName(path)} aside failed: {e.Message}");
      return false;
    }
  }
}

/// Where Kurzschrift keeps its files on Windows.
public static class Paths {
  /// %APPDATA%\Kurzschrift: settings and API keys (roam with the user profile).
  public static string Config =>
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Kurzschrift");

  /// %LOCALAPPDATA%\Kurzschrift: models, recordings in progress, log (machine-local, large).
  public static string Local =>
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kurzschrift");

  public static string SettingsFile => Path.Combine(Config, "settings.json");

  /// Same JSON format as the Mac version's ~/Library/Application Support/Kurzschrift/api-keys.json.
  public static string ApiKeysFile => Path.Combine(Config, "api-keys.json");

  public static string Models => Path.Combine(Local, "Models");
  public static string Recordings => Path.Combine(Local, "Recordings");
  public static string LogFile => Path.Combine(Local, "Kurzschrift.log");

  /// Where a transcript or recording goes when the transcripts folder can't be written, and where
  /// the recording of a meeting that didn't finish saving is put when its transcript is gone.
  public static string Unsaved => Path.Combine(Local, "Unsaved");

  public static string DefaultSaveFolder =>
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Meeting Transcripts");
}
