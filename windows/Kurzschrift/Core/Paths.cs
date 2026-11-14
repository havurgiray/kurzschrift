namespace Kurzschrift;

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

  public static string DefaultSaveFolder =>
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Meeting Transcripts");
}
