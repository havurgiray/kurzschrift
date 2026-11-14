using System.Globalization;

namespace Kurzschrift;

/// Collects transcript lines during a meeting and refreshes the minutes on a timer.
///
/// Every update sends the whole transcript so far (cached by the provider) plus the current minutes.
/// You can edit the minutes in between: your version becomes the current minutes, and the list of
/// everything you changed goes with every later update and the final minutes, so the model builds
/// on your text instead of overwriting it, and doesn't bring back what you deleted.
///
/// Threading: the Mac version is @MainActor. Here one lock guards all of the runner's state, so any
/// method may be called from any thread; requests run outside the lock. StateChanged is raised
/// outside the lock too, in the order the states were made, on whichever thread made them (the UI
/// marshals them to its own thread). A handler may call back into the runner.
public sealed class MinutesRunner {
  public MinutesConfig Config { get; }
  /// Every new state of the minutes. May fire on any thread.
  public event Action<MinutesState>? StateChanged;

  readonly object gate = new();
  readonly MinutesClient client;
  readonly List<string> chunks = [];
  readonly List<string> pendingLines = [];
  /// Cancels the timer loop; null when stopped.
  CancellationTokenSource? loop;
  bool inFlight;
  MinutesState state;
  string meetingContext = "";
  TimeSpan interval;

  readonly List<MinutesEdit> edits = [];
  bool editing;
  /// Bumped by each edit, so a response to a request sent before it isn't applied over it.
  int revision;
  /// An update came due while you were editing.
  bool deferred;
  /// A response that arrived while you were editing; used if you end up changing nothing.
  string? held;

  /// States waiting to be raised, and whether some thread is raising them right now.
  readonly Queue<MinutesState> outbox = new();
  bool delivering;

  /// `draft`: minutes to start from, e.g. the finished meeting's when they're redone with a new context.
  public MinutesRunner(MinutesConfig config, string? draft = null) {
    Config = config;
    interval = config.Interval;
    client = new MinutesClient(config.Provider, config.Model, config.ApiKey);
    state = new MinutesState(config.Model, config.Provider);
    if (draft != null) state = state with { Text = draft };
  }

  public MinutesState State {
    get { lock (gate) return state; }
  }

  /// What you entered in the Context tab (MeetingContext.PromptText); the next update uses it.
  public string MeetingContext {
    get { lock (gate) return meetingContext; }
    set { lock (gate) meetingContext = value; }
  }

  /// Between updates; follows "Update every" in Settings, even mid-meeting.
  public TimeSpan Interval {
    get { lock (gate) return interval; }
  }

  /// The settings' context (vocabulary) plus the meeting's own.
  MinutesConfig RequestConfig => meetingContext.Length == 0 ? Config : Config.Adding(meetingContext);

  /// Only the 5-minute cache stays warm between updates that start less than 5 minutes apart.
  bool LongCache => interval > TimeSpan.FromMinutes(4);

  public void Start() {
    CancellationToken token;
    lock (gate) token = Restart();
    Flush();
    _ = RunLoopAsync(new WeakReference<MinutesRunner>(this), token);
  }

  /// "Update every" changed during the meeting: the next update comes one new interval from now.
  public void SetInterval(TimeSpan newInterval) {
    CancellationToken token;
    lock (gate) {
      if (newInterval == interval) return;
      interval = newInterval;
      if (loop == null) return;
      token = Restart();
    }
    Flush();
    _ = RunLoopAsync(new WeakReference<MinutesRunner>(this), token);
  }

  public void Stop() {
    lock (gate) {
      loop?.Cancel();
      loop = null;
      state = state with { NextUpdate = null };
      Publish();
    }
    Flush();
  }

  public void Add(string line) {
    lock (gate) pendingLines.Add(line);
  }

  /// "Update now" in the minutes pane. The next timed update comes a full interval after it.
  public void UpdateNow() {
    CancellationToken? token = null;
    lock (gate) {
      if (loop != null) token = Restart();
    }
    Flush();
    if (token is { } t) _ = RunLoopAsync(new WeakReference<MinutesRunner>(this), t);
    UpdateInBackground();
  }

