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

  SettingsWindow() {
    Title = "Kurzschrift Settings";
    Width = 640;
    Height = 640;
    WindowStartupLocation = WindowStartupLocation.CenterScreen;
    Closing += (_, e) => {
      if (!App.ShuttingDown) {
        e.Cancel = true;
        Hide();
      }
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

  static TabItem Tab(string header, UIElement content) =>
    new() { Header = header, Content = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };

  static StackPanel Panel() => new() { Margin = new Thickness(14) };

  static TextBlock Heading(string text) =>
    new() { Text = text, FontWeight = FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(0, 14, 0, 6) };

  static TextBlock Note(string text) =>
    new() { Text = text, TextWrapping = TextWrapping.Wrap, Opacity = 0.7, FontSize = 12, Margin = new Thickness(0, 2, 0, 6) };

  CheckBox Check(string label, string property) {
    var c = new CheckBox { Content = label, Margin = new Thickness(0, 4, 0, 4) };
    c.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, new Binding(property) { Source = s, Mode = BindingMode.TwoWay });
    return c;
  }

  FrameworkElement Labeled(string label, FrameworkElement control) {
    var g = new Grid { Margin = new Thickness(0, 4, 0, 4) };
    g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
    g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
    var t = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
    g.Children.Add(t);
    Grid.SetColumn(control, 1);
    g.Children.Add(control);
    return g;
  }

  ComboBox Choice<T>(IEnumerable<(T Value, string Name)> items, string property) {
    var c = new ComboBox { DisplayMemberPath = "Name", SelectedValuePath = "Value" };
    c.ItemsSource = items.Select(i => new { Value = (object?)i.Value, i.Name }).ToList();
    c.SetBinding(System.Windows.Controls.Primitives.Selector.SelectedValueProperty, new Binding(property) { Source = s, Mode = BindingMode.TwoWay });
    return c;
  }

  TextBox Text(string property, bool multiline = false) {
    var t = new TextBox { TextWrapping = TextWrapping.Wrap, AcceptsReturn = multiline, MinHeight = multiline ? 80 : 0 };
    t.SetBinding(TextBox.TextProperty, new Binding(property) { Source = s, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
    return t;
  }

  static IEnumerable<(string, string)> SpokenLanguages() => Languages.Spoken.Select(l => (l.Code, l.Name));

  UIElement General() {
    var p = Panel();
    p.Children.Add(Labeled("Your name", Text(nameof(AppSettings.MyName))));
    p.Children.Add(Note("Your microphone's lines are labelled with it."));
    var folder = Text(nameof(AppSettings.SaveFolder));
    var browse = new Button { Content = "Choose…", Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(10, 2, 10, 2) };
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
    p.Children.Add(Labeled("Provisional text", Choice(AppSettings.ProvisionalChoices.Select(n =>
      (n, n == 0 ? "Off (only finished sentences)" : $"Every {n} s while someone speaks")), nameof(AppSettings.ProvisionalSeconds))));
    p.Children.Add(Labeled("Text size", Choice(new[] { 12.0, 13, 15, 17, 20, 24 }.Select(n => (n, $"{n:0} pt")), nameof(AppSettings.LiveFontSize))));

    p.Children.Add(Heading("Recording"));
    p.Children.Add(Check("Keep the audio next to the transcript", nameof(AppSettings.KeepAudio)));
    p.Children.Add(Labeled("Audio quality", Choice(AppSettings.AudioBitrateChoices.Select(k => (k, AppSettings.AudioBitrateName(k))), nameof(AppSettings.AudioBitrate))));
    p.Children.Add(Labeled("Stop after silence", Choice(AppSettings.AutoStopChoices.Select(m =>
      (m, m == 0 ? "Never" : $"{m} minutes without speech")), nameof(AppSettings.AutoStopMinutes))));

    p.Children.Add(Heading("Subtitles"));
    p.Children.Add(Check("Show subtitles on screen while recording", nameof(AppSettings.Subtitles)));
    p.Children.Add(Check("At the top of the screen", nameof(AppSettings.SubtitlesAtTop)));
    p.Children.Add(Labeled("Subtitle size", Choice(new[] { 20.0, 24, 28, 34, 40 }.Select(n => (n, $"{n:0} pt")), nameof(AppSettings.SubtitleFontSize))));

    p.Children.Add(Heading("Shortcuts and start"));
    p.Children.Add(Check("Global shortcuts: Ctrl+Alt+T translate, Ctrl+Alt+R transcribe, Ctrl+Alt+B bookmark", nameof(AppSettings.GlobalShortcuts)));
    var login = new CheckBox { Content = "Start Kurzschrift when I sign in to Windows", IsChecked = s.LaunchAtLogin, Margin = new Thickness(0, 4, 0, 4) };
    login.Click += (_, _) => s.LaunchAtLogin = login.IsChecked == true;
    p.Children.Add(login);

    p.Children.Add(Heading("Audio devices"));
    var outputs = new List<(string, string)> { ("", "Windows default output") };
    outputs.AddRange(WasapiSource.Outputs());
    p.Children.Add(Labeled("Meeting audio plays on", Choice(outputs, nameof(AppSettings.MeetingDeviceId))));
    p.Children.Add(Note("Kurzschrift records what this device plays. Choose your headset here if Teams or Zoom plays on it and it isn't Windows' default."));
    var mics = new List<(string, string)> { ("", "Windows default (communications)") };
    mics.AddRange(WasapiSource.Microphones());
    p.Children.Add(Labeled("Microphone", Choice(mics, nameof(AppSettings.MicDeviceId))));
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
    p.Children.Add(Note("Text that's already in the target language is kept as it is. Translation runs on this PC (OPUS-MT, University of Helsinki); " +
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
    p.Children.Add(Check("Recognize voices I've named in earlier meetings", nameof(AppSettings.RecognizeVoices)));
    var forget = new Button { Content = "Forget All Named Voices…", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(0, 6, 0, 0) };
    forget.Click += (_, _) => {
      var names = VoiceLibrary.All().Select(v => v.Name).ToList();
      if (names.Count == 0) {
        MessageBox.Show(this, "No voices are remembered.", "Kurzschrift");
        return;
      }
      if (MessageBox.Show(this, $"Forget {names.Count} remembered voice(s): {string.Join(", ", names)}?", "Kurzschrift",
            MessageBoxButton.OKCancel) == MessageBoxResult.OK)
        foreach (var n in names) VoiceLibrary.Forget(n);
    };
    p.Children.Add(forget);
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

    var keyBox = new PasswordBox();
    var keyStatus = new TextBlock { Opacity = 0.7, FontSize = 12, Margin = new Thickness(0, 2, 0, 0) };
    void UpdateKeyStatus() => keyStatus.Text = ApiKeyStore.Key(s.Provider) != null
      ? $"A {s.Provider.DisplayName()} key is saved in {Paths.ApiKeysFile}."
      : $"No {s.Provider.DisplayName()} key yet.";
    var saveKey = new Button { Content = "Save Key", Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(10, 2, 10, 2) };
    saveKey.Click += (_, _) => {
      ApiKeyStore.SetKey(keyBox.Password.Trim(), s.Provider);
      keyBox.Clear();
      UpdateKeyStatus();
    };
    var keyRow = new DockPanel();
    DockPanel.SetDock(saveKey, Dock.Right);
    keyRow.Children.Add(saveKey);
    keyRow.Children.Add(keyBox);
    p.Children.Add(Labeled("API key", keyRow));
    p.Children.Add(keyStatus);

    var model = new ComboBox { IsEditable = true };
    void UpdateModel() {
      model.Text = s.MinutesModel;
      model.ItemsSource = null;
    }
    model.LostFocus += (_, _) => s.MinutesModel = model.Text.Trim();
    model.SelectionChanged += (_, _) => { if (model.SelectedItem is string m) s.MinutesModel = m; };
    var load = new Button { Content = "Load Models", Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(10, 2, 10, 2) };
    load.Click += async (_, _) => {
      var key = ApiKeyStore.Key(s.Provider);
      if (key == null) {
        MessageBox.Show(this, "Save an API key first.", "Kurzschrift");
        return;
      }
      try {
        load.IsEnabled = false;
        var models = await new MinutesClient(s.Provider, s.MinutesModel, key).ListModelsAsync();
        model.ItemsSource = models;
        model.Text = s.MinutesModel;
      } catch (Exception e) {
        MessageBox.Show(this, e.Message, "Kurzschrift");
      } finally {
        load.IsEnabled = true;
      }
    };
    var modelRow = new DockPanel();
    DockPanel.SetDock(load, Dock.Right);
    modelRow.Children.Add(load);
    modelRow.Children.Add(model);
    p.Children.Add(Labeled("Model", modelRow));
    p.Children.Add(Labeled("Update the minutes", Choice(AppSettings.MinutesIntervalChoices.Select(m => (m, $"Every {m} minute{(m == 1 ? "" : "s")}")),
      nameof(AppSettings.MinutesIntervalMinutes))));
    s.PropertyChanged += (_, e) => {
      if (e.PropertyName == nameof(AppSettings.MinutesProviderName)) { UpdateKeyStatus(); UpdateModel(); }
    };
    UpdateKeyStatus();
    UpdateModel();

    p.Children.Add(Heading("Names and terms"));
    p.Children.Add(Note("Names and terms that may come up, separated by commas or one per line. The minutes spell them this way."));
    p.Children.Add(Text(nameof(AppSettings.Vocabulary), multiline: true));

    p.Children.Add(Heading("Instructions for the minutes"));
    var prompt = new TextBox { TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, MinHeight = 160,
      Text = string.IsNullOrWhiteSpace(s.MinutesPrompt) ? MinutesClient.DefaultPrompt : s.MinutesPrompt };
    prompt.LostFocus += (_, _) => s.MinutesPrompt = prompt.Text.Trim() == MinutesClient.DefaultPrompt.Trim() ? "" : prompt.Text;
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
    p.Children.Add(Check("Stop and save when the call ends", nameof(AppSettings.StopWhenCallEnds)));
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
    p.Children.Add(Heading("About"));
    p.Children.Add(Note($"Kurzschrift {Program.Version} for Windows ({System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}). " +
                        "Free software under the AGPL v3: https://github.com/havurgiray/kurzschrift. Speech recognition: NVIDIA Parakeet " +
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
