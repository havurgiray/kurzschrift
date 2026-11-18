using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Threading;

namespace Kurzschrift;

public enum Phase { Idle, Starting, Recording, Finishing }

/// The in-progress line of one source while someone is still talking.
public sealed class LivePartial : Observable {
  string original = "";
  string? translation;
  public AudioSourceKind Source { get; init; }
  public string? Speaker { get; init; }
  public string Original { get => original; set => Set(ref original, value); }
  public string? Translation { get => translation; set => Set(ref translation, value); }
}

public sealed class QAItem : Observable {
  string? answer, error;
  public string Question { get; init; } = "";
  public string? Answer { get => answer; set => Set(ref answer, value); }
  public string? Error { get => error; set => Set(ref error, value); }
}

public sealed class FinishedMeeting {
  public required TranscriptDocument Document { get; init; }
  public required List<Turn> Turns { get; init; }
  public DateTime EndedAt { get; init; }
  public List<string> Notes { get; init; } = new();
  public List<string> AudioFiles { get; init; } = new();
  public string? Minutes { get; set; }
  /// The final minutes couldn't be written (no network, a rejected key…); they can be tried again.
  public bool MinutesFailed { get; set; }
  /// The note about that in `Notes`, removed when a later try works.
  public string? MinutesNote { get; set; }
  /// The questions asked about this meeting; the answered ones are in its file.
  public List<QAItem> Questions { get; init; } = new();
  public List<double> Bookmarks { get; init; } = new();
  public required SessionConfig Config { get; init; }
  /// Voice print per speaker name, for "remember this voice" when renaming.
  public Dictionary<string, float[]> Voices { get; init; } = new();
}

public sealed class SessionException : Exception {
  public string Suggestion { get; }
  public SessionException(string message, string suggestion = "") : base(message) => Suggestion = suggestion;
}

/// Records a meeting: audio → speech → translation → live window, draft file and minutes; on stop,
/// speakers, the final transcript, minutes and the kept recording. A port of the Mac version's
/// SessionController; its state lives on the UI thread.
public sealed class SessionController : Observable {
  public static SessionController Shared { get; } = new();

  Phase phase = Phase.Idle;
  string phaseText = "";
  SessionConfig? config;
  DateTime? startedAt;
  int elapsedSeconds;
  MinutesState? minutes;
  string? minutesDraft, warning, lastTranscript;
  int? autoStopIn;
  FinishedMeeting? finished;
  MeetingContext context = new();
  MeetingContext? minutesContext;

  public Phase Phase => phase;
  public string PhaseText => phaseText;
  public bool IsIdle => phase == Phase.Idle;
  public bool IsRecording => phase == Phase.Recording;
  public SessionConfig? Config { get => config; private set => Set(ref config, value); }
  public DateTime? StartedAt { get => startedAt; private set => Set(ref startedAt, value); }
  public int ElapsedSeconds { get => elapsedSeconds; private set { if (Set(ref elapsedSeconds, value)) Raise(nameof(ElapsedText)); } }
  public string ElapsedText => TranscriptDocument.Clock(elapsedSeconds);
  public ObservableCollection<LiveEntry> Entries { get; } = new();
  public ObservableCollection<LivePartial> Partials { get; } = new();
  public MinutesState? Minutes { get => minutes; private set => Set(ref minutes, value); }
  /// Your edits to the minutes while you're typing them.
  public string? MinutesDraft { get => minutesDraft; set => Set(ref minutesDraft, value); }
  public string? Warning { get => warning; private set => Set(ref warning, value); }
  public string? LastTranscript { get => lastTranscript; private set => Set(ref lastTranscript, value); }
  public int? AutoStopIn { get => autoStopIn; private set => Set(ref autoStopIn, value); }
  public ObservableCollection<QAItem> Questions { get; } = new();
  public FinishedMeeting? Finished { get => finished; private set => Set(ref finished, value); }
  public MeetingContext? MinutesContext { get => minutesContext; private set { Set(ref minutesContext, value); Raise(nameof(CanRedoMinutes)); } }

  public MeetingContext Context {
    get => context;
    set {
      if (context.Equals(value)) return;
      context = value;
      Raise();
      Raise(nameof(CanRedoMinutes));
      ContextChanged();
    }
  }

  /// The UI downloads missing models (with progress) and says whether they're there now.
  public Func<List<ModelStore.Model>, Task<bool>>? ModelsNeeded;
  public event Action? ShowWindowRequested;
  public event Action<string, string>? ErrorOccurred;
  public event Action? BookmarkAdded;

  readonly Dictionary<AudioSourceKind, Track> tracks = new();
  readonly Dictionary<AudioSourceKind, IAudioSource> sources = new();
  readonly Dictionary<AudioSourceKind, VadSegmenter> vads = new();
  SpeechEngine? engine;
  Task<TranslationService>? translatorTask;
  TranslationWorker? translationWorker;
  readonly List<FinalSegment> segments = new();
  TranscriptDocument? document;
  string? recordingFolder;
  MinutesRunner? minutesRunner;
  CancellationTokenSource? contextDelay;
  // What the warning banner is made of (see UpdateWarning).
  string? minutesWarning, silenceWarning, microphoneWarning, recordingWarning, loadWarning, translationWarning, saveWarning;
  readonly Dictionary<AudioSourceKind, string> captureWarnings = new();
  /// The finished file has been written at least once: no draft may overwrite it any more.
  bool finalWritten;
  /// The recording folder still holds something that couldn't be saved anywhere else.
  bool keepRecordingFolder;
  /// The meeting finished before the current one, for answers and minutes that arrive late.
  FinishedMeeting? lastFinished;
  /// The meeting's clock: elapsed time, bookmarks and auto-stop run on it, so a change of the PC's
  /// clock (daylight saving, a time sync) during a meeting can't shift or end it.
  readonly System.Diagnostics.Stopwatch clock = new();
  double Now => clock.Elapsed.TotalSeconds;
  /// On `clock`: when speech was last recognized.
  double lastSpeechAt;
  /// A new one for every meeting, so call detection can tell "the recording that ran during this call".
  public Guid MeetingId { get; private set; }
  readonly List<double> bookmarks = new();
  Guid sessionId = Guid.NewGuid();
  DispatcherTimer? tick;
  bool warnedAboutSilence;

  void SetPhase(Phase p, string text = "") {
    phase = p;
    phaseText = text;
    Raise(nameof(Phase));
    Raise(nameof(PhaseText));
    Raise(nameof(IsIdle));
    Raise(nameof(IsRecording));
    Raise(nameof(CanAsk));
    Raise(nameof(CanStartAIMinutes));
    Raise(nameof(CanRedoMinutes));
    Raise(nameof(CanRetryMinutes));
    Raise(nameof(CanEditMinutes));
    Raise(nameof(CanExport));
  }

