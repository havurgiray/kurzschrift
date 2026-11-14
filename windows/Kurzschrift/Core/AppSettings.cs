using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace Kurzschrift;

public enum MeetingMode { Translate, Transcribe }

public enum TranscriptLayout { Interleaved, Sections }

/// All user settings, saved as JSON in %APPDATA%\Kurzschrift\settings.json. Defaults match the Mac
/// version. Languages are ISO 639-1 codes ("de", "en"): Parakeet recognizes the language by itself.
public sealed class AppSettings : INotifyPropertyChanged {
  public static AppSettings Shared { get; } = Load();

  public event PropertyChangedEventHandler? PropertyChanged;

  // General
  string saveFolder = Paths.DefaultSaveFolder;
  string myName = "Me";
  bool showLiveWindow = true;
  bool liveWindowSmall;
  double liveFontSize = 15;
  int provisionalSeconds = 2;
  bool keepAudio = true;
  int audioBitrate = 32;
  int autoStopMinutes = 10;
  bool subtitles;
  bool subtitlesAtTop;
  double subtitleFontSize = 28;
  bool detectCalls = true;
  bool stopWhenCallEnds = true;
  bool recognizeVoices = true;
  bool globalShortcuts = true;
  string vocabulary = "";

  // Translate meetings
  string translateSpoken = "de";
  string translateTarget = "en";
  bool translateIncludeMic;
  bool translateSeparateSpeakers = true;
  TranscriptLayout translateLayout = TranscriptLayout.Interleaved;

  // Transcribe meetings
  string transcribeLanguage = "en";
  bool transcribeIncludeMic = true;
  bool transcribeSeparateSpeakers = true;

  // Minutes (sent to an LLM provider; off by default)
  bool minutesForTranslate;
  bool minutesForTranscribe;
  string minutesProvider = "anthropic";
  string minutesModelAnthropic = MinutesProvider.Anthropic.DefaultModel();
  string minutesModelOpenAI = MinutesProvider.OpenAI.DefaultModel();
  int minutesIntervalMinutes = 3;
  string minutesPrompt = "";

  // Devices
  string meetingDeviceId = "";
  string micDeviceId = "";
  bool micShared;

  bool developerLinks;

  public string SaveFolder { get => saveFolder; set => Set(ref saveFolder, value); }
  public string MyName { get => myName; set => Set(ref myName, value); }
  public bool ShowLiveWindow { get => showLiveWindow; set => Set(ref showLiveWindow, value); }
  /// The meeting window shrunk to a small bar: clock, latest caption, Bookmark and Stop.
  public bool LiveWindowSmall { get => liveWindowSmall; set => Set(ref liveWindowSmall, value); }
  public double LiveFontSize { get => liveFontSize; set => Set(ref liveFontSize, value); }
  /// How often provisional text is refreshed while someone speaks (0 = only finished sentences).
  public int ProvisionalSeconds { get => provisionalSeconds; set => Set(ref provisionalSeconds, value); }
  public bool KeepAudio { get => keepAudio; set => Set(ref keepAudio, value); }
  /// AAC bit rate of the kept recording in kbps; 0 keeps the uncompressed WAV.
  public int AudioBitrate { get => audioBitrate; set => Set(ref audioBitrate, value); }
  /// Minutes without recognized speech before a recording stops and saves by itself (0 = never).
  public int AutoStopMinutes { get => autoStopMinutes; set => Set(ref autoStopMinutes, value); }
  public bool Subtitles { get => subtitles; set => Set(ref subtitles, value); }
  public bool SubtitlesAtTop { get => subtitlesAtTop; set => Set(ref subtitlesAtTop, value); }
  public double SubtitleFontSize { get => subtitleFontSize; set => Set(ref subtitleFontSize, value); }
  public bool DetectCalls { get => detectCalls; set => Set(ref detectCalls, value); }
  public bool StopWhenCallEnds { get => stopWhenCallEnds; set => Set(ref stopWhenCallEnds, value); }
  public bool RecognizeVoices { get => recognizeVoices; set => Set(ref recognizeVoices, value); }
  public bool GlobalShortcuts { get => globalShortcuts; set => Set(ref globalShortcuts, value); }
  /// Names and terms, one per line or comma-separated, given to the minutes.
  public string Vocabulary { get => vocabulary; set => Set(ref vocabulary, value); }

  public string TranslateSpoken { get => translateSpoken; set => Set(ref translateSpoken, value); }
  public string TranslateTarget { get => translateTarget; set => Set(ref translateTarget, value); }
  public bool TranslateIncludeMic { get => translateIncludeMic; set => Set(ref translateIncludeMic, value); }
  public bool TranslateSeparateSpeakers { get => translateSeparateSpeakers; set => Set(ref translateSeparateSpeakers, value); }
  public TranscriptLayout TranslateLayout { get => translateLayout; set => Set(ref translateLayout, value); }

