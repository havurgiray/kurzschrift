using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace Kurzschrift;

/// The meeting window: the transcript as people speak, with Minutes, Ask and Context beside it;
/// or a small bar with the clock and the latest line. It floats above other windows, like the
/// Mac's panel, and doesn't take the keyboard when a meeting starts.
public partial class LiveWindow : Window {
  static LiveWindow? shared;
  static string? pendingTab;
  readonly SessionController session = SessionController.Shared;
  readonly System.Windows.Threading.DispatcherTimer captionTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
  bool updatingContext;
  bool paneOpen = true;
  /// Which shape the window has now (null before the first ApplySize), and whether we're moving it ourselves.
  bool? small;
  bool placing;
  bool wasEditing;

  public LiveWindow() {
    InitializeComponent();
    DataContext = session;
    session.PropertyChanged += SessionChanged;
    AppSettings.Shared.PropertyChanged += SettingsChanged;
    session.Entries.CollectionChanged += EntriesChanged;
    session.Partials.CollectionChanged += (_, _) => { ScrollToEndIfFollowing(); UpdateStatus(); };
    Closing += (_, e) => {
      if (!App.ShuttingDown) {
        e.Cancel = true;
        Hide();
      }
    };
    LocationChanged += (_, _) => RememberBounds();
    SizeChanged += (_, _) => RememberBounds();
    captionTimer.Tick += (_, _) => UpdateSmallLine();
    IsVisibleChanged += (_, _) => UpdateCaptionTimer();
    ApplyFonts();
    UpdateAll();
    if (pendingTab != null) SelectTabNow(pendingTab);
  }

  /// `activate` false: a meeting started by itself (shortcut, call prompt, link) shows the window
  /// without taking the keyboard away from the app you're in.
  public static void ShowShared(bool activate = true) {
    shared ??= new LiveWindow();
    shared.ApplySize();
    if (!shared.IsVisible) {
      shared.ShowActivated = activate;
      shared.Show();
    }
    if (shared.WindowState == WindowState.Minimized) shared.WindowState = WindowState.Normal;
    if (activate) shared.Activate();
  }

  public static void SelectTab(string tab) {
    pendingTab = tab;
    shared?.SelectTabNow(tab);
  }

  void SelectTabNow(string tab) {
    foreach (TabItem item in SideTabs.Items) {
      if ((string)item.Tag == tab) SideTabs.SelectedItem = item;
    }
    if (!paneOpen) TogglePane();
  }

  /// Renders the window's own content to a PNG (developer link), never the screen.
  public static void Snapshot(string path) {
    var w = shared;
    if (w == null || !w.IsVisible) {
      Log.Write("snapshot: no live window");
      return;
    }
    w.UpdateLayout();
    Render((FrameworkElement)w.Content, w, path);
  }

  /// Draws one of the app's own views into a PNG (never the screen). Through a brush, so the view
  /// lands at the picture's corner whatever margin it has in its window.
  public static void Render(FrameworkElement element, Window window, string path) {
    var dpi = VisualTreeHelper.GetDpi(window);
    var bmp = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(element.ActualHeight * dpi.DpiScaleY),
      dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
    var picture = new DrawingVisual();
    using (var dc = picture.RenderOpen()) {
      var area = new Rect(0, 0, element.ActualWidth, element.ActualHeight);
      dc.DrawRectangle(window.Background ?? Brushes.White, null, area);
      dc.DrawRectangle(new VisualBrush(element) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, area);
    }
    bmp.Render(picture);
    var enc = new PngBitmapEncoder();
    enc.Frames.Add(BitmapFrame.Create(bmp));
    using var f = File.Create(path);
    enc.Save(f);
    Log.Write("snapshot written");
  }

  public static void SaveAs(bool markdown) {
    var s = SessionController.Shared;
    var dlg = new SaveFileDialog {
      FileName = s.IsRecording ? FormattableString.Invariant($"{s.ExportBaseName} (as of {DateTime.Now:HH-mm})") : s.ExportBaseName,
      DefaultExt = markdown ? ".md" : ".txt",
      Filter = markdown ? "Markdown|*.md" : "Text|*.txt",
      InitialDirectory = s.ExportFolder ?? "",
    };
    if (dlg.ShowDialog() != true) return;
    try {
      File.WriteAllText(dlg.FileName, markdown ? s.ExportMarkdown() : s.ExportText());
      Log.Write($"exported {(markdown ? "markdown" : "text")} transcript");
    } catch (Exception e) {
      MessageBox.Show(e.Message, "Kurzschrift");
    }
  }