  /// "Transcribing German" or "Translating German → English", for the window's header.
  public string ModeDescription => config is not { } c ? ""
    : c.Mode == MeetingMode.Translate && c.Target != null
      ? $"Translating {Languages.Name(c.Spoken)} → {Languages.Name(c.Target)}"
      : $"Transcribing {Languages.Name(c.Spoken)}";

  /// What is being said right now, for the small bar and the subtitles: the newest line in
  /// progress, or the last finished one while nobody is mid-sentence.
  public (string? Speaker, string Original, string? Translation)? CurrentCaption {
    get {
      var partial = Partials.LastOrDefault(p => p.Original.Length > 0);
      if (partial != null) return (partial.Speaker, partial.Original, partial.Translation);
      var entry = Entries.LastOrDefault(e => !e.IsBookmark);
      return entry != null ? (entry.Speaker, entry.Original, entry.Translation) : null;
    }
  }

  /// The banner shows everything that's wrong right now, most urgent first.
  void UpdateWarning() {
    var all = new[] {
      loadWarning, translationWarning, captureWarnings.GetValueOrDefault(AudioSourceKind.System),
      captureWarnings.GetValueOrDefault(AudioSourceKind.Microphone), silenceWarning, microphoneWarning, recordingWarning,
      saveWarning, minutesWarning,
    }.Where(w => !string.IsNullOrEmpty(w)).ToList();
    Warning = all.Count > 0 ? string.Join("\n", all) : null;
  }

  void ClearWarnings(bool keepMinutes = false) {
    silenceWarning = microphoneWarning = recordingWarning = loadWarning = translationWarning = saveWarning = null;
    if (!keepMinutes) minutesWarning = null;
    captureWarnings.Clear();
    UpdateWarning();
  }

  /// The draft file, while the meeting runs. If the transcripts folder can't be written, the draft
  /// goes to the Unsaved folder and the banner says so, instead of nobody noticing until the end.
  void WriteDraft(string? minutes) {
    if (document is not { } doc || finalWritten) return;
    bool ok = doc.WriteDraft(Entries, minutes);
    var warning = ok ? null
      : doc.SavedPath != null
        ? $"The transcript can't be written to {Path.GetDirectoryName(doc.FilePath)} ({doc.WriteError}). For now it is kept in {Path.GetDirectoryName(doc.SavedPath)}."
        : $"The transcript can't be written to {Path.GetDirectoryName(doc.FilePath)} ({doc.WriteError}). It is only in this window: export it from the menu before you quit.";
    if (warning != saveWarning) {
      saveWarning = warning;
      UpdateWarning();
    }
  }

  /// A capture that stops by itself (device unplugged, switched off) shows in the banner until it's back.
  void Watch(WasapiSource source, AudioSourceKind kind, Guid id) {
    source.Interrupted += () => Ui.Post(() => {
      if (id != sessionId) return;
      captureWarnings[kind] = kind == AudioSourceKind.System
        ? "The meeting audio stopped (device unplugged or changed). Kurzschrift is trying to reconnect…"
        : "The microphone stopped (device unplugged or changed). Kurzschrift is trying to reconnect…";
      UpdateWarning();
    });
    source.Resumed += () => Ui.Post(() => {
      if (id != sessionId) return;
      captureWarnings.Remove(kind);
      UpdateWarning();
    });
  }

  /// Any microphone failure: the meeting is recorded from the meeting audio alone, with a warning.
  void DropMicrophone(SessionConfig config, Exception e) {
    Log.Write($"microphone unavailable, recording the meeting audio only: {e.Message}");
    bool denied = e is UnauthorizedAccessException || (e is COMException c && (uint)c.HResult == 0x80070005);
    microphoneWarning = denied
      ? "Kurzschrift isn't allowed to use the microphone (Windows Settings → Privacy & security → Microphone → " +
        "\"Let desktop apps access your microphone\"). It is recording the meeting audio only."
      : "No usable microphone was found. Kurzschrift is recording the meeting audio only.";
    if (sources.Remove(AudioSourceKind.Microphone, out var source)) {
      try { source.Dispose(); } catch { }
    }
    if (tracks.Remove(AudioSourceKind.Microphone, out var track)) {
      track.Close(finishSpeech: false);
      try { File.Delete(track.FilePath); } catch { }
    }
    if (vads.Remove(AudioSourceKind.Microphone, out var vad)) {
      try { vad.Dispose(); } catch { }
    }
    config.IncludeMic = false;
    UpdateWarning();
  }

  // MARK: Start