  public string TranscribeLanguage { get => transcribeLanguage; set => Set(ref transcribeLanguage, value); }
  public bool TranscribeIncludeMic { get => transcribeIncludeMic; set => Set(ref transcribeIncludeMic, value); }
  public bool TranscribeSeparateSpeakers { get => transcribeSeparateSpeakers; set => Set(ref transcribeSeparateSpeakers, value); }

  public bool MinutesForTranslate { get => minutesForTranslate; set => Set(ref minutesForTranslate, value); }
  public bool MinutesForTranscribe { get => minutesForTranscribe; set => Set(ref minutesForTranscribe, value); }
  public string MinutesProviderName { get => minutesProvider; set => Set(ref minutesProvider, value); }
  public string MinutesModelAnthropic { get => minutesModelAnthropic; set => Set(ref minutesModelAnthropic, value); }
  public string MinutesModelOpenAI { get => minutesModelOpenAI; set => Set(ref minutesModelOpenAI, value); }
  public int MinutesIntervalMinutes { get => minutesIntervalMinutes; set => Set(ref minutesIntervalMinutes, value); }
  /// The minutes prompt; empty means the built-in default (so improvements to it still arrive).
  public string MinutesPrompt { get => minutesPrompt; set => Set(ref minutesPrompt, value); }

  /// The output device the meeting plays on; "" = Windows' default output at the time.
  public string MeetingDeviceId { get => meetingDeviceId; set => Set(ref meetingDeviceId, value); }
  public string MicDeviceId { get => micDeviceId; set => Set(ref micDeviceId, value); }
  /// Several people share your microphone (a meeting room): separate their voices too.
  public bool MicShared { get => micShared; set => Set(ref micShared, value); }

  /// kurzschrift:// links meant for testing (start?audio=, snapshot, window, context).
  /// Off by default: any web page can try to open a kurzschrift:// link.
  public bool DeveloperLinks { get => developerLinks; set => Set(ref developerLinks, value); }

  public static readonly int[] AudioBitrateChoices = { 16, 24, 32, 48, 0 };
  public static readonly int[] AutoStopChoices = { 0, 5, 10, 15, 30, 60 };
  public static readonly int[] MinutesIntervalChoices = { 1, 2, 3, 5, 10 };
  public static readonly int[] ProvisionalChoices = { 0, 1, 2, 3, 5 };

  [JsonIgnore]
  public MinutesProvider Provider =>
    MinutesProviderExtensions.FromRawValue(minutesProvider) ?? MinutesProvider.Anthropic;

  [JsonIgnore]
  public string MinutesModel {
    get => Provider == MinutesProvider.Anthropic ? MinutesModelAnthropic : MinutesModelOpenAI;
    set {
      if (Provider == MinutesProvider.Anthropic) MinutesModelAnthropic = value; else MinutesModelOpenAI = value;
    }
  }

  /// Start with Windows: the per-user Run key, like the Mac's login item.
  [JsonIgnore]
  public bool LaunchAtLogin {
    get {
      using var key = Registry.CurrentUser.OpenSubKey(RunKey);
      return key?.GetValue("Kurzschrift") != null;
    }
    set {
      try {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (value) key.SetValue("Kurzschrift", $"\"{Environment.ProcessPath}\" --background");
        else key.DeleteValue("Kurzschrift", false);
      } catch (Exception e) {
        Log.Write($"Launch at login change failed: {e.Message}");
      }
      PropertyChanged?.Invoke(this, new(nameof(LaunchAtLogin)));
    }
  }
  const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

  public bool MinutesEnabled(MeetingMode mode) => mode == MeetingMode.Translate ? MinutesForTranslate : MinutesForTranscribe;