  /// The clipboard can be held by another app for a moment; that must not become an error dialog.
  public static void Copy(string text) {
    try {
      Clipboard.SetText(text);
    } catch (Exception e) {
      Log.Write($"copying to the clipboard failed: {e.Message}");
    }
  }

  // Updates

  void SessionChanged(object? sender, PropertyChangedEventArgs e) {
    switch (e.PropertyName) {
      case nameof(SessionController.ElapsedSeconds):
        UpdateStatus();
        UpdateMinutesStatus();
        break;
      case nameof(SessionController.Phase):
      case nameof(SessionController.PhaseText):
      case nameof(SessionController.Finished):
      case nameof(SessionController.LastTranscript):
        UpdateStatus();
        UpdateMinutes();
        UpdateContextPane();
        break;
      case nameof(SessionController.Minutes):
      case nameof(SessionController.MinutesDraft):
      case nameof(SessionController.CanStartAIMinutes):
      case nameof(SessionController.CanRedoMinutes):
      case nameof(SessionController.CanRetryMinutes):
      case nameof(SessionController.CanEditMinutes):
        UpdateMinutes();
        UpdateContextPane();
        break;
      case nameof(SessionController.Context):
        UpdateContextPane();
        UpdateMinutes();
        break;
      case nameof(SessionController.AutoStopIn):
        AutoStopText.Text = session.AutoStopIn is int n ? $"No speech: stopping in {n} s" : "";
        UpdateSmallLine();
        break;
      case nameof(SessionController.Config):
        ApplyFonts();
        UpdateStatus();
        break;
    }
  }

  void SettingsChanged(object? sender, PropertyChangedEventArgs e) {
    if (e.PropertyName == nameof(AppSettings.LiveWindowSmall)) ApplySize();
    if (e.PropertyName == nameof(AppSettings.LiveFontSize)) ApplyFonts();
    if (e.PropertyName == nameof(AppSettings.GlobalShortcuts)) UpdateStatus();
  }

  void UpdateAll() {
    UpdateStatus();
    UpdateMinutes();
    UpdateContextPane();
    ApplySize();
  }

  void UpdateStatus() {
    StatusText.Text = session.Phase switch {
      Phase.Recording => session.ElapsedText,
      Phase.Starting or Phase.Finishing => session.PhaseText,
      _ => session.Finished != null ? "Saved" : "Not recording",
    };
    ModeText.Text = session.IsRecording ? session.ModeDescription : "";
    OpenButton.Visibility = session.IsIdle && session.LastTranscript != null ? Visibility.Visible : Visibility.Collapsed;
    EmptyText.Text = session.Entries.Count > 0 || session.Partials.Count > 0 ? "" : session.Phase switch {
      Phase.Recording => "The transcript appears here as people speak.",
      Phase.Starting => session.PhaseText,
      Phase.Finishing => session.PhaseText,
      _ => "Start a meeting with the Start button or from the speech bubble in the notification area" +
           (AppSettings.Shared.GlobalShortcuts ? ",\nor with Ctrl+Alt+T (translate) or Ctrl+Alt+R (transcribe)." : "."),
    };
    BookmarkButton.ToolTip = AppSettings.Shared.GlobalShortcuts ? "Mark this moment as important (Ctrl+Alt+B)" : "Mark this moment as important";
    Title = session.IsRecording ? $"Kurzschrift · {session.ElapsedText}" : "Kurzschrift";
    UpdateSmallLine();
  }

  void ApplyFonts() {
    double size = AppSettings.Shared.LiveFontSize;
    bool translate = session.Config?.Mode == MeetingMode.Translate || session.Finished?.Config.Mode == MeetingMode.Translate;
    Resources["MainSize"] = size;
    Resources["OriginalSize"] = translate ? Math.Max(10, size - 3) : size;
    Resources["OriginalOpacity"] = translate ? 0.65 : 1.0;
    // The headings above each line, and everything in the side pane, follow the text size too.
    Resources["HeadingSize"] = Math.Max(10, size - 3);
    Resources["SideSize"] = Math.Max(10, size - 1);
    MinutesView.FontSize = MinutesBox.FontSize = MinutesEmpty.FontSize = QuestionBox.FontSize = Math.Max(10, size - 1);
    SmallLine.FontSize = size;
    SmallOriginal.FontSize = Math.Max(10, size - 2);
  }