  /// `testAudio` replays a recording instead of capturing the meeting audio (no microphone), for testing.
  /// `secondLanguage` starts the second Transcribe entry (see `AppSettings.SecondTranscribe`).
  public async Task Start(MeetingMode mode, string? testAudio = null, bool secondLanguage = false) {
    if (phase != Phase.Idle) {
      Log.Write($"start({mode}) ignored: phase is {phase}");
      return;
    }
    Log.Write($"start({mode.ToString().ToLowerInvariant()}{(secondLanguage ? ", second language" : "")})");
    FinishEditingMinutes();  // notes of the last meeting that are still being typed go into its file first
    var config = AppSettings.Shared.Snapshot(mode, secondLanguage);
    if (testAudio != null) config.IncludeMic = false;
    Config = config;
    MeetingId = Guid.NewGuid();
    // While a finished meeting is shown, the Context tab is that meeting's. Filled in before any
    // meeting (or since a start that didn't happen), it is meant for this one and is kept.
    if (Finished != null) {
      context = new MeetingContext();
      Raise(nameof(Context));
    }
    contextDelay?.Cancel();
    MinutesContext = null;
    Entries.Clear();
    Partials.Clear();
    segments.Clear();
    bookmarks.Clear();
    Questions.Clear();
    Finished = null;
    ClearWarnings();
    minutesWarning = AppSettings.Shared.MinutesProblem(mode);
    UpdateWarning();
    finalWritten = false;
    keepRecordingFolder = false;
    Minutes = null;
    MinutesDraft = null;
    warnedAboutSilence = false;
    SetPhase(Phase.Starting, "Checking the models…");
    if (AppSettings.Shared.ShowLiveWindow) ShowWindowRequested?.Invoke();

    try {
      if (mode == MeetingMode.Translate) {
        if (config.Target == null || Languages.Same(config.Spoken, config.Target))
          throw new SessionException($"People speak {Languages.Name(config.Spoken)}, the language you translate into, so there's nothing to translate.",
            "In Settings → Translate, set \"People speak\" to the meeting's language, or use Transcribe Meeting.");
        if (Languages.Route(config.Spoken, config.Target) == null)
          throw new SessionException($"There's no translation model from {Languages.Name(config.Spoken)} to {Languages.Name(config.Target)}.",
            "Choose another target language in Settings → Translate.");
      }
      var missing = ModelStore.Missing(config);
      if (missing.Count > 0) {
        bool ok = ModelsNeeded != null && await ModelsNeeded(missing);
        if (!ok) {
          Log.Write("start cancelled: models not downloaded");
          SetPhase(Phase.Idle);
          Config = null;
          return;
        }
      }

      SetPhase(Phase.Starting, "Starting audio…");
      sessionId = Guid.NewGuid();
      var id = sessionId;
      int threads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
      engine = new SpeechEngine(Task.Run(() => new Recognizer(threads)));
      engine.Recognized += (job, text, words) => Ui.Post(() => OnRecognized(id, job, text, words));
      engine.LoadFailed += e => Ui.Post(() => {
        if (id != sessionId) return;
        loadWarning = $"Speech recognition couldn't be loaded: {e.Message}";
        UpdateWarning();
      });
      if (mode == MeetingMode.Translate) {
        translatorTask = Task.Run(() => new TranslationService(config.Spoken, config.Target!, 2));
        translationWorker = new TranslationWorker(translatorTask);
        translationWorker.LoadFailed += e => Ui.Post(() => {
          if (id != sessionId) return;
          translationWarning = $"The translation couldn't be loaded: {e.Message} The transcript goes on without it.";
          UpdateWarning();
        });
      }

      recordingFolder = Path.Combine(Paths.Recordings, Guid.NewGuid().ToString());
      Directory.CreateDirectory(recordingFolder);
      var kinds = config.IncludeMic ? new[] { AudioSourceKind.System, AudioSourceKind.Microphone } : new[] { AudioSourceKind.System };
      foreach (var kind in kinds) {
        var vad = new VadSegmenter(config.ProvisionalSeconds,
          (start, samples) => engine.SubmitFinal(kind, start, samples),
          config.ProvisionalSeconds > 0 ? (start, samples) => engine.SubmitPartial(kind, start, samples) : null);
        vads[kind] = vad;
        var track = new Track(kind, Path.Combine(recordingFolder, $"{kind.Key()}.wav"), vad.Feed, vad.Flush);
        track.WriteFailed += e => Ui.Post(() => {
          if (id != sessionId) return;
          recordingWarning = $"The recording can't be written any more ({e.Message}). The transcript goes on without it.";
          UpdateWarning();
        });
        tracks[kind] = track;
      }
      if (testAudio != null) {
        // When the file ends the recording goes on, as on the Mac: stop it like any meeting.
        var file = new FileSource(testAudio, tracks[AudioSourceKind.System].Append);
        file.Finished += () => Log.Write("replay: the file has ended; still recording");
        sources[AudioSourceKind.System] = file;
      } else {
        var meeting = WasapiSource.MeetingAudio(config.MeetingDeviceId, tracks[AudioSourceKind.System].Append);
        Watch(meeting, AudioSourceKind.System, id);
        Log.Write($"meeting audio from the {(config.MeetingDeviceId.Length > 0 ? "chosen" : "default")} output device");
        sources[AudioSourceKind.System] = meeting;
      }
      if (tracks.TryGetValue(AudioSourceKind.Microphone, out var micTrack)) {
        try {
          var microphone = WasapiSource.Microphone(config.MicDeviceId, micTrack.Append);
          Watch(microphone, AudioSourceKind.Microphone, id);
          sources[AudioSourceKind.Microphone] = microphone;
          Log.Write($"microphone: the {(config.MicDeviceId.Length > 0 ? "chosen" : "default")} device");
        } catch (Exception e) {
          DropMicrophone(config, e);
        }
      }
      var start = DateTime.Now;
      clock.Restart();
      sources[AudioSourceKind.System].Start();
      if (sources.TryGetValue(AudioSourceKind.Microphone, out var mic)) {
        try {
          mic.Start();
        } catch (Exception e) {
          DropMicrophone(config, e);
        }
      }

      document = new TranscriptDocument(config, start) { Context = context };
      Recovery.WriteSidecar(recordingFolder, document.FilePath, config, start);
      WriteDraft(null);
      StartedAt = start;
      lastSpeechAt = Now;
      AutoStopIn = null;
      ElapsedSeconds = 0;
      SetPhase(Phase.Recording);
      tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
      tick.Tick += (_, _) => Tick();
      tick.Start();
      if (config.Minutes != null) {
        var runner = new MinutesRunner(config.Minutes);
        runner.StateChanged += state => Ui.Post(() => { if (id == sessionId) MinutesChanged(state); });
        runner.MeetingContext = context.PromptText;
        minutesRunner = runner;
        Minutes = runner.State;
        runner.Start();
      } else {
        // No AI minutes: the pane is for your own notes, saved with the transcript.
        Minutes = new MinutesState("", AppSettings.Shared.Provider) { UsesAI = false };
      }
      ContextChanged();
      Log.Write($"recording: sources=[{string.Join(", ", tracks.Keys.Select(k => k.Key()))}] language={config.Spoken}" +
                (config.Target != null ? $" target={config.Target}" : ""));
    } catch (Exception e) {
      Log.Write($"start failed: {e.Message}");
      await TearDown();
      SetPhase(Phase.Idle);
      Config = null;
      ErrorOccurred?.Invoke(e.Message, (e as SessionException)?.Suggestion ?? "");
    }
  }

  // MARK: Stop

  /// In the first version of the final file, which is written before the minutes and the recording
  /// are done; it only stays in the file if Kurzschrift is closed before they are.
  const string StillSavingNote = "Kurzschrift was still writing the minutes or saving the recording when this version of the file was written.";

