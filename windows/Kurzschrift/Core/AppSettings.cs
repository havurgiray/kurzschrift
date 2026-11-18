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
  string liveWindowBounds = "";
  string liveBarBounds = "";
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
  bool transcribeSecondEntry = true;

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
  bool automationLinks;
  bool checkForUpdates;
  DateTime updateLastChecked;
  string updateKnownVersion = "";

  public string SaveFolder { get => saveFolder; set => Set(ref saveFolder, value); }
  public string MyName { get => myName; set => Set(ref myName, value); }
  public bool ShowLiveWindow { get => showLiveWindow; set => Set(ref showLiveWindow, value); }
  /// The meeting window shrunk to a small bar: clock, latest caption, Bookmark and Stop.
  public bool LiveWindowSmall { get => liveWindowSmall; set => Set(ref liveWindowSmall, value); }
  /// Where the meeting window and the small bar were last: "left,top,width,height".
  public string LiveWindowBounds { get => liveWindowBounds; set => Set(ref liveWindowBounds, value ?? ""); }
  public string LiveBarBounds { get => liveBarBounds; set => Set(ref liveBarBounds, value ?? ""); }
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
  /// A second "Transcribe Meeting" entry in the menu, in the spoken language of Translate Meeting.
  public bool TranscribeSecondEntry { get => transcribeSecondEntry; set => Set(ref transcribeSecondEntry, value); }

  public bool MinutesForTranslate { get => minutesForTranslate; set => Set(ref minutesForTranslate, value); }
  public bool MinutesForTranscribe { get => minutesForTranscribe; set => Set(ref minutesForTranscribe, value); }
  public string MinutesProviderName { get => minutesProvider; set => Set(ref minutesProvider, value); }
  public string MinutesModelAnthropic { get => minutesModelAnthropic; set => Set(ref minutesModelAnthropic, value); }
  public string MinutesModelOpenAI { get => minutesModelOpenAI; set => Set(ref minutesModelOpenAI, value); }
  public int MinutesIntervalMinutes { get => minutesIntervalMinutes; set => Set(ref minutesIntervalMinutes, value); }
  /// The minutes prompt; empty means the built-in default (so improvements to it still arrive).
  public string MinutesPrompt { get => minutesPrompt; set => Set(ref minutesPrompt, value); }

  /// The output device the meeting plays on; "" = Windows' default output at the time.
  public string MeetingDeviceId { get => meetingDeviceId; set => Set(ref meetingDeviceId, value ?? ""); }
  public string MicDeviceId { get => micDeviceId; set => Set(ref micDeviceId, value ?? ""); }
  /// Several people share your microphone (a meeting room): separate their voices too.
  public bool MicShared { get => micShared; set => Set(ref micShared, value); }

  /// kurzschrift:// links meant for testing (start?audio=, snapshot, window, context).
  /// Off by default: any web page can try to open a kurzschrift:// link.
  public bool DeveloperLinks { get => developerLinks; set => Set(ref developerLinks, value); }

  /// kurzschrift:// links that send the transcript to the AI provider or change the saved file
  /// (ask, minutes when it starts or redoes AI minutes, rename). Off by default, for the same
  /// reason. Start, stop, toggle and bookmark links always work.
  public bool AutomationLinks { get => automationLinks; set => Set(ref automationLinks, value); }

  /// Ask GitHub once a day whether a newer release exists. Off by default: it is the one request
  /// Kurzschrift would make by itself.
  public bool CheckForUpdates { get => checkForUpdates; set => Set(ref checkForUpdates, value); }
  /// When the last check succeeded (UTC), and a newer version it found, so the menu keeps offering it.
  public DateTime UpdateLastChecked { get => updateLastChecked; set => Set(ref updateLastChecked, value); }
  public string UpdateKnownVersion { get => updateKnownVersion; set => Set(ref updateKnownVersion, value); }

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
  /// `spoken` is the language of a transcribed meeting when it isn't the first Transcribe entry's.
  public MinutesConfig? MinutesConfigFor(MeetingMode mode, bool evenIfOff = false, string? spoken = null) {
    if (!evenIfOff && !MinutesEnabled(mode)) return null;
    var language = Languages.Name(mode == MeetingMode.Translate ? TranslateTarget : spoken ?? TranscribeLanguage);
    return MinutesConfig.FromSettings(Provider, MinutesModel.Trim(), MinutesIntervalMinutes, language, MinutesPrompt, VocabularyTerms);
  }

  public string? MinutesProblem(MeetingMode mode) => MinutesConfig.Problem(MinutesEnabled(mode), Provider, MinutesModel.Trim());

  /// Frozen copy of the settings a session runs with, so edits mid-meeting don't half-apply.
  /// `secondLanguage` is the second Transcribe entry: Translate Meeting's language, not translated.
  public SessionConfig Snapshot(MeetingMode mode, bool secondLanguage = false) {
    bool translate = mode == MeetingMode.Translate;
    var spoken = translate ? TranslateSpoken : secondLanguage && SecondTranscribe is { } second ? second : TranscribeLanguage;
    return new SessionConfig {
      Mode = mode,
      Spoken = spoken,
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
      Minutes = MinutesConfigFor(mode, spoken: spoken),
      Vocabulary = VocabularyTerms,
      ProvisionalSeconds = ProvisionalSeconds,
    };
  }

  public string MenuTitle(MeetingMode mode) => mode == MeetingMode.Translate
    ? $"Translate Meeting ({Languages.Name(TranslateSpoken)} → {Languages.Name(TranslateTarget)})"
    : $"Transcribe Meeting ({Languages.Name(TranscribeLanguage)})";

  /// The second Transcribe entry's language, or null when it's switched off or would only repeat
  /// the first entry.
  [JsonIgnore]
  public string? SecondTranscribe =>
    TranscribeSecondEntry && Languages.Base(TranslateSpoken) != Languages.Base(TranscribeLanguage) ? TranslateSpoken : null;

  /// Its title in the menu, e.g. "Transcribe Meeting (German)".
  [JsonIgnore]
  public string? SecondTranscribeTitle => SecondTranscribe is { } l ? $"Transcribe Meeting ({Languages.Name(l)})" : null;

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

  /// Writes pending changes now; the save otherwise runs 300 ms after the last change, which
  /// quitting right after a change would cut off.
  public void Flush() {
    saveTimer?.Dispose();
    saveTimer = null;
    Save();
  }

  static readonly object SaveGate = new();

  public void Save() {
    // One at a time: the delayed save and a direct one (at launch, at quit) can meet, and both
    // write the same temporary file.
    lock (SaveGate) {
      try {
        Directory.CreateDirectory(Paths.Config);
        var tmp = Paths.SettingsFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(new Stored(this), JsonOptions));
        File.Move(tmp, Paths.SettingsFile, true);
      } catch (Exception e) {
        Log.Write($"saving settings failed: {e.Message}");
      }
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