  // Your edits

  /// Updates wait until you're done, so nothing changes under your cursor.
  public void BeginEditing() {
    lock (gate) editing = true;
  }

  public void FinishEditing(string text) {
    // A Windows text box ends lines with \r\n; the minutes use \n, as on the Mac.
    text = text.Replace("\r\n", "\n");
    bool run;
    lock (gate) {
      editing = false;
      if (MinutesEdit.Between(state.Text, text) is { } edit) {
        edits.Add(edit);
        revision += 1;
        held = null;
        state = state with { Text = text, EditsPending = true };
        Publish();
        Log.Write($"minutes edited: {edit.Removed.Count} lines removed, {edit.Added.Count} added");
      } else {
        ApplyHeld();
      }
      run = TakeDeferred();
    }
    Flush();
    if (run) UpdateInBackground();
  }

  public void CancelEditing() {
    bool run;
    lock (gate) {
      editing = false;
      ApplyHeld();
      run = TakeDeferred();
    }
    Flush();
    if (run) UpdateInBackground();
  }

  /// Under the lock.
  void ApplyHeld() {
    if (held is not { } text) return;
    held = null;
    state = state with { Text = text, UpdatedAt = DateTimeOffset.Now };
    Publish();
  }

  /// Under the lock: whether an update came due while you were editing.
  bool TakeDeferred() {
    if (!deferred) return false;
    deferred = false;
    return true;
  }

  // Updating

  /// Under the lock: (re)starts the timer and shows when the next update comes.
  CancellationToken Restart() {
    loop?.Cancel();
    loop = new CancellationTokenSource();
    Schedule();
    return loop.Token;
  }

  /// Under the lock.
  void Schedule() {
    state = state with { NextUpdate = DateTimeOffset.Now + interval };
    Publish();
  }

  /// The timer: an update one interval after the previous one has finished. It holds the runner
  /// weakly (like [weak self] on the Mac), so a runner that's dropped without Stop() stops too.
  static async Task RunLoopAsync(WeakReference<MinutesRunner> weakRunner, CancellationToken token) {
    while (!token.IsCancellationRequested) {
      if (IntervalOf(weakRunner) is not { } interval) return;
      try {
        await Task.Delay(interval, token).ConfigureAwait(false);
      } catch (OperationCanceledException) {
        return;
      }
      var update = Tick(weakRunner, token);
      if (update == null) return;
      await update.ConfigureAwait(false);
      update = null;
    }
  }

  // Not async, so the loop holds no strong reference to the runner while it waits.
  static TimeSpan? IntervalOf(WeakReference<MinutesRunner> weakRunner) =>
    weakRunner.TryGetTarget(out var runner) ? runner.Interval : null;

  /// Shows when the next update comes and starts this one; null if the runner is gone, or was
  /// stopped or restarted meanwhile.
  static Task? Tick(WeakReference<MinutesRunner> weakRunner, CancellationToken token) {
    if (!weakRunner.TryGetTarget(out var runner)) return null;
    lock (runner.gate) {
      if (token.IsCancellationRequested) return null;
      runner.Schedule();
    }
    runner.Flush();
    return runner.UpdateAsync();
  }

  void UpdateInBackground() => _ = Task.Run(UpdateAsync);