  // Size and place: remembered across launches, separately for the window and the bar

  static bool ParseBounds(string text, out Rect bounds) {
    bounds = default;
    var parts = text.Split(',');
    var n = new double[4];
    if (parts.Length != 4) return false;
    for (int i = 0; i < 4; i++)
      if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out n[i])) return false;
    if (n[2] < 200 || n[3] < 40) return false;
    bounds = new Rect(n[0], n[1], n[2], n[3]);
    // Only if it is still on a screen (a monitor may have gone since).
    var screens = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
      SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
    var visible = Rect.Intersect(screens, bounds);
    return !visible.IsEmpty && visible.Width >= 100 && visible.Height >= 40;
  }

  void RememberBounds() {
    if (placing || small == null || !IsLoaded || !IsVisible || WindowState != WindowState.Normal) return;
    var text = FormattableString.Invariant($"{Left:0},{Top:0},{ActualWidth:0},{ActualHeight:0}");
    if (small == true) AppSettings.Shared.LiveBarBounds = text; else AppSettings.Shared.LiveWindowBounds = text;
  }

  void ApplySize() {
    bool wanted = AppSettings.Shared.LiveWindowSmall;
    if (small == wanted) {
      UpdateSmallLine();
      return;
    }
    RememberBounds();  // of the shape we're leaving, so switching back returns to where it was
    placing = true;
    small = wanted;
    if (wanted) {
      Body.Visibility = Visibility.Collapsed;
      PaneButton.Visibility = Visibility.Collapsed;
      ModeText.Visibility = Visibility.Collapsed;
      SmallCaption.Visibility = Visibility.Visible;
      MinHeight = 0;
      SizeToContent = SizeToContent.Height;
      SizeButton.Content = "Expand";
      SizeButton.ToolTip = "Back to the full window";
      if (ParseBounds(AppSettings.Shared.LiveBarBounds, out var bar)) {
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = bar.Left;
        Top = bar.Top;
        Width = bar.Width;
      } else if (Width > 900) {
        Width = 760;
      }
    } else {
      Body.Visibility = Visibility.Visible;
      PaneButton.Visibility = Visibility.Visible;
      ModeText.Visibility = Visibility.Visible;
      SmallCaption.Visibility = Visibility.Collapsed;
      SizeToContent = SizeToContent.Manual;
      MinHeight = 260;
      SizeButton.Content = "▁ Bar";
      SizeButton.ToolTip = "Shrink to a small bar. Recording, transcript and minutes carry on.";
      if (ParseBounds(AppSettings.Shared.LiveWindowBounds, out var full)) {
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = full.Left;
        Top = full.Top;
        Width = full.Width;
        Height = Math.Max(full.Height, 260);
      } else {
        Width = 980;
        Height = 660;
      }
    }
    placing = false;
    UpdateCaptionTimer();
    UpdateSmallLine();
  }

  void UpdateCaptionTimer() {
    // The bar's caption follows the translation of the line in progress, which changes without the lists changing.
    if (small == true && IsVisible) captionTimer.Start(); else captionTimer.Stop();
  }

  /// The bar's caption, like the Mac's: what is being said right now; in a translated meeting the
  /// original above, smaller, and the translation below.
  void UpdateSmallLine() {
    if (small != true) return;
    string? original = null;
    string line;
    if (session.AutoStopIn is int n) {
      line = $"No speech: stopping in {n} s";
    } else if (session.IsRecording) {
      if (session.CurrentCaption is { } now) {
        if (session.Config?.Mode == MeetingMode.Translate) {
          original = Tail(now.Original, 110);
          line = now.Translation ?? "…";
        } else {
          line = now.Original;
        }
      } else {
        line = "Listening…";
      }
    } else if (session.IsIdle && session.LastTranscript != null) {
      line = Path.GetFileName(session.LastTranscript);
    } else {
      line = "";
    }
    line = Tail(line, 170);
    if (SmallLine.Text != line) SmallLine.Text = line;
    if (SmallOriginal.Text != (original ?? "")) SmallOriginal.Text = original ?? "";
    SmallOriginal.Visibility = original != null ? Visibility.Visible : Visibility.Collapsed;
  }

  /// The end of a long line: that's where the talking is.
  static string Tail(string text, int length) => text.Length > length ? "…" + text[^length..] : text;

  void EntriesChanged(object? sender, NotifyCollectionChangedEventArgs e) {
    if (e.Action == NotifyCollectionChangedAction.Reset) SetFollowing(true);  // a new meeting, or the finished transcript
    UpdateStatus();
    ScrollToEndIfFollowing();
  }

  ScrollViewer? scroller;
  bool following = true;

  void SetFollowing(bool value) {
    following = value;
    JumpButton.Visibility = !value && session.Entries.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
  }

  void ScrollToEndIfFollowing() {
    scroller ??= FindScrollViewer(EntryList);
    if (scroller == null) return;
    if (following) Dispatcher.BeginInvoke(() => scroller.ScrollToEnd(), System.Windows.Threading.DispatcherPriority.Background);
  }

  protected override void OnContentRendered(EventArgs e) {
    base.OnContentRendered(e);
    scroller = FindScrollViewer(EntryList);
    if (scroller != null) {
      scroller.ScrollChanged += (_, args) => {
        // Follow new lines only while you're at the bottom; reading further up isn't interrupted.
        if (args.ExtentHeightChange == 0 && args.ViewportHeightChange == 0)
          SetFollowing(scroller.VerticalOffset >= scroller.ScrollableHeight - 40);
      };
    }
  }

  static ScrollViewer? FindScrollViewer(DependencyObject d) {
    if (d is ScrollViewer sv) return sv;
    for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++) {
      var found = FindScrollViewer(VisualTreeHelper.GetChild(d, i));
      if (found != null) return found;
    }
    return null;
  }

  void Jump_Click(object sender, RoutedEventArgs e) {
    SetFollowing(true);
    scroller?.ScrollToEnd();
  }

  // Minutes pane

  void UpdateMinutes() {
    var m = session.Minutes;
    bool editing = session.MinutesDraft != null;
    bool notesOnly = m is { UsesAI: false };
    MinutesTitle.Text = m == null ? "" : notesOnly ? "Notes" : "Minutes";
    if (editing && !wasEditing) MinutesBox.Text = session.MinutesDraft ?? "";
    wasEditing = editing;
    var text = m?.Text ?? "";
    if (!editing && MinutesView.Markdown != text) MinutesView.Markdown = text;
    MinutesBox.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
    MinutesView.Visibility = editing ? Visibility.Collapsed : Visibility.Visible;
    EditMinutesButton.Content = editing ? "Done" : "Edit";
    // Not while final minutes are being written: they would replace what you typed, or your "Done" them.
    EditMinutesButton.IsEnabled = editing || session.CanEditMinutes;
    CancelEditButton.Visibility = Show(editing);
    CopyMinutesButton.Visibility = Show(!editing);
    CopyMinutesButton.IsEnabled = text.Length > 0;
    CopyMinutesButton.ToolTip = notesOnly ? "Copy notes" : "Copy minutes";
    AIMinutesButton.Visibility = Show(!editing && session.CanStartAIMinutes);
    RetryMinutesButton.Visibility = Show(!editing && session.CanRetryMinutes);
    UpdateMinutesButton.Visibility = Show(!editing && m is { UsesAI: true } && session.IsRecording);
    UpdateMinutesButton.IsEnabled = m?.IsUpdating != true;
    // An error stays in view while the next try is running.
    MinutesError.Text = m?.Error ?? "";
    MinutesError.Visibility = Show(!string.IsNullOrEmpty(m?.Error));
    UpdateMinutesStatus();
    MinutesEmpty.Text = text.Length > 0 || editing ? "" :
      m == null ? "Minutes appear here during a meeting." :
      notesOnly && session.CanStartAIMinutes ? (session.IsRecording
        ? $"Press AI Minutes to have minutes written from the transcript so far and updated every {AppSettings.Shared.MinutesIntervalMinutes} min, or take your own notes with Edit."
        : "Press AI Minutes to have minutes written from the whole transcript, or take your own notes with Edit.") :
      notesOnly ? "Take notes with Edit; they're saved with the transcript. Add an API key in Settings → Minutes and an AI Minutes button appears here." :
      "The first minutes appear after the first update interval, once enough has been said. You can also start them yourself with Edit." +
      (session.Context.IsEmpty ? " Under Context, say who's in the meeting and what it's about, and the minutes use it." : "");
  }

  static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

  void UpdateMinutesStatus() {
    var m = session.Minutes;
    bool editing = session.MinutesDraft != null;
    MinutesStatus.Text = m == null ? "" : MinutesStatusText(m, editing, session.IsRecording);
  }

  static string MinutesStatusText(MinutesState m, bool editing, bool recording) {
    if (!m.UsesAI) return editing ? "Your notes are saved with the transcript." : "";
    var parts = new List<string>();
    if (m.IsUpdating) parts.Add("Updating…");
    else if (recording && editing) parts.Add("Updates wait while you edit");
    else if (recording && m.EditsPending) parts.Add("Your edits go into the next update");
    else if (m.UpdatedAt is { } at) parts.Add(FormattableString.Invariant($"Updated {at.ToLocalTime():HH:mm}"));
    if (recording && !editing && !m.IsUpdating && m.NextUpdate is { } next) {
      int seconds = Math.Max(0, (int)(next - DateTimeOffset.Now).TotalSeconds);
      parts.Add(FormattableString.Invariant($"next in {seconds / 60}:{seconds % 60:00}"));
    }
    if (m.Model.Length > 0) parts.Add(m.Model);
    if (m.Cost is double c && c > 0) parts.Add(c < 0.01 ? "< $0.01" : FormattableString.Invariant($"≈ ${c:0.00}"));
    return string.Join(" · ", parts);
  }

  void MinutesBox_TextChanged(object sender, TextChangedEventArgs e) {
    if (session.MinutesDraft != null && wasEditing) session.MinutesDraft = MinutesBox.Text;
  }

  void EditMinutes_Click(object sender, RoutedEventArgs e) {
    if (session.MinutesDraft == null) {
      session.BeginEditingMinutes();
      UpdateMinutes();
      if (session.MinutesDraft != null) MinutesBox.Focus();
    } else {
      session.FinishEditingMinutes();
      UpdateMinutes();
    }
  }

  void CancelEdit_Click(object sender, RoutedEventArgs e) {
    session.CancelEditingMinutes();
    UpdateMinutes();
  }

  void AIMinutes_Click(object sender, RoutedEventArgs e) => session.StartAIMinutes();
  void RetryMinutes_Click(object sender, RoutedEventArgs e) => session.RetryMinutes();
  void UpdateMinutes_Click(object sender, RoutedEventArgs e) => session.UpdateMinutesNow();
  void CopyMinutes_Click(object sender, RoutedEventArgs e) => Copy(session.Minutes?.Text ?? "");

  // Ask pane

  void Ask_Click(object sender, RoutedEventArgs e) {
    session.Ask(QuestionBox.Text);
    QuestionBox.Clear();
  }

  void QuestionBox_KeyDown(object sender, KeyEventArgs e) {
    if (e.Key == Key.Enter) Ask_Click(sender, e);
  }

  // Context pane

  void UpdateContextPane() {
    updatingContext = true;
    if (!ParticipantsBox.IsKeyboardFocusWithin) ParticipantsBox.Text = session.Context.Participants;
    if (!AboutBox.IsKeyboardFocusWithin) AboutBox.Text = session.Context.About;
    updatingContext = false;
    RedoMinutesButton.Visibility = session.CanRedoMinutes ? Visibility.Visible : Visibility.Collapsed;
    bool finishing = session.Phase is Phase.Finishing or Phase.Starting;
    ParticipantsBox.IsEnabled = AboutBox.IsEnabled = !finishing;
    ContextNote.Text = session.Phase switch {
      Phase.Recording => "Used by the minutes and your questions from their next update on, and saved with the transcript.",
      Phase.Idle when session.Finished != null => session.CanRedoMinutes
        ? "The context changed since the minutes were written: Redo Minutes writes them again with it."
        : "Saved with the transcript.",
      Phase.Idle => "Fill this in before the meeting or during it: it is used by the minutes and your questions, and saved with the transcript.",
      _ => "",
    };
  }

  void Context_TextChanged(object sender, TextChangedEventArgs e) {
    if (updatingContext) return;
    session.Context = new MeetingContext(ParticipantsBox.Text, AboutBox.Text);
  }

  void Redo_Click(object sender, RoutedEventArgs e) => session.RedoMinutes();

  void SideTabs_SelectionChanged(object sender, SelectionChangedEventArgs e) {
    if (e.Source == SideTabs && SideTabs.SelectedItem is TabItem t) pendingTab = (string)t.Tag;
  }

  // Buttons

  /// The same entries as the notification area menu.
  void Start_Click(object sender, RoutedEventArgs e) {
    var settings = AppSettings.Shared;
    var menu = new ContextMenu { PlacementTarget = StartButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
    void Add(string title, MeetingMode mode, bool second = false) {
      var item = new MenuItem { Header = title };
      item.Click += (_, _) => _ = session.Start(mode, secondLanguage: second);
      menu.Items.Add(item);
    }
    Add(settings.MenuTitle(MeetingMode.Translate), MeetingMode.Translate);
    Add(settings.MenuTitle(MeetingMode.Transcribe), MeetingMode.Transcribe);
    if (settings.SecondTranscribeTitle is { } title) Add(title, MeetingMode.Transcribe, second: true);
    menu.IsOpen = true;
  }

  void Bookmark_Click(object sender, RoutedEventArgs e) => session.AddBookmark();
  async void Stop_Click(object sender, RoutedEventArgs e) => await session.Stop();
  void Keep_Click(object sender, RoutedEventArgs e) => session.KeepRecording();
  void Open_Click(object sender, RoutedEventArgs e) {
    if (session.LastTranscript != null) TrayIcon.Open(session.LastTranscript);
  }
  void Size_Click(object sender, RoutedEventArgs e) => AppSettings.Shared.LiveWindowSmall = !AppSettings.Shared.LiveWindowSmall;
  void Pane_Click(object sender, RoutedEventArgs e) => TogglePane();

  void TogglePane() {
    paneOpen = !paneOpen;
    PaneColumn.Width = paneOpen ? new GridLength(360) : new GridLength(0);
    SplitterColumn.Width = paneOpen ? new GridLength(6) : new GridLength(0);
    SideTabs.Visibility = paneOpen ? Visibility.Visible : Visibility.Collapsed;
  }

  void Rename_Click(object sender, RoutedEventArgs e) {
    if ((sender as FrameworkElement)?.DataContext is not LiveEntry entry || entry.Speaker == null) return;
    if (session.Finished == null) {
      MessageBox.Show("Speakers can be renamed once the meeting is saved.", "Kurzschrift");
      return;
    }
    var result = RenameDialog.Ask(this, entry.Speaker, session.Context.Names);
    if (result is { } r) session.RenameSpeaker(entry.Speaker, r.Name, r.Remember);
  }

  void CopyEntry_Click(object sender, RoutedEventArgs e) {
    if ((sender as FrameworkElement)?.DataContext is LiveEntry entry)
      Copy(entry.Translation is { } t ? $"{entry.Original}\n{t}" : entry.Original);
  }
}