  /// Stops recording and saves. Always ends idle with everything torn down. What can't be saved
  /// where it belongs is kept in the Unsaved folder, and the user is told through ErrorOccurred.
  public async Task Stop() {
    if (phase != Phase.Recording || config is not { } cfg || document is not { } doc) {
      Log.Write($"stop ignored: phase is {phase}");
      return;
    }
    Log.Write($"stop: {segments.Count} segments so far");
    SetPhase(Phase.Finishing, "Finishing transcription…");
    string? failure = null;
    var where = new List<string>();
    string? rescueAs = null;
    FinishedMeeting? f = null;
    try {
      FinishEditingMinutes();
      AutoStopIn = null;
      tick?.Stop();
      foreach (var s in sources.Values) s.Stop();
      var trackList = tracks.Values.ToList();
      var eng = engine;
      var tw = translationWorker;
      await Task.Run(() => {
        // Closing a track finishes its speech detector under the track's lock, so a capture or
        // timer callback that is still running can't be feeding the detector at the same time.
        foreach (var t in trackList) t.Close();
        eng?.Finish();
      });
      await Task.Run(() => tw?.Drain());
      await Dispatcher.Yield(DispatcherPriority.Background);  // let the last results land
      Partials.Clear();
      minutesRunner?.Stop();
      var endedAt = DateTime.Now;
      Log.Write($"speech finished: {segments.Count} segments");

      var notes = new List<string>();
      var speakers = new Dictionary<AudioSourceKind, List<SpeakerSpan>>();
      var voices = new Dictionary<string, float[]>();
      if (cfg.SeparateSpeakers && ModelStore.Speakers.Installed) {
        var toDiarize = new List<AudioSourceKind>();
        if (segments.Any(s => s.Source == AudioSourceKind.System)) toDiarize.Add(AudioSourceKind.System);
        if (cfg.MicShared && segments.Any(s => s.Source == AudioSourceKind.Microphone)) toDiarize.Add(AudioSourceKind.Microphone);
        foreach (var source in toDiarize) {
          if (!tracks.TryGetValue(source, out var track)) continue;
          SetPhase(Phase.Finishing, "Identifying speakers…");
          try {
            var result = await Diarization.Speakers(track.FilePath, Diarization.Timeout(track.Duration));
            speakers[source] = result.Spans;
            foreach (var (sid, v) in result.Voices) voices[$"{source.Key()}:{sid}"] = v;
            Log.Write($"diarized {source.Key()}: {result.Spans.Count} spans, {result.Spans.Select(s => s.Speaker).Distinct().Count()} speakers");
          } catch (Exception e) {
            Log.Write($"diarization failed: {e.Message}");
            notes.Add($"Speakers couldn't be identified: {e.Message}");
          }
        }
      }
      var same = Diarization.SameVoices(voices);
      if (same.Count > 0) Log.Write($"{same.Count} microphone speaker(s) are people in the meeting audio");
      var known = AppSettings.Shared.RecognizeVoices
        ? VoiceLibrary.Match(voices.Where(kv => !same.ContainsKey(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value))
        : new();
      if (known.Count > 0) Log.Write($"recognized {known.Count} known voice(s)");
      var (turns, speakerNames) = TranscriptBuilder.Build(segments, speakers, cfg, known, same);
      var voicesByName = new Dictionary<string, float[]>();
      foreach (var (label, name) in speakerNames) if (voices.TryGetValue(label, out var v)) voicesByName[name] = v;

      if (translatorTask != null && cfg.Target != null) {
        SetPhase(Phase.Finishing, "Translating transcript…");
        try {
          var translator = await translatorTask;
          await Task.Run(() => {
            foreach (var turn in turns) {
              var parts = new List<string>();
              for (int i = 0; i < turn.Pieces.Count; i++) {
                var lang = i < turn.Languages.Count ? turn.Languages[i] : "";
                parts.Add(Languages.Same(lang, cfg.Target) ? turn.Pieces[i] : translator.Translate(turn.Pieces[i]));
              }
              turn.Translation = string.Join(" ", parts);
            }
          });
        } catch (Exception e) {
          Log.Write($"translating the transcript failed: {e.Message}");
          notes.Add($"The transcript couldn't be translated: {e.Message}");
        }
      }

      // The finished transcript goes to disk now, with the minutes as they stand. The final minutes
      // and the recording can take minutes more (a slow network, a long meeting); if the PC is shut
      // down meanwhile, the transcript with its speakers is already there.
      contextDelay?.Cancel();
      doc.Context = context;
      var runner = minutesRunner;
      bool finalMinutesWanted = runner != null && turns.Count > 0;
      f = new FinishedMeeting {
        Document = doc, Turns = turns, EndedAt = endedAt, Notes = notes,
        Minutes = string.IsNullOrEmpty(Minutes?.Text) ? null : Minutes!.Text,  // live minutes, or your own notes
        Bookmarks = bookmarks.ToList(), Config = cfg, Voices = voicesByName, Questions = Questions.ToList(),
      };
      if (finalMinutesWanted || cfg.KeepAudio) {
        notes.Add(StillSavingNote);
        finalWritten = true;
        WriteFinished(f);
        notes.Remove(StillSavingNote);
        Log.Write($"transcript written: {turns.Count} turns; {(finalMinutesWanted ? "minutes" : "")}{(finalMinutesWanted && cfg.KeepAudio ? " and " : "")}{(cfg.KeepAudio ? "recording" : "")} to follow");
      }

      if (runner != null) {
        if (finalMinutesWanted) {
          SetPhase(Phase.Finishing, "Writing minutes…");
          var text = await runner.FinalizeAsync(MinutesTranscript(turns, bookmarks));
          if (text != null) {
            f.Minutes = text;
          } else {
            var why = runner.State.Error ?? "unknown error";
            const string prefix = "Final minutes failed: ";
            if (why.StartsWith(prefix, StringComparison.Ordinal)) why = why[prefix.Length..];
            f.MinutesFailed = true;
            f.MinutesNote = f.Minutes != null
              ? $"The final minutes couldn't be written ({why}); these are the last live minutes."
              : $"The final minutes couldn't be written ({why}).";
            notes.Add(f.MinutesNote);
          }
          if (cfg.KeepAudio) {
            notes.Add(StillSavingNote);
            WriteFinished(f);
            notes.Remove(StillSavingNote);
          }
        }
        MinutesContext = context;
      }

      if (cfg.KeepAudio) {
        SetPhase(Phase.Finishing, "Saving the recording…");
        foreach (var (source, track) in tracks.OrderBy(kv => kv.Key)) {
          var name = source == AudioSourceKind.System ? "meeting audio" : "microphone";
          var basePath = Path.Combine(cfg.SaveFolder, $"{doc.BaseName} ({name})");
          var saved = await Task.Run(() => KeptRecording.Save(track.FilePath, basePath, cfg.AudioBitrate));
          if (saved != null) {
            f.AudioFiles.Add(saved);
            continue;
          }
          // Not next to the transcript: keep it in the Unsaved folder rather than lose it.
          var rescued = await Task.Run(() => KeptRecording.Rescue(track.FilePath, $"{doc.BaseName} ({name})"));
          if (rescued != null) notes.Add($"The recording ({name}) couldn't be saved next to this transcript. It is in {rescued}.");
          where.Add($"The recording ({name}) is in {rescued ?? track.FilePath}.");
          if (rescued == null) keepRecordingFolder = true;
        }
      }

      foreach (var q in Questions) if (!f.Questions.Contains(q)) f.Questions.Add(q);
      Finished = f;
      lastFinished = f;
      if (f.Minutes != null && Minutes != null) Minutes = Minutes with { Text = f.Minutes };
      finalWritten = true;
      bool written = WriteFinished(f);
      LastTranscript = doc.SavedPath ?? doc.FilePath;
      if (written) {
        Log.Write($"saved {Path.GetFileName(doc.FilePath)}: {turns.Count} turns");
        if (where.Count > 0) failure = $"The recording couldn't be saved to {cfg.SaveFolder}.";
      } else {
        Log.Write($"not saved to the transcripts folder: {turns.Count} turns, {(doc.SavedPath != null ? "kept in the Unsaved folder" : "no copy could be written")}");
        failure = $"The meeting couldn't be saved to {cfg.SaveFolder}{(doc.WriteError != null ? $" ({doc.WriteError})" : "")}.";
        where.Insert(0, doc.SavedPath != null
          ? $"The transcript is in {doc.SavedPath}."
          : "The transcript couldn't be written anywhere. It is still in the meeting window: save it now with Export Transcript in the menu.");
      }
      Entries.Clear();
      foreach (var e in turns.Select(t => new LiveEntry {
                 Source = AudioSourceKind.System, Speaker = t.Speaker, Start = t.Start, Original = t.Text, Translation = t.Translation,
               }).Concat(bookmarks.Select(BookmarkEntry)).OrderBy(e => e.Start))
        Entries.Add(e);
    } catch (Exception e) {
      // Whatever went wrong, the draft or the first final version is on disk; keep the recording too.
      Log.Write($"stop failed: {e.GetType().Name}: {e.Message}");
      failure = $"The meeting couldn't be finished properly ({e.Message}).";
      if (f != null) {
        try {
          WriteFinished(f);
          Finished = f;
          lastFinished = f;
        } catch (Exception again) {
          Log.Write($"writing the transcript after the failure failed too: {again.Message}");
        }
      }
      LastTranscript = doc.SavedPath ?? doc.FilePath;
      where.Insert(0, $"The transcript {(f != null ? "" : "so far ")}is in {doc.SavedPath ?? doc.FilePath}.");
      if (cfg.KeepAudio || f == null) rescueAs = doc.BaseName;
    } finally {
      foreach (var path in await TearDown(rescueAs)) where.Add($"The recording is in {path}.");
      SetPhase(Phase.Idle);
    }
    if (failure != null) {
      Log.Write("save problem reported to the user");
      ErrorOccurred?.Invoke(failure, string.Join("\n", where));
    }
  }

  /// Never throws. `rescueAs`: the meeting didn't finish properly, so its recordings go to the
  /// Unsaved folder under this name instead of being deleted; returns where they are.
  async Task<List<string>> TearDown(string? rescueAs = null) {
    var kept = new List<string>();
    tick?.Stop();
    tick = null;
    try { minutesRunner?.Stop(); } catch { }
    minutesRunner = null;
    foreach (var s in sources.Values) {
      try { s.Dispose(); } catch { }
    }
    sources.Clear();
    var trackList = tracks.Values.ToList();
    foreach (var t in trackList) t.Close(finishSpeech: false);
    tracks.Clear();
    var vadList = vads.Values.ToList();
    vads.Clear();
    var eng = engine;
    engine = null;
    var tw = translationWorker;
    translationWorker = null;
    var tt = translatorTask;
    translatorTask = null;
    try {
      await Task.Run(() => {
        try { eng?.Dispose(); } catch { }
        try { tw?.Dispose(); } catch { }
        try { if (tt is { IsCompletedSuccessfully: true }) tt.Result.Dispose(); } catch { }
        // The tracks are closed, so nothing feeds the speech detectors any more.
        foreach (var v in vadList) {
          try { v.Dispose(); } catch { }
        }
      });
    } catch (Exception e) {
      Log.Write($"tearing down failed: {e.Message}");
    }
    document = null;
    sessionId = Guid.NewGuid();
    var folder = recordingFolder;
    recordingFolder = null;
    if (folder != null) {
      bool keep = keepRecordingFolder;
      if (rescueAs != null) {
        foreach (var t in trackList) {
          if (!File.Exists(t.FilePath)) continue;
          var name = t.Kind == AudioSourceKind.System ? "meeting audio" : "microphone";
          var rescued = KeptRecording.Rescue(t.FilePath, $"{rescueAs} ({name})");
          kept.Add(rescued ?? t.FilePath);
          if (rescued == null) keep = true;
        }
      }
      if (keep) {
        Log.Write("the recording folder is kept: not everything in it could be saved elsewhere");
      } else {
        try { Directory.Delete(folder, true); } catch { }
      }
    }
    keepRecordingFolder = false;
    clock.Stop();
    ClearWarnings(keepMinutes: true);
    GC.Collect();
    return kept;
  }

  // MARK: Live results

  void OnRecognized(Guid id, SpeechEngine.Job job, string text, List<TimedWord> words) {
    if (id != sessionId || config is not { } cfg || phase is not (Phase.Recording or Phase.Finishing)) return;
    if (text.Length > 0) {
      lastSpeechAt = Now;
      AutoStopIn = null;
    }
    var speaker = cfg.IncludeMic ? (job.Source == AudioSourceKind.Microphone ? cfg.MyName : "Others") : null;
    if (!job.Final) {
      if (text.Length == 0 || phase != Phase.Recording) return;
      var p = Partials.FirstOrDefault(x => x.Source == job.Source);
      if (p == null) {
        p = new LivePartial { Source = job.Source, Speaker = speaker };
        Partials.Add(p);
      }
      p.Original = TextCleanup.Tidy(TextCleanup.SplitLeadingPunctuation(text).Text);
      if (cfg.Target != null) {
        if (Languages.Same(Languages.Guess(text), cfg.Target)) {
          p.Translation = p.Original;
        } else {
          var original = p.Original;
          translationWorker?.Partial(job.Source, original, t => Ui.Post(() => {
            if (id == sessionId && p.Original == original) p.Translation = t;
          }));
        }
      }
      return;
    }

    var partial = Partials.FirstOrDefault(x => x.Source == job.Source);
    if (partial != null) Partials.Remove(partial);
    var segment = new FinalSegment {
      Source = job.Source, Text = text, Start = job.StartSample / (double)Track.Rate,
      End = (job.StartSample + job.Samples.Length) / (double)Track.Rate, Words = words,
      Language = Languages.Guess(text) ?? cfg.Spoken,
    };
    RepairPunctuation(segment);
    if (segment.Text.Length == 0) return;
    Log.Write(FormattableString.Invariant($"final {segment.Source.Key()} {segment.Start:F1}-{segment.End:F1}: {segment.Text.Length} chars, {segment.Words.Count} words"));
    segments.Add(segment);
    var entry = new LiveEntry { Id = segment.Id, Source = segment.Source, Speaker = speaker, Start = segment.Start, Original = segment.Text };
    if (cfg.Target != null) {
      if (Languages.Same(segment.Language, cfg.Target)) {
        entry.Translation = segment.Text;  // spoken in the translation language already
      } else {
        translationWorker?.Final(segment.Text, t => Ui.Post(() => {
          if (id != sessionId) return;
          entry.Translation = t;
          WriteDraft(Minutes?.Text);
        }));
      }
    }
    if (Insert(entry, segment)) minutesRunner?.Add(MinutesLine(segment, speaker));
    WriteDraft(Minutes?.Text);
  }

  static string MinutesLine(FinalSegment s, string? speaker) =>
    $"[{TranscriptDocument.Clock(s.Start)}] {speaker ?? "Speaker"}: {s.Text}";

  /// Returns false when the line was dropped as loudspeaker echo.
  bool Insert(LiveEntry entry, FinalSegment segment) {
    var system = segments.Where(s => s.Source == AudioSourceKind.System).ToList();
    if (segment.Source == AudioSourceKind.Microphone && TranscriptBuilder.IsEcho(segment, system)) return false;
    if (segment.Source == AudioSourceKind.System) {
      var echoes = segments.Where(s => s.Source == AudioSourceKind.Microphone && TranscriptBuilder.IsEcho(s, system))
        .Select(s => s.Id).ToHashSet();
      foreach (var e in Entries.Where(e => echoes.Contains(e.Id)).ToList()) Entries.Remove(e);
    }
    int index = 0;
    for (int i = Entries.Count - 1; i >= 0; i--) {
      if (Entries[i].Start <= entry.Start) { index = i + 1; break; }
    }
    Entries.Insert(index, entry);
    return true;
  }

  /// Moves punctuation the recognizer put at the start of this segment back to the end of the
  /// previous one from the same source.
  void RepairPunctuation(FinalSegment segment) {
    var (lead, rest) = TextCleanup.SplitLeadingPunctuation(segment.Text);
    segment.Text = TextCleanup.Tidy(rest);
    if (lead.Length == 0) return;
    if (segment.Words.Count > 0)
      segment.Words[0] = segment.Words[0] with { Text = " " + TextCleanup.SplitLeadingPunctuation(segment.Words[0].Text).Text };
    var prev = segments.LastOrDefault(s => s.Source == segment.Source);
    if (prev == null || TextCleanup.EndsWithPunctuation(prev.Text)) return;
    var mark = lead[..1];
    prev.Text += mark;
    if (prev.Words.Count > 0) prev.Words[^1] = prev.Words[^1] with { Text = prev.Words[^1].Text + mark };
    var e = Entries.FirstOrDefault(x => x.Id == prev.Id);
    if (e != null) e.Original = prev.Text;
  }

  void Tick() {
    if (phase != Phase.Recording) return;
    ElapsedSeconds = (int)Now;
    CheckAutoStop();
    if (!tracks.TryGetValue(AudioSourceKind.System, out var sys)) return;
    if (!warnedAboutSilence && ElapsedSeconds >= 20 && !sys.HasSignal) {
      warnedAboutSilence = true;
      silenceWarning = "No meeting audio received yet. Kurzschrift records what plays on the output device chosen in " +
                       "Settings → General → Audio devices (Windows' default output unless you chose another). If the meeting " +
                       "plays on a different device, such as a headset, choose it there or make it Windows' default.";
      UpdateWarning();
    } else if (silenceWarning != null && sys.HasSignal) {
      silenceWarning = null;
      UpdateWarning();
    }
  }

  /// Stops and saves after Settings → "Stop after silence" minutes without speech, counting down
  /// the last minute in the window.
  void CheckAutoStop() {
    double limit = AppSettings.Shared.AutoStopMinutes * 60;
    if (limit <= 0 || !IsRecording) return;
    double quiet = Now - lastSpeechAt;
    if (quiet >= limit) {
      Log.Write($"auto-stop after {(int)quiet} s without speech");
      AutoStopIn = null;
      _ = Stop();
    } else if (quiet >= limit - 60) {
      AutoStopIn = (int)(limit - quiet);
    } else if (AutoStopIn != null) {
      AutoStopIn = null;
    }
  }

  public void KeepRecording() {
    lastSpeechAt = Now;
    AutoStopIn = null;
  }

  /// Start/stop from the global shortcuts: stops whatever is recording, otherwise starts `mode`.
  public async Task Toggle(MeetingMode mode) {
    if (IsRecording) await Stop(); else if (IsIdle) await Start(mode);
  }

  public void AddBookmark() {
    if (!IsRecording) return;
    double time = Now;
    bookmarks.Add(time);
    int index = 0;
    for (int i = Entries.Count - 1; i >= 0; i--) {
      if (Entries[i].Start <= time) { index = i + 1; break; }
    }
    Entries.Insert(index, BookmarkEntry(time));
    minutesRunner?.Add(BookmarkLine(time));
    WriteDraft(Minutes?.Text);
    BookmarkAdded?.Invoke();
    Log.Write($"bookmark at {(int)time} s");
  }

  static LiveEntry BookmarkEntry(double time) =>
    new() { Source = AudioSourceKind.System, Start = time, Original = "Bookmark", IsBookmark = true };

  static string BookmarkLine(double time) => $"[{TranscriptDocument.Clock(time)}] ⭐ The user bookmarked this moment as important.";

  // MARK: Finished meeting

  /// Writes the finished meeting's file again with its current speaker names and questions.
  public void RewriteFinished() {
    if (Finished is { } f) WriteFinished(f);
  }

  /// Writes a finished meeting's file with what it holds now. False when it couldn't be written to
  /// the transcripts folder (it is then in the Unsaved folder; see TranscriptDocument.SavedPath).
  bool WriteFinished(FinishedMeeting f) {
    var qa = f.Questions.Where(q => q.Answer != null).Select(q => (q.Question, q.Answer!)).ToList();
    bool ok = f.Document.WriteFinal(f.Turns, f.EndedAt, f.Notes, f.AudioFiles, f.Minutes, f.Bookmarks, qa);
    if (f == Finished) LastTranscript = f.Document.SavedPath ?? f.Document.FilePath;
    return ok;
  }

  /// The speaker names of the finished meeting, for renaming.
  public List<string> FinishedSpeakers =>
    Finished?.Turns.Select(t => t.Speaker).OfType<string>().Distinct().ToList() ?? new();

  /// Renames a speaker throughout the finished meeting (file, window, minutes) and, if asked,
  /// remembers the voice so the name is used automatically next time.
  public void RenameSpeaker(string old, string newName, bool remember) {
    var name = newName.Trim();
    if (Finished is not { } f || name.Length == 0 || name == old) return;
    foreach (var t in f.Turns.Where(t => t.Speaker == old)) t.Speaker = name;
    if (f.Minutes != null) f.Minutes = Regex.Replace(f.Minutes, $@"\b{Regex.Escape(old)}\b", name.Replace("$", "$$"));
    if (f.Voices.Remove(old, out var voice) && !f.Voices.ContainsKey(name)) {
      f.Voices[name] = voice;
      if (remember) VoiceLibrary.Remember(name, voice);
    }
    foreach (var e in Entries.Where(e => e.Speaker == old)) e.Speaker = name;
    if (Minutes != null && f.Minutes != null) Minutes = Minutes with { Text = f.Minutes };
    RewriteFinished();
    Raise(nameof(FinishedSpeakers));
    Log.Write("speaker renamed");
  }

  // MARK: Questions

  public bool CanAsk => !string.IsNullOrWhiteSpace(AppSettings.Shared.MinutesModel) && ApiKeyStore.Key(AppSettings.Shared.Provider) != null;

  public void Ask(string question) {
    question = question.Trim();
    if (question.Length == 0) return;
    var s = AppSettings.Shared;
    var item = new QAItem { Question = question };
    Questions.Add(item);
    Finished?.Questions.Add(item);  // asked after the meeting: it goes into that meeting's file
    var key = ApiKeyStore.Key(s.Provider);
    if (key == null || string.IsNullOrWhiteSpace(s.MinutesModel)) {
      item.Error = key == null ? "No API key is set (Settings → Minutes)." : "No model is chosen (Settings → Minutes).";
      return;
    }
    var transcript = string.Join("\n", Entries.Select(e => e.IsBookmark
      ? $"[{TranscriptDocument.Clock(e.Start)}] ⭐ bookmarked by the user"
      : $"[{TranscriptDocument.Clock(e.Start)}] {e.Speaker ?? "Speaker"}: {e.Original}"));
    var language = Languages.Name(config?.Target ?? config?.Spoken ?? Finished?.Config.Target ?? Finished?.Config.Spoken ?? "en");
    var client = new MinutesClient(s.Provider, s.MinutesModel, key);
    var promptContext = context.PromptText;
    _ = Task.Run(async () => {
      try {
        var result = await client.AnswerAsync(question, transcript, language, promptContext);
        Ui.Post(() => {
          item.Answer = result.Text;
          Log.Write($"question answered, usage in={result.Usage.Input} out={result.Usage.Output}");
          // Into the file of the meeting it was asked about, even if the next one has started.
          // (While that meeting is still running, saving it picks the answer up.)
          var owner = new[] { Finished, lastFinished }.FirstOrDefault(m => m != null && m.Questions.Contains(item));
          if (owner != null) WriteFinished(owner);
        });
      } catch (Exception e) {
        Ui.Post(() => item.Error = e.Message);
      }
    });
  }

  // MARK: Minutes

  void MinutesChanged(MinutesState state) {
    bool textChanged = state.Text != Minutes?.Text;
    Minutes = state;
    Raise(nameof(CanRedoMinutes));
    Raise(nameof(CanRetryMinutes));
    Raise(nameof(CanEditMinutes));
    if (textChanged && phase == Phase.Recording) WriteDraft(state.Text);
  }

  public void UpdateMinutesNow() => minutesRunner?.UpdateNow();

  public void MinutesIntervalChanged() =>
    minutesRunner?.SetInterval(TimeSpan.FromMinutes(Math.Max(1, AppSettings.Shared.MinutesIntervalMinutes)));

  /// The pane holds only notes, and a key and model are set now.
  public bool CanStartAIMinutes => Minutes is { UsesAI: false } && CanAsk && (IsRecording || (IsIdle && Finished != null));

  /// "AI Minutes" in the notes pane. During the meeting: minutes from the transcript so far right
  /// away, then at the usual interval. After it: final minutes from the finished transcript. Your
  /// notes are handed over as your own edits, so they're worked in rather than replaced.
  public void StartAIMinutes() {
    var meetingConfig = IsRecording ? config : Finished?.Config;
    if (!CanStartAIMinutes || meetingConfig == null) return;
    var mc = AppSettings.Shared.MinutesConfigFor(meetingConfig.Mode, evenIfOff: true, spoken: meetingConfig.Spoken);
    if (mc == null) return;
    FinishEditingMinutes();
    var notes = Minutes?.Text ?? "";
    var id = sessionId;
    var runner = new MinutesRunner(mc);
    // After the meeting the pane follows this runner only while that meeting is still the one shown.
    var shown = IsRecording ? null : Finished;
    runner.StateChanged += state => Ui.Post(() => {
      if (shown != null ? Finished == shown : id == sessionId) MinutesChanged(state);
    });
    runner.MeetingContext = context.PromptText;
    if (notes.Length > 0) runner.FinishEditing(notes);
    Minutes = runner.State;
    minutesWarning = null;
    UpdateWarning();
    Log.Write($"AI minutes switched on {(IsRecording ? "during" : "after")} the meeting, notes: {(notes.Length == 0 ? "none" : "yes")}");
    if (IsRecording) {
      config!.Minutes = mc;
      if (document != null) document.UsesAIMinutes = true;
      foreach (var e in Entries)
        runner.Add(e.IsBookmark ? BookmarkLine(e.Start) : $"[{TranscriptDocument.Clock(e.Start)}] {e.Speaker ?? "Speaker"}: {e.Original}");
      minutesRunner = runner;
      runner.Start();
      runner.UpdateNow();
    } else if (Finished is { } f) {
      var used = context;
      _ = Task.Run(async () => {
        var text = await runner.FinalizeAsync(MinutesTranscript(f.Turns, f.Bookmarks));
        if (text == null) return;  // the error shows in the pane; your notes stay as they were
        Ui.Post(() => {
          // Into the meeting they were written for, even if the next one has started meanwhile.
          f.Document.UsesAIMinutes = true;
          f.Minutes = text;
          if (Finished == f) MinutesContext = used;
          WriteFinished(f);
        });
      });
    }
  }

  /// After the meeting, once the context has changed since the minutes were written.
  public bool CanRedoMinutes =>
    IsIdle && Finished is { Minutes: { Length: > 0 } } && Minutes is { UsesAI: true, IsUpdating: false } &&
    MinutesContext != null && !MinutesContext.Equals(context) && CanAsk;

  /// "Redo Minutes" in the Context tab: final minutes again, from the whole transcript and the
  /// current minutes, with the new context.
  public void RedoMinutes() {
    if (!CanRedoMinutes || Finished is not { } f) return;
    Log.Write("minutes redone with a changed context");
    WriteFinalMinutesAgain(f);
  }

  /// After the meeting, when its final minutes couldn't be written (no network, a rejected key,
  /// an answer that was cut off): "Try Again" in the minutes pane.
  public bool CanRetryMinutes =>
    IsIdle && Finished is { MinutesFailed: true } && Minutes is { UsesAI: true, IsUpdating: false } && CanAsk;

  public void RetryMinutes() {
    if (!CanRetryMinutes || Finished is not { } f) return;
    Log.Write("final minutes tried again");
    WriteFinalMinutesAgain(f);
  }

  /// Final minutes for a finished meeting once more, from its whole transcript, the minutes it has
  /// and the current context.
  void WriteFinalMinutesAgain(FinishedMeeting f) {
    var mc = AppSettings.Shared.MinutesConfigFor(f.Config.Mode, evenIfOff: true, spoken: f.Config.Spoken);
    if (mc == null) return;
    FinishEditingMinutes();
    var runner = new MinutesRunner(mc, f.Minutes);
    runner.StateChanged += state => Ui.Post(() => { if (Finished == f) MinutesChanged(state); });
    runner.MeetingContext = context.PromptText;
    var used = context;
    Minutes = runner.State;
    _ = Task.Run(async () => {
      var text = await runner.FinalizeAsync(MinutesTranscript(f.Turns, f.Bookmarks));
      Ui.Post(() => {
        if (text == null) return;  // the error shows in the pane; it can be tried again
        // Into the meeting they were written for, even if the next one has started meanwhile.
        f.Minutes = text;
        f.Document.UsesAIMinutes = true;
        f.MinutesFailed = false;
        if (f.MinutesNote != null) f.Notes.Remove(f.MinutesNote);
        f.MinutesNote = null;
        if (Finished == f) {
          MinutesContext = used;
          Raise(nameof(CanRetryMinutes));
        }
        WriteFinished(f);
      });
    });
  }

  /// The transcript as the minutes see it: one line per turn, with bookmarks in place.
  static string MinutesTranscript(List<Turn> turns, List<double> marks) =>
    string.Join("\n", turns.Select(t => (t.Start, Line: $"[{TranscriptDocument.Clock(t.Start)}] {t.Speaker ?? "Speaker"}: {t.Text}"))
      .Concat(marks.Select(b => (Start: b, Line: BookmarkLine(b)))).OrderBy(x => x.Start).Select(x => x.Line));

  /// Not while the meeting is being saved or final minutes are on their way: they would replace
  /// what you typed, or your "Done" would replace them.
  public bool CanEditMinutes =>
    Minutes != null && phase is Phase.Recording or Phase.Idle && !(IsIdle && Minutes is { UsesAI: true, IsUpdating: true });

  public void BeginEditingMinutes() {
    if (Minutes is not { } current || !CanEditMinutes || MinutesDraft != null) return;
    MinutesDraft = current.Text;
    minutesRunner?.BeginEditing();
  }

  /// During the meeting the next update builds on your version; afterwards it goes straight into the file.
  public void FinishEditingMinutes() {
    if (MinutesDraft is not { } draft) return;
    MinutesDraft = null;
    if (minutesRunner != null) {
      minutesRunner.FinishEditing(draft);
    } else if (phase != Phase.Idle && Minutes != null) {
      Minutes = Minutes with { Text = draft };
      WriteDraft(draft);
    } else if (Finished is { } f && Minutes != null && draft != Minutes.Text) {
      f.Minutes = draft;
      Minutes = Minutes with { Text = draft };
      RewriteFinished();
    }
  }

  public void CancelEditingMinutes() {
    if (MinutesDraft == null) return;
    MinutesDraft = null;
    minutesRunner?.CancelEditing();
  }

  // MARK: Context

  void ContextChanged() {
    contextDelay?.Cancel();
    var cts = contextDelay = new CancellationTokenSource();
    _ = Task.Delay(1000, cts.Token).ContinueWith(t => {
      if (!t.IsCanceled) Ui.Post(ApplyContext);
    }, TaskScheduler.Default);
  }

  /// Hands the Context tab to the minutes and the file. Questions read it when asked.
  void ApplyContext() {
    if (IsRecording) {
      if (minutesRunner != null) minutesRunner.MeetingContext = context.PromptText;
      if (document != null) {
        document.Context = context;
        WriteDraft(Minutes?.Text);
      }
    } else if (IsIdle && Finished is { } f) {
      f.Document.Context = context;
      RewriteFinished();
    } else {
      return;  // starting or finishing: picked up when that's done
    }
    Log.Write($"context: {context.People.Count} participants, {context.AboutText.Length} characters about the meeting");
  }

  // MARK: Export

  public bool CanExport => Entries.Count > 0 || !string.IsNullOrEmpty(Minutes?.Text);

  /// The transcript (and translation and minutes) as it stands right now.
  public string ExportMarkdown() {
    if (IsRecording && document != null) return document.DraftMarkdown(Entries, Minutes?.Text);
    if (LastTranscript != null && File.Exists(LastTranscript)) return File.ReadAllText(LastTranscript);
    return "";
  }

  public string ExportText() {
    var lines = new List<string>();
    if (!string.IsNullOrEmpty(Minutes?.Text))
      lines.AddRange(new[] { Minutes!.UsesAI ? "MINUTES" : "NOTES", "", Minutes.Text, "", "TRANSCRIPT", "" });
    foreach (var e in Entries) {
      if (e.IsBookmark) {
        lines.AddRange(new[] { $"[{TranscriptDocument.Clock(e.Start)}] * BOOKMARK *", "" });
        continue;
      }
      lines.Add($"[{TranscriptDocument.Clock(e.Start)}]{(e.Speaker != null ? " " + e.Speaker : "")}");
      lines.Add(e.Original);
      if (e.Translation != null) lines.Add(e.Translation);
      lines.Add("");
    }
    return string.Join(Environment.NewLine, lines);
  }

  public string ExportBaseName => document?.BaseName ?? (LastTranscript != null ? Path.GetFileNameWithoutExtension(LastTranscript) : "Meeting");
  public string? ExportFolder => config?.SaveFolder ?? (LastTranscript != null ? Path.GetDirectoryName(LastTranscript) : null);
}

/// Translates finished lines first and the newest in-progress line per source when there's time.
public sealed class TranslationWorker : IDisposable {
  readonly Task<TranslationService> loading;
  readonly object gate = new();
  readonly Queue<(string Text, Action<string> Done)> finals = new();
  readonly Dictionary<AudioSourceKind, (string Text, Action<string> Done)> partials = new();
  readonly Thread worker;
  bool stopped, draining;
  int busy;

