using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;

namespace Kurzschrift;

/// Settings, in the same sections as the Mac version.
public sealed class SettingsWindow : Window {
  static SettingsWindow? shared;
  readonly AppSettings s = AppSettings.Shared;

  public static void ShowShared() {
    shared ??= new SettingsWindow();
    if (!shared.IsVisible) shared.Show();
    shared.Activate();
  }

  /// Developer hook (kurzschrift://snapshot?window=settings&tab=…&path=…): draws one settings tab,
  /// at its full height, into a PNG.
  public static void Snapshot(string tab, string path) {
    ShowShared();
    var tabs = (TabControl)shared!.Content;
    var item = tabs.Items.Cast<TabItem>().FirstOrDefault(t => string.Equals((string)t.Header, tab, StringComparison.OrdinalIgnoreCase))
               ?? (TabItem)tabs.Items[0]!;
    tabs.SelectedItem = item;
    shared.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, () => {
      shared.UpdateLayout();
      LiveWindow.Render((FrameworkElement)((ScrollViewer)item.Content).Content, shared, path);
    });
  }

  SettingsWindow() {
    Title = "Kurzschrift Settings";
    Width = 640;
    Height = 640;
    WindowStartupLocation = WindowStartupLocation.CenterScreen;
    Closing += (_, e) => {
      Commit();
      foreach (var commit in closingCommits) commit();
      if (!App.ShuttingDown) {
        e.Cancel = true;
        Hide();
      }
    };
    Deactivated += (_, _) => Commit();
    // What can change while the window is hidden (devices plugged in, voices named) is read again.
    IsVisibleChanged += (_, _) => {
      if (IsVisible) foreach (var refresh in refreshers) refresh();
    };
    var tabs = new TabControl { Margin = new Thickness(8) };
    tabs.Items.Add(Tab("General", General()));
    tabs.Items.Add(Tab("Translate", Translate()));
    tabs.Items.Add(Tab("Transcribe", Transcribe()));
    tabs.Items.Add(Tab("Minutes", Minutes()));
    tabs.Items.Add(Tab("Meetings", Meetings()));
    tabs.Items.Add(Tab("Advanced", Advanced()));
    Content = tabs;
  }

  /// Fields that are stored when you leave them (the model, the minutes prompt). Leaving the window
  /// or closing it with the cursor still in one doesn't count as leaving the field for WPF, so
  /// they are stored then too.
  readonly List<Action> commits = new();
  /// Stored only when the window closes (a key that was typed but not tested).
  readonly List<Action> closingCommits = new();
  /// Run every time the window is shown.
  readonly List<Action> refreshers = new();

  void Commit() {
    foreach (var commit in commits) commit();
  }

  static TabItem Tab(string header, UIElement content) =>
    new() { Header = header, Content = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };

  static StackPanel Panel() => new() { Margin = new Thickness(14) };

  static TextBlock Heading(string text) =>
    new() { Text = text, FontWeight = FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(0, 14, 0, 6) };

  static TextBlock Note(string text) =>
    new() { Text = text, TextWrapping = TextWrapping.Wrap, Opacity = 0.7, FontSize = 12, Margin = new Thickness(0, 2, 0, 6) };

  /// A long label wraps instead of being cut off at the window's edge. `enabledBy`: a setting
  /// this one depends on; it is greyed out while that is off.
  CheckBox Check(string label, string property, string? enabledBy = null) {
    var c = new CheckBox { Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap }, Margin = new Thickness(0, 4, 0, 4) };
    c.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, new Binding(property) { Source = s, Mode = BindingMode.TwoWay });
    if (enabledBy != null) c.SetBinding(IsEnabledProperty, new Binding(enabledBy) { Source = s, Mode = BindingMode.OneWay });
    return c;
  }

  FrameworkElement Labeled(string label, FrameworkElement control, string? enabledBy = null) {
    var g = new Grid { Margin = new Thickness(0, 4, 0, 4) };
    g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
    g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
    var t = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
    g.Children.Add(t);
    Grid.SetColumn(control, 1);
    g.Children.Add(control);
    if (enabledBy != null) {
      g.SetBinding(IsEnabledProperty, new Binding(enabledBy) { Source = s, Mode = BindingMode.OneWay });
      void Dim() => t.Opacity = g.IsEnabled ? 1 : 0.5;
      g.IsEnabledChanged += (_, _) => Dim();
      Dim();
    }
    return g;
  }

  ComboBox Choice<T>(IEnumerable<(T Value, string Name)> items, string property) {
    var c = new ComboBox { DisplayMemberPath = "Name", SelectedValuePath = "Value" };
    c.ItemsSource = items.Select(i => new { Value = (object?)i.Value, i.Name }).ToList();
    c.SetBinding(System.Windows.Controls.Primitives.Selector.SelectedValueProperty, new Binding(property) { Source = s, Mode = BindingMode.TwoWay });
    return c;
  }

  /// An audio device list, read again every time the window is shown. A chosen device that isn't
  /// connected right now stays chosen (and listed), so it is used again when it's back.
  ComboBox DeviceChoice(Func<List<(string Id, string Name)>> devices, string defaultName, Func<string> get, Action<string> set) {
    var c = new ComboBox { DisplayMemberPath = "Name", SelectedValuePath = "Value" };
    bool filling = false;
    void Fill() {
      filling = true;
      var items = new List<(string Id, string Name)> { ("", defaultName) };
      items.AddRange(devices());
      var current = get();
      if (current.Length > 0 && items.All(i => i.Id != current)) items.Add((current, "Disconnected device"));
      c.ItemsSource = items.Select(i => new { Value = i.Id, i.Name }).ToList();
      c.SelectedValue = current;
      filling = false;
    }
    c.SelectionChanged += (_, _) => {
      if (!filling && c.SelectedValue is string id) set(id);
    };
    Fill();
    refreshers.Add(Fill);
    return c;
  }

  TextBox Text(string property, bool multiline = false) {
    var t = new TextBox { TextWrapping = TextWrapping.Wrap, AcceptsReturn = multiline, MinHeight = multiline ? 80 : 0 };
    t.SetBinding(TextBox.TextProperty, new Binding(property) { Source = s, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
    return t;
  }

  static Button SmallButton(string text) =>
    new() { Content = text, Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(10, 2, 10, 2) };

  static IEnumerable<(string, string)> SpokenLanguages() => Languages.Spoken.Select(l => (l.Code, l.Name));

  UIElement General() {
    var p = Panel();
    p.Children.Add(Labeled("Your name", Text(nameof(AppSettings.MyName))));
    p.Children.Add(Note("Your microphone's lines are labelled with it."));
    var folder = Text(nameof(AppSettings.SaveFolder));
    var browse = SmallButton("Choose…");
    browse.Click += (_, _) => {
      var d = new OpenFolderDialog { InitialDirectory = Environment.ExpandEnvironmentVariables(s.SaveFolder) };
      if (d.ShowDialog() == true) s.SaveFolder = d.FolderName;
    };
    var row = new DockPanel();
    DockPanel.SetDock(browse, Dock.Right);
    row.Children.Add(browse);
    row.Children.Add(folder);
    p.Children.Add(Labeled("Transcripts folder", row));
    p.Children.Add(Check("Show the meeting window when a recording starts", nameof(AppSettings.ShowLiveWindow)));
    var bar = Check("Minimize it to a small bar", nameof(AppSettings.LiveWindowSmall));
    bar.Margin = new Thickness(22, 0, 0, 4);
    p.Children.Add(bar);
    p.Children.Add(Labeled("Provisional text", Choice(AppSettings.ProvisionalChoices.Select(n =>
      (n, n == 0 ? "Off (only finished sentences)" : $"Every {n} s while someone speaks")), nameof(AppSettings.ProvisionalSeconds))));
    p.Children.Add(Labeled("Text size", Choice(new[] { 12.0, 13, 15, 17, 20, 24 }.Select(n => (n, $"{n:0} pt")), nameof(AppSettings.LiveFontSize))));

    p.Children.Add(Heading("Recording"));
    p.Children.Add(Check("Keep the audio next to the transcript", nameof(AppSettings.KeepAudio)));
    p.Children.Add(Labeled("Audio quality", Choice(AppSettings.AudioBitrateChoices.Select(k => (k, AppSettings.AudioBitrateName(k))), nameof(AppSettings.AudioBitrate)),
      enabledBy: nameof(AppSettings.KeepAudio)));
    p.Children.Add(Labeled("Stop after silence", Choice(AppSettings.AutoStopChoices.Select(m =>
      (m, m == 0 ? "Never" : $"{m} min without speech")), nameof(AppSettings.AutoStopMinutes))));

    p.Children.Add(Heading("Speakers"));
    p.Children.Add(Check("Recognize people I've named before", nameof(AppSettings.RecognizeVoices)));
    var voices = new StackPanel();
    void FillVoices() {
      voices.Children.Clear();
      var all = VoiceLibrary.All().OrderBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
      if (all.Count == 0) {
        voices.Children.Add(Note("No voices are remembered yet. After a meeting, rename a speaker in the meeting window and leave " +
                                 "\"Remember this voice for later meetings\" ticked."));
        return;
      }
      foreach (var voice in all) {
        var name = voice.Name;
        var line = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
        var forget = SmallButton("Forget");
        forget.Click += (_, _) => {
          VoiceLibrary.Forget(name);
          FillVoices();
        };
        DockPanel.SetDock(forget, Dock.Right);
        line.Children.Add(forget);
        var meetings = new TextBlock {
          Text = voice.Meetings == 1 ? "1 meeting" : $"{voice.Meetings} meetings", Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center,
          Margin = new Thickness(8, 0, 4, 0),
        };
        DockPanel.SetDock(meetings, Dock.Right);
        line.Children.Add(meetings);
        line.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        voices.Children.Add(line);
      }
    }
    FillVoices();
    refreshers.Add(FillVoices);
    p.Children.Add(voices);

    p.Children.Add(Heading("Subtitles"));
    p.Children.Add(Check("Show subtitles on screen during a meeting", nameof(AppSettings.Subtitles)));
    p.Children.Add(Check("At the top of the screen", nameof(AppSettings.SubtitlesAtTop), enabledBy: nameof(AppSettings.Subtitles)));
    p.Children.Add(Labeled("Subtitle size", Choice(new[] { 20.0, 24, 28, 34, 40 }.Select(n => (n, $"{n:0} pt")), nameof(AppSettings.SubtitleFontSize)),
      enabledBy: nameof(AppSettings.Subtitles)));

    p.Children.Add(Heading("Shortcuts and start"));
    p.Children.Add(Check("Global shortcuts: Ctrl+Alt+T translate, Ctrl+Alt+R transcribe, Ctrl+Alt+B bookmark", nameof(AppSettings.GlobalShortcuts)));
    var login = new CheckBox {
      Content = new TextBlock { Text = "Start Kurzschrift when I sign in to Windows", TextWrapping = TextWrapping.Wrap },
      IsChecked = s.LaunchAtLogin, Margin = new Thickness(0, 4, 0, 4),
    };
    login.Click += (_, _) => s.LaunchAtLogin = login.IsChecked == true;
    refreshers.Add(() => login.IsChecked = s.LaunchAtLogin);
    p.Children.Add(login);

    p.Children.Add(Heading("Audio devices"));
    p.Children.Add(Labeled("Meeting audio plays on", DeviceChoice(WasapiSource.Outputs, "Windows default output",
      () => s.MeetingDeviceId, id => s.MeetingDeviceId = id)));
    p.Children.Add(Note("Kurzschrift records what this device plays. Choose your headset here if Teams or Zoom plays on it and it isn't Windows' default."));
    p.Children.Add(Labeled("Microphone", DeviceChoice(WasapiSource.Microphones, "Windows default (communications)",
      () => s.MicDeviceId, id => s.MicDeviceId = id)));
    p.Children.Add(Check("Several people share this microphone (a meeting room): separate their voices too", nameof(AppSettings.MicShared)));
    return p;
  }

  UIElement Translate() {
    var p = Panel();
    p.Children.Add(Note("Translate Meeting shows the translation as people speak and saves both languages."));
    var spoken = Choice(SpokenLanguages().Where(l => Languages.Targets(l.Item1).Any()), nameof(AppSettings.TranslateSpoken));
    var target = new ComboBox { DisplayMemberPath = "Name", SelectedValuePath = "Value" };
    void FillTargets() {
      var items = Languages.Targets(s.TranslateSpoken).Select(l => new { Value = (object?)l.Code, l.Name }).ToList();
      target.ItemsSource = items;
      if (items.All(i => (string?)i.Value != s.TranslateTarget)) s.TranslateTarget = (string?)items.FirstOrDefault()?.Value ?? "en";
      target.SelectedValue = s.TranslateTarget;
    }
    FillTargets();
    target.SelectionChanged += (_, _) => { if (target.SelectedValue is string v) s.TranslateTarget = v; };
    s.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(AppSettings.TranslateSpoken)) FillTargets(); };
    p.Children.Add(Labeled("People speak", spoken));
    p.Children.Add(Labeled("Translate into", target));
    p.Children.Add(Note("A sentence that is clearly in the target language already is kept as it is; Kurzschrift can tell for English, " +
                        "German, French, Spanish, Italian and Dutch. Translation runs on this PC (OPUS-MT, University of Helsinki); " +
                        "languages without a direct model go through English."));
    p.Children.Add(Check("Include my microphone", nameof(AppSettings.TranslateIncludeMic)));
    p.Children.Add(Check("Separate the speakers (Speaker 1, 2…) when the meeting ends", nameof(AppSettings.TranslateSeparateSpeakers)));
    p.Children.Add(Labeled("Transcript layout", Choice(new[] {
      (TranscriptLayout.Interleaved, "Original and translation together"), (TranscriptLayout.Sections, "Original first, then the translation"),
    }, nameof(AppSettings.TranslateLayout))));
    return p;
  }

  UIElement Transcribe() {
    var p = Panel();
    p.Children.Add(Note("Transcribe Meeting writes down the meeting audio and your microphone."));
    p.Children.Add(Labeled("Language", Choice(SpokenLanguages(), nameof(AppSettings.TranscribeLanguage))));
    p.Children.Add(Note("Parakeet recognizes all 25 of its languages by itself; this sets the file's language and the minutes' language."));
    p.Children.Add(Check("Include my microphone", nameof(AppSettings.TranscribeIncludeMic)));
    p.Children.Add(Check("Separate the speakers (Speaker 1, 2…) when the meeting ends", nameof(AppSettings.TranscribeSeparateSpeakers)));
    p.Children.Add(Check("Second menu entry in the language of Translate Meeting", nameof(AppSettings.TranscribeSecondEntry)));
    var secondNote = Note("");
    void UpdateSecondNote() => secondNote.Text = s.SecondTranscribeTitle is { } title
      ? $"The menu also offers “{title}”: the language set under Translate, written down without translating."
      : s.TranscribeSecondEntry
        ? "Nothing to add right now: Translate Meeting and Transcribe Meeting use the same language."
        : "Adds a second Transcribe Meeting entry to the menu for the language set under Translate, written down without translating.";
    UpdateSecondNote();
    s.PropertyChanged += (_, e) => {
      if (e.PropertyName is nameof(AppSettings.TranscribeSecondEntry) or nameof(AppSettings.TranslateSpoken) or nameof(AppSettings.TranscribeLanguage))
        UpdateSecondNote();
    };
    p.Children.Add(secondNote);
    p.Children.Add(Note("Named voices and \"Recognize people I've named before\" are under General → Speakers."));
    return p;
  }

  UIElement Minutes() {
    var p = Panel();
    p.Children.Add(Note("Optional: AI minutes during the meeting, and answers to your questions. These send the transcript to the " +
                        "provider you choose, with your own API key. Everything else stays on this PC."));
    p.Children.Add(Check("Live minutes in Translate meetings", nameof(AppSettings.MinutesForTranslate)));
    p.Children.Add(Check("Live minutes in Transcribe meetings", nameof(AppSettings.MinutesForTranscribe)));
    var provider = Choice(new[] { ("anthropic", "Anthropic (Claude)"), ("openai", "OpenAI") }, nameof(AppSettings.MinutesProviderName));
    p.Children.Add(Labeled("Provider", provider));

    // The key: typed here, saved (encrypted) and checked with "Test Key", which also lists the
    // account's models, as on the Mac.
    var keyBox = new PasswordBox();
    var keyStatus = new TextBlock { Opacity = 0.7, FontSize = 12, Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap };
    var keyResult = new TextBlock { FontSize = 12, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    var testKey = SmallButton("Test Key");
    var removeKey = SmallButton("Remove");
    var model = new ComboBox { IsEditable = true };
    void UpdateKeyStatus() {
      bool saved = ApiKeyStore.Key(s.Provider) != null;
      keyStatus.Text = saved
        ? $"A {s.Provider.DisplayName()} key is saved, encrypted for your Windows account, in {Paths.ApiKeysFile}."
        : $"No {s.Provider.DisplayName()} key yet. Paste one and press Test Key.";
      removeKey.Visibility = saved ? Visibility.Visible : Visibility.Collapsed;
    }
    void Result(string? text, bool? ok) {
      keyResult.Text = text ?? "";
      keyResult.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
      keyResult.Foreground = ok switch {
        true => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2E, 0x9E, 0x4F)),
        false => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0x57, 0x2A)),
        _ => keyStatus.Foreground,
      };
    }
    // Saves what was typed; false (with the reason shown) when that didn't work.
    bool SaveTypedKey() {
      var typed = keyBox.Password.Trim();
      if (typed.Length == 0) return true;
      if (!ApiKeyStore.SetKey(typed, s.Provider)) {
        Result("The key couldn't be saved. The log (Settings → Advanced → Open Log) says why.", false);
        return false;
      }
      keyBox.Clear();
      UpdateKeyStatus();
      return true;
    }
    testKey.Click += async (_, _) => {
      if (!SaveTypedKey()) return;
      var provider = s.Provider;
      var key = ApiKeyStore.Key(provider);
      if (key == null) {
        Result("Paste an API key first.", false);
        return;
      }
      try {
        testKey.IsEnabled = false;
        Result("Testing…", null);
        var models = await new MinutesClient(provider, s.MinutesModel, key).ListModelsAsync();
        if (provider == s.Provider) {
          model.ItemsSource = models;
          // No model chosen yet (OpenAI has no default): take the first one the key can use.
          if (string.IsNullOrWhiteSpace(s.MinutesModel) && models.Count > 0) s.MinutesModel = models[0];
          model.Text = s.MinutesModel;
        }
        Result($"Key works ({models.Count} models)", true);
      } catch (Exception e) {
        Result(e.Message, false);
      } finally {
        testKey.IsEnabled = true;
      }
    };
    removeKey.Click += (_, _) => {
      if (MessageBox.Show(this, $"Remove the saved {s.Provider.DisplayName()} key?", "Kurzschrift", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
      ApiKeyStore.SetKey("", s.Provider);
      keyBox.Clear();
      Result(null, null);
      UpdateKeyStatus();
    };
    closingCommits.Add(() => SaveTypedKey());  // typed but not tested: still kept
    var keyRow = new DockPanel();
    DockPanel.SetDock(removeKey, Dock.Right);
    DockPanel.SetDock(testKey, Dock.Right);
    keyRow.Children.Add(removeKey);
    keyRow.Children.Add(testKey);
    keyRow.Children.Add(keyBox);
    p.Children.Add(Labeled("API key", keyRow));
    var getKey = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run("Get a key"));
    getKey.Click += (_, _) => TrayIcon.Open(s.Provider.KeysPage().AbsoluteUri);
    var keyLine = new TextBlock { FontSize = 12, Margin = new Thickness(190, 0, 0, 0) };
    keyLine.Inlines.Add(getKey);
    p.Children.Add(keyLine);
    p.Children.Add(keyResult);
    p.Children.Add(keyStatus);

    void UpdateModel() {
      model.Text = s.MinutesModel;
      model.ItemsSource = null;
    }
    void CommitModel() => s.MinutesModel = model.Text.Trim();
    model.LostKeyboardFocus += (_, _) => CommitModel();
    commits.Add(CommitModel);
    model.SelectionChanged += (_, _) => { if (model.SelectedItem is string m) s.MinutesModel = m; };
    p.Children.Add(Labeled("Model", model));
    p.Children.Add(Note("Test Key lists the models your key can use."));
    p.Children.Add(Labeled("Update the minutes", Choice(AppSettings.MinutesIntervalChoices.Select(m => (m, $"Every {m} minute{(m == 1 ? "" : "s")}")),
      nameof(AppSettings.MinutesIntervalMinutes))));
    s.PropertyChanged += (_, e) => {
      if (e.PropertyName == nameof(AppSettings.MinutesProviderName)) {
        keyBox.Clear();
        Result(null, null);
        UpdateKeyStatus();
        UpdateModel();
      }
    };
    refreshers.Add(UpdateKeyStatus);
    UpdateKeyStatus();
    UpdateModel();

    p.Children.Add(Heading("Names and terms"));
    p.Children.Add(Note("Names and terms that may come up, separated by commas or one per line. The minutes spell them this way."));
    p.Children.Add(Text(nameof(AppSettings.Vocabulary), multiline: true));

    p.Children.Add(Heading("Instructions for the minutes"));
    var prompt = new TextBox { TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, MinHeight = 160,
      Text = string.IsNullOrWhiteSpace(s.MinutesPrompt) ? MinutesClient.DefaultPrompt : s.MinutesPrompt };
    void CommitPrompt() => s.MinutesPrompt = prompt.Text.Trim() == MinutesClient.DefaultPrompt.Trim() ? "" : prompt.Text;
    prompt.LostKeyboardFocus += (_, _) => CommitPrompt();
    commits.Add(CommitPrompt);
    var reset = new Button { Content = "Reset to Default", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(0, 6, 0, 0) };
    reset.Click += (_, _) => {
      s.MinutesPrompt = "";
      prompt.Text = MinutesClient.DefaultPrompt;
    };
    p.Children.Add(prompt);
    p.Children.Add(reset);
    return p;
  }

  UIElement Meetings() {
    var p = Panel();
    p.Children.Add(Check("Notice when a Teams, Zoom or Webex call starts and offer to record it", nameof(AppSettings.DetectCalls)));
    p.Children.Add(Check("Stop and save when the call ends", nameof(AppSettings.StopWhenCallEnds), enabledBy: nameof(AppSettings.DetectCalls)));
    p.Children.Add(Note("Kurzschrift sees which apps use the microphone the same way Windows' privacy settings do. It hears nothing until you start a recording."));
    p.Children.Add(Heading("Recording other people"));
    p.Children.Add(Note("Recording other people may need their consent where you live. Let the people in your meetings know that you transcribe them."));
    return p;
  }

  UIElement Advanced() {
    var p = Panel();
    p.Children.Add(Heading("Models"));
    long bytes = 0;
    try {
      if (Directory.Exists(Paths.Models))
        bytes = new DirectoryInfo(Paths.Models).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
    } catch { }
    p.Children.Add(Note($"Speech, translation and speaker models: {bytes / 1e6:0} MB in {Paths.Models}."));
    var openModels = new Button { Content = "Open Models Folder", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 2, 10, 2) };
    openModels.Click += (_, _) => {
      Directory.CreateDirectory(Paths.Models);
      TrayIcon.Open(Paths.Models);
    };
    p.Children.Add(openModels);
    p.Children.Add(Heading("Troubleshooting"));
    var openLog = new Button { Content = "Open Log", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 2, 10, 2) };
    openLog.Click += (_, _) => { if (File.Exists(Paths.LogFile)) TrayIcon.Open(Paths.LogFile); };
    p.Children.Add(openLog);
    p.Children.Add(Note("The log lists what Kurzschrift did (times in UTC), never what was said."));
    p.Children.Add(Heading("Developer links"));
    p.Children.Add(Check("Allow developer links (kurzschrift://start?audio=…, snapshot, window, context, edit-minutes)", nameof(AppSettings.DeveloperLinks)));
    p.Children.Add(Note("Off by default: any web page can try to open a kurzschrift:// link."));
    p.Children.Add(Check("Allow links to ask questions, start AI minutes and rename speakers", nameof(AppSettings.AutomationLinks)));
    p.Children.Add(Note("Off by default: any web page can try to open a kurzschrift:// link, and these send the transcript to your " +
                        "AI provider or change the saved file. Start, stop, toggle and bookmark links always work."));
    p.Children.Add(Heading("Updates"));
    var updateStatus = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), TextWrapping = TextWrapping.Wrap };
    var checkNow = new Button { Content = "Check Now", Padding = new Thickness(10, 2, 10, 2) };
    var getUpdate = new Button { Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(10, 0, 0, 0), Visibility = Visibility.Collapsed };
    checkNow.Click += (_, _) => _ = UpdateChecker.Check();
    getUpdate.Click += (_, _) => TrayIcon.Open(UpdateChecker.AvailablePage);
    void UpdateStatus() {
      checkNow.IsEnabled = UpdateChecker.State != UpdateChecker.Status.Checking;
      getUpdate.Visibility = UpdateChecker.State == UpdateChecker.Status.Available ? Visibility.Visible : Visibility.Collapsed;
      getUpdate.Content = $"Get {UpdateChecker.AvailableVersion}…";
      updateStatus.Text = UpdateChecker.State switch {
        UpdateChecker.Status.Checking => "Checking…",
        UpdateChecker.Status.UpToDate => $"{Program.Version} is the latest version.",
        UpdateChecker.Status.Available => $"Kurzschrift {UpdateChecker.AvailableVersion} is available.",
        UpdateChecker.Status.Failed => UpdateChecker.Problem ?? "",
        _ => s.UpdateLastChecked > DateTime.MinValue ? $"Last checked {s.UpdateLastChecked.ToLocalTime():g}." : "",
      };
    }
    UpdateChecker.Changed += () => Dispatcher.BeginInvoke(UpdateStatus);
    UpdateStatus();
    var updateRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
    updateRow.Children.Add(checkNow);
    updateRow.Children.Add(getUpdate);
    updateRow.Children.Add(updateStatus);
    p.Children.Add(updateRow);
    p.Children.Add(Check("Check for a new version once a day", nameof(AppSettings.CheckForUpdates)));
    p.Children.Add(Note("Asks GitHub for the number of the latest release, nothing else, and tells you in the menu when there is one. " +
                        "Kurzschrift never downloads or installs anything by itself."));
    p.Children.Add(Heading("About"));
    p.Children.Add(Note($"Kurzschrift {Program.Version} for Windows ({System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}). " +
                        "Free software under the AGPL v3, without any warranty: https://github.com/havurgiray/kurzschrift. Speech recognition: NVIDIA Parakeet " +
                        "(CC BY 4.0) via sherpa-onnx; translation: OPUS-MT, University of Helsinki (Apache 2.0 or CC BY 4.0, by language pair); " +
                        "speaker separation: pyannote segmentation 3.0 (MIT) and WeSpeaker (CC BY 4.0)."));
    var licenses = new Button { Content = "Licenses…", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 2, 10, 2) };
    licenses.Click += (_, _) => {
      var notices = Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.txt");
      TrayIcon.Open(File.Exists(notices) ? notices : "https://github.com/havurgiray/kurzschrift/tree/main/notices");
    };
    p.Children.Add(licenses);
    return p;
  }
}