/// Asks for a speaker's real name, suggesting the participants from the Context tab.
public static class RenameDialog {
  public static (string Name, bool Remember)? Ask(Window owner, string current, IEnumerable<string> suggestions) {
    var box = new ComboBox { IsEditable = true, Text = current.StartsWith("Speaker ") ? "" : current, MinWidth = 260 };
    foreach (var s in suggestions) box.Items.Add(s);
    var remember = new CheckBox { Content = "Remember this voice for later meetings", IsChecked = true, Margin = new Thickness(0, 10, 0, 0) };
    var ok = new Button { Content = "Rename", IsDefault = true, Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(0, 14, 8, 0) };
    var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(0, 14, 0, 0) };
    var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
    buttons.Children.Add(ok);
    buttons.Children.Add(cancel);
    var panel = new StackPanel { Margin = new Thickness(16) };
    panel.Children.Add(new TextBlock { Text = $"Rename \"{current}\" to:", Margin = new Thickness(0, 0, 0, 6) });
    panel.Children.Add(box);
    panel.Children.Add(remember);
    panel.Children.Add(buttons);
    var w = new Window {
      Title = "Rename Speaker", Content = panel, SizeToContent = SizeToContent.WidthAndHeight, Owner = owner,
      WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
    };
    ok.Click += (_, _) => w.DialogResult = true;
    w.Loaded += (_, _) => box.Focus();
    return w.ShowDialog() == true && box.Text.Trim().Length > 0 ? (box.Text.Trim(), remember.IsChecked == true) : null;
  }
}

public sealed class VisibleIfConverter : IValueConverter {
  public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch {
    null => Visibility.Collapsed,
    bool b => b ? Visibility.Visible : Visibility.Collapsed,
    string s => s.Length > 0 ? Visibility.Visible : Visibility.Collapsed,
    int i => i != 0 ? Visibility.Visible : Visibility.Collapsed,
    _ => Visibility.Visible,
  };
  public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class VisibleIfNotConverter : IValueConverter {
  public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
    new VisibleIfConverter().Convert(value, targetType, parameter, culture) is Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
  public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