  public List<string> VocabularyTerms =>
    Vocabulary.Split(new[] { ',', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

  /// null when minutes are off for this mode, or on but missing a key or model (`MinutesProblem` says which).
  /// `evenIfOff` is for switching them on by hand during or after a meeting.
  public MinutesConfig? MinutesConfigFor(MeetingMode mode, bool evenIfOff = false) {
    if (!evenIfOff && !MinutesEnabled(mode)) return null;
    var language = Languages.Name(mode == MeetingMode.Translate ? TranslateTarget : TranscribeLanguage);
    return MinutesConfig.FromSettings(Provider, MinutesModel.Trim(), MinutesIntervalMinutes, language, MinutesPrompt, VocabularyTerms);
  }

  public string? MinutesProblem(MeetingMode mode) => MinutesConfig.Problem(MinutesEnabled(mode), Provider, MinutesModel.Trim());

  /// Frozen copy of the settings a session runs with, so edits mid-meeting don't half-apply.
  public SessionConfig Snapshot(MeetingMode mode) {
    bool translate = mode == MeetingMode.Translate;
    return new SessionConfig {
      Mode = mode,
      Spoken = translate ? TranslateSpoken : TranscribeLanguage,
      Target = translate ? TranslateTarget : null,
      IncludeMic = translate ? TranslateIncludeMic : TranscribeIncludeMic,
      SeparateSpeakers = translate ? TranslateSeparateSpeakers : TranscribeSeparateSpeakers,
      MeetingDeviceId = MeetingDeviceId,
      MicDeviceId = MicDeviceId,
      MicShared = MicShared,
      MyName = string.IsNullOrWhiteSpace(MyName) ? "Me" : MyName.Trim(),
      SaveFolder = Environment.ExpandEnvironmentVariables(SaveFolder),
      KeepAudio = KeepAudio,
      AudioBitrate = AudioBitrate,
      Layout = translate ? TranslateLayout : TranscriptLayout.Interleaved,
      Minutes = MinutesConfigFor(mode),
      Vocabulary = VocabularyTerms,
      ProvisionalSeconds = ProvisionalSeconds,
    };
  }

  public string MenuTitle(MeetingMode mode) => mode == MeetingMode.Translate
    ? $"Translate Meeting ({Languages.Name(TranslateSpoken)} → {Languages.Name(TranslateTarget)})"
    : $"Transcribe Meeting ({Languages.Name(TranscribeLanguage)})";

  public static string AudioBitrateName(int kbps) {
    int rate = kbps > 0 ? kbps : 16000 * 16 / 1000;
    int mbPerHour = (int)Math.Round(rate * 3600.0 / 8 / 1000);
    return kbps > 0 ? $"{kbps} kbps (about {mbPerHour} MB per hour)" : $"Uncompressed WAV (about {mbPerHour} MB per hour)";
  }

  // Persistence

  static readonly JsonSerializerOptions JsonOptions = new() {
    WriteIndented = true,
    Converters = { new JsonStringEnumConverter() },
    IncludeFields = true,
  };

  Timer? saveTimer;

  void Set<T>(ref T field, T value, [CallerMemberName] string? name = null) {
    if (EqualityComparer<T>.Default.Equals(field, value)) return;
    field = value;
    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    saveTimer?.Dispose();
    saveTimer = new Timer(_ => Save(), null, 300, Timeout.Infinite);
  }

  public void Save() {
    try {
      Directory.CreateDirectory(Paths.Config);
      var tmp = Paths.SettingsFile + ".tmp";
      File.WriteAllText(tmp, JsonSerializer.Serialize(new Stored(this), JsonOptions));
      File.Move(tmp, Paths.SettingsFile, true);
    } catch (Exception e) {
      Log.Write($"saving settings failed: {e.Message}");
    }
  }

  static AppSettings Load() {
    var s = new AppSettings();
    try {
      if (File.Exists(Paths.SettingsFile)) {
        var stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(Paths.SettingsFile), JsonOptions);
        stored?.ApplyTo(s);
      }
    } catch (Exception e) {
      Log.Write($"reading settings failed, using defaults: {e.Message}");
    }
    return s;
  }

  /// The JSON shape: every setting by name. Unknown or missing keys keep their defaults.
  sealed class Stored {
    public Dictionary<string, JsonElement> Values { get; set; } = new();
    public Stored() { }
    public Stored(AppSettings s) {
      foreach (var p in typeof(AppSettings).GetProperties()) {
        if (p.CanWrite && p.GetCustomAttributes(typeof(JsonIgnoreAttribute), false).Length == 0 && p.GetSetMethod() != null)
          Values[p.Name] = JsonSerializer.SerializeToElement(p.GetValue(s), p.PropertyType, JsonOptions);
      }
    }
    public void ApplyTo(AppSettings s) {
      foreach (var (name, value) in Values) {
        var p = typeof(AppSettings).GetProperty(name);
        if (p == null || !p.CanWrite || p.GetCustomAttributes(typeof(JsonIgnoreAttribute), false).Length > 0) continue;
        try {
          p.SetValue(s, value.Deserialize(p.PropertyType, JsonOptions));
        } catch {
          // A bad value keeps the default.
        }
      }
    }
  }
}

/// The settings one meeting runs with.
public sealed record SessionConfig {
  public MeetingMode Mode { get; init; }
  public string Spoken { get; init; } = "en";
  public string? Target { get; init; }
  public bool IncludeMic { get; set; }
  public bool SeparateSpeakers { get; init; }
  public string MeetingDeviceId { get; init; } = "";
  public string MicDeviceId { get; init; } = "";
  public bool MicShared { get; init; }
  public string MyName { get; init; } = "Me";
  public string SaveFolder { get; set; } = Paths.DefaultSaveFolder;
  public bool KeepAudio { get; set; }
  public int AudioBitrate { get; init; } = 32;
  public TranscriptLayout Layout { get; init; }
  public MinutesConfig? Minutes { get; set; }
  public List<string> Vocabulary { get; init; } = new();
  public int ProvisionalSeconds { get; init; } = 2;
}