  async Task UpdateAsync() {
    while (true) {
      string[] sentChunks;
      string? previousMinutes;
      MinutesEdit[] sentEdits;
      MinutesConfig requestConfig;
      bool longCache;
      int sentRevision;
      lock (gate) {
        if (editing) {
          deferred = true;
          return;
        }
        if (inFlight) return;
        if (pendingLines.Count > 0) {
          var newText = string.Join("\n", pendingLines);
          // Wait for a little substance before the first update, unless you've already written something.
          if (chunks.Count == 0 && new StringInfo(newText).LengthInTextElements < 150 && !state.EditsPending) return;
          chunks.Add(newText);
          pendingLines.Clear();
        } else if (!state.EditsPending) {
          return;
        }
        if (chunks.Count == 0) return;

        inFlight = true;
        state = state with { IsUpdating = true };
        Publish();
        sentRevision = revision;
        sentChunks = [.. chunks];
        previousMinutes = state.Text.Length == 0 ? null : state.Text;
        sentEdits = [.. edits];
        requestConfig = RequestConfig;
        longCache = LongCache;
      }
      Flush();

      var rerun = false;
      try {
        var result = await client.MinutesAsync(
          sentChunks, previousMinutes, sentEdits, requestConfig, final: false, longCache).ConfigureAwait(false);
        lock (gate) {
          var usage = state.Usage + result.Usage;
          state = state with { Usage = usage, Cost = usage.EstimatedCost(Config.Model, LongCache), Error = null };
          Log.Write(
            $"minutes updated: {chunks.Count} chunks, {edits.Count} edits, usage in={result.Usage.Input} "
            + $"cached={result.Usage.CacheRead} written={result.Usage.CacheWrite} out={result.Usage.Output}");
          if (revision != sentRevision) {
            // You edited while this ran; redo it from your version.
            rerun = true;
          } else if (editing) {
            held = result.Text;
          } else {
            state = state with { Text = result.Text, EditsPending = false, UpdatedAt = DateTimeOffset.Now };
          }
        }
      } catch (Exception error) {
        lock (gate) state = state with { Error = error.Message };
        Log.Write($"minutes update failed: {MinutesException.LogText(error)}");
      }
      lock (gate) {
        inFlight = false;
        state = state with { IsUpdating = false };
        Publish();
      }
      Flush();
      if (!rerun) return;
    }
  }

  /// Final minutes from the finished transcript (with speaker names), using the live minutes as a
  /// draft and keeping your edits. null if they couldn't be written (State.Error says why).
  public async Task<string?> FinalizeAsync(string transcript, CancellationToken cancellationToken = default) {
    Stop();
    if (transcript.Length == 0) return null;
    string? previousMinutes;
    MinutesEdit[] sentEdits;
    MinutesConfig requestConfig;
    lock (gate) {
      state = state with { IsUpdating = true };
      Publish();
      previousMinutes = state.Text.Length == 0 ? null : state.Text;
      sentEdits = [.. edits];
      requestConfig = RequestConfig;
    }
    Flush();
    try {
      var result = await client.MinutesAsync(
        [transcript], previousMinutes, sentEdits, requestConfig, final: true, longCache: false, cancellationToken)
        .ConfigureAwait(false);
      lock (gate) {
        var usage = state.Usage + result.Usage;
        state = state with {
          Text = result.Text, EditsPending = false, UpdatedAt = DateTimeOffset.Now, Error = null, Usage = usage,
          Cost = usage.EstimatedCost(Config.Model, LongCache),
        };
        Log.Write($"final minutes written, {edits.Count} edits, usage in={result.Usage.Input} out={result.Usage.Output}");
      }
      return result.Text;
    } catch (Exception error) {
      lock (gate) state = state with { Error = "Final minutes failed: " + error.Message };
      Log.Write($"final minutes failed: {MinutesException.LogText(error)}");
      return null;
    } finally {
      lock (gate) {
        state = state with { IsUpdating = false };
        Publish();
      }
      Flush();
    }
  }

  // Publishing

  /// Under the lock: queues the current state for StateChanged.
  void Publish() => outbox.Enqueue(state);

  /// Raises StateChanged for the queued states, oldest first and outside the lock. If another thread
  /// is already doing that, it raises these too.
  void Flush() {
    lock (gate) {
      if (delivering) return;
      delivering = true;
    }
    while (true) {
      MinutesState next;
      lock (gate) {
        if (outbox.Count == 0) {
          delivering = false;
          return;
        }
        next = outbox.Dequeue();
      }
      try {
        StateChanged?.Invoke(next);
      } catch (Exception error) {
        Log.Write($"minutes state handler failed: {error.GetType().Name}: {error.Message}");
      }
    }
  }
}