  /// The model couldn't be loaded; nothing will be translated. Raised on the worker thread.
  public event Action<Exception>? LoadFailed;

  public TranslationWorker(Task<TranslationService> loading) {
    this.loading = loading;
    worker = new Thread(Run) { IsBackground = true, Name = "translation" };
    worker.Start();
  }

  public void Final(string text, Action<string> done) {
    lock (gate) {
      finals.Enqueue((text, done));
      Monitor.Pulse(gate);
    }
  }

  public void Partial(AudioSourceKind source, string text, Action<string> done) {
    lock (gate) {
      partials[source] = (text, done);
      Monitor.Pulse(gate);
    }
  }

  /// Waits until the queued finished lines are translated.
  public void Drain() {
    lock (gate) {
      draining = true;
      partials.Clear();
      while ((finals.Count > 0 || busy > 0) && !stopped) Monitor.Wait(gate, 200);
    }
  }

  void Run() {
    TranslationService service;
    try {
      service = loading.GetAwaiter().GetResult();
    } catch (Exception e) {
      Log.Write($"loading translation failed: {e.Message}");
      lock (gate) { stopped = true; Monitor.PulseAll(gate); }
      try { LoadFailed?.Invoke(e); } catch { }
      return;
    }
    while (true) {
      (string Text, Action<string> Done) job;
      lock (gate) {
        while (finals.Count == 0 && (partials.Count == 0 || draining) && !stopped) Monitor.Wait(gate);
        if (stopped) return;
        if (finals.Count > 0) {
          job = finals.Dequeue();
        } else {
          var key = partials.Keys.First();
          job = partials[key];
          partials.Remove(key);
        }
        busy++;
      }
      try {
        job.Done(service.Translate(job.Text));
      } catch (Exception e) {
        Log.Write($"translation failed: {e.Message}");
      } finally {
        lock (gate) { busy--; Monitor.PulseAll(gate); }
      }
    }
  }

  public void Dispose() {
    lock (gate) {
      stopped = true;
      Monitor.PulseAll(gate);
    }
    worker.Join(3000);
  }
}
