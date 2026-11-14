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
/// or a small bar with the clock and the latest line.
public partial class LiveWindow : Window {
  static LiveWindow? shared;
  static string? pendingTab;
  readonly SessionController session = SessionController.Shared;
  bool updatingContext;
  bool paneOpen = true;
  double fullHeight = 660, fullWidth = 980;

  public LiveWindow() {
    InitializeComponent();
    DataContext = session;
    session.PropertyChanged += SessionChanged;
    AppSettings.Shared.PropertyChanged += SettingsChanged;
    session.Entries.CollectionChanged += EntriesChanged;
    session.Partials.CollectionChanged += (_, _) => { ScrollToEndIfFollowing(); UpdateSmallLine(); };
    Closing += (_, e) => {
      if (!App.ShuttingDown) {
        e.Cancel = true;
        Hide();
      }
    };
    ApplyFonts();
    UpdateAll();
    if (pendingTab != null) SelectTabNow(pendingTab);
  }

  public static void ShowShared() {
    shared ??= new LiveWindow();
    shared.ApplySize();
    if (!shared.IsVisible) shared.Show();
    if (shared.WindowState == WindowState.Minimized) shared.WindowState = WindowState.Normal;
    shared.Activate();
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
    var element = (FrameworkElement)w.Content;
    var dpi = VisualTreeHelper.GetDpi(w);
    var bmp = new RenderTargetBitmap((int)(element.ActualWidth * dpi.DpiScaleX), (int)(element.ActualHeight * dpi.DpiScaleY),
      dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
    var bg = new DrawingVisual();
    using (var dc = bg.RenderOpen()) {
      dc.DrawRectangle(w.Background ?? Brushes.White, null, new Rect(0, 0, element.ActualWidth, element.ActualHeight));
    }
    bmp.Render(bg);
    bmp.Render(element);
    var enc = new PngBitmapEncoder();
    enc.Frames.Add(BitmapFrame.Create(bmp));
    using var f = File.Create(path);
    enc.Save(f);
    Log.Write("snapshot written");
  }

  public static void SaveAs(bool markdown) {
    var s = SessionController.Shared;
    var dlg = new SaveFileDialog {
      FileName = s.IsRecording ? $"{s.ExportBaseName} (as of {DateTime.Now:HH-mm})" : s.ExportBaseName,
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

  // Updates

  void SessionChanged(object? sender, PropertyChangedEventArgs e) {
    switch (e.PropertyName) {
      case nameof(SessionController.ElapsedSeconds):
      case nameof(SessionController.Phase):
      case nameof(SessionController.PhaseText):
      case nameof(SessionController.Finished):
      case nameof(SessionController.LastTranscript):
        UpdateStatus();
        UpdateMinutes();
        break;
      case nameof(SessionController.Minutes):
      case nameof(SessionController.MinutesDraft):
      case nameof(SessionController.CanStartAIMinutes):
      case nameof(SessionController.CanRedoMinutes):
        UpdateMinutes();
        UpdateContextPane();
        break;
      case nameof(SessionController.Context):
        UpdateContextPane();
        break;
      case nameof(SessionController.AutoStopIn):
        AutoStopText.Text = session.AutoStopIn is int n ? $"No speech for a while: stopping and saving in {n} s." : "";
        break;
      case nameof(SessionController.Config):
        ApplyFonts();
        break;
    }
  }

  void SettingsChanged(object? sender, PropertyChangedEventArgs e) {
    if (e.PropertyName == nameof(AppSettings.LiveWindowSmall)) ApplySize();
    if (e.PropertyName == nameof(AppSettings.LiveFontSize)) ApplyFonts();
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
      _ => session.Finished != null ? "Saved" : "Kurzschrift",
    };
    OpenButton.Visibility = session.IsIdle && session.LastTranscript != null ? Visibility.Visible : Visibility.Collapsed;
    EmptyText.Text = session.Entries.Count > 0 || session.Partials.Count > 0 ? "" : session.Phase switch {
      Phase.Recording => "Listening… Text appears here as people speak.",
      Phase.Starting => session.PhaseText,
      Phase.Finishing => session.PhaseText,
      _ => "Start a meeting from the speech bubble in the notification area,\nor with Ctrl+Alt+T (translate) or Ctrl+Alt+R (transcribe).",
    };
    Title = session.IsRecording ? $"Kurzschrift · {session.ElapsedText}" : "Kurzschrift";
    UpdateSmallLine();
  }

  void ApplyFonts() {
    double size = AppSettings.Shared.LiveFontSize;
    bool translate = session.Config?.Mode == MeetingMode.Translate || session.Finished?.Config.Mode == MeetingMode.Translate;
    Resources["MainSize"] = size;
    Resources["OriginalSize"] = translate ? Math.Max(10, size - 3) : size;
    Resources["OriginalOpacity"] = translate ? 0.65 : 1.0;
    SmallLine.FontSize = size;
  }

  void ApplySize() {
    bool small = AppSettings.Shared.LiveWindowSmall;
    if (small) {
      if (Body.Visibility == Visibility.Visible && IsLoaded) {
        fullHeight = ActualHeight;
        fullWidth = ActualWidth;
      }
      Body.Visibility = Visibility.Collapsed;
      PaneButton.Visibility = Visibility.Collapsed;
      SmallLine.Visibility = Visibility.Visible;
      SizeToContent = SizeToContent.Height;
      Topmost = true;
      SizeButton.Content = "Expand";
      if (Width > 900) Width = 760;
    } else {
      Body.Visibility = Visibility.Visible;
      PaneButton.Visibility = Visibility.Visible;
      SmallLine.Visibility = Visibility.Collapsed;
      SizeToContent = SizeToContent.Manual;
      Topmost = false;
      SizeButton.Content = "▁ Bar";
      if (ActualHeight < 200) {
        Height = fullHeight;
        Width = fullWidth;
      }
    }
    UpdateSmallLine();
  }

  void UpdateSmallLine() {
    if (!AppSettings.Shared.LiveWindowSmall) return;
    var p = session.Partials.LastOrDefault();
    var e = session.Entries.LastOrDefault(x => !x.IsBookmark);
    SmallLine.Text = p != null ? (p.Translation ?? p.Original) : e != null ? (e.Translation ?? e.Original) : "";
  }

  void EntriesChanged(object? sender, NotifyCollectionChangedEventArgs e) {
    UpdateStatus();
    ScrollToEndIfFollowing();
  }

  ScrollViewer? scroller;
  bool following = true;

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
        if (args.ExtentHeightChange == 0) following = scroller.VerticalOffset >= scroller.ScrollableHeight - 40;
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

  // Minutes pane

  void UpdateMinutes() {
    var m = session.Minutes;
    bool editing = session.MinutesDraft != null;
    if (!editing) MinutesBox.Text = m?.Text ?? "";
    MinutesBox.IsReadOnly = !editing;
    EditMinutesButton.Content = editing ? "Done" : "Edit";
    EditMinutesButton.IsEnabled = m != null;
    CancelEditButton.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
    AIMinutesButton.Visibility = session.CanStartAIMinutes ? Visibility.Visible : Visibility.Collapsed;
    UpdateMinutesButton.Visibility = m is { UsesAI: true } && session.IsRecording ? Visibility.Visible : Visibility.Collapsed;
    MinutesStatus.Text = m == null ? "" : MinutesStatusText(m);
    MinutesEmpty.Text = !string.IsNullOrEmpty(MinutesBox.Text) || editing ? "" :
      m == null ? "Minutes appear here during a meeting." :
      !m.UsesAI ? "Your notes: click Edit to write them; they're saved with the transcript." +
                  (session.CanAsk ? "" : "\n\nFor AI minutes, add an API key in Settings → Minutes.") :
      session.Context.IsEmpty ? "The first minutes arrive after a few minutes of talking.\n\nTip: tell Kurzschrift who's in the meeting and what it's about in the Context tab."
                              : "The first minutes arrive after a few minutes of talking.";
  }

  static string MinutesStatusText(MinutesState m) {
    if (!m.UsesAI) return "Notes";
    if (m.IsUpdating) return "Updating…";
    if (!string.IsNullOrEmpty(m.Error)) return m.Error!;
    var parts = new List<string>();
    if (m.UpdatedAt is { } at) parts.Add($"Updated {at.ToLocalTime():HH:mm}");
    if (m.EditsPending) parts.Add("your edits go into the next update");
    else if (m.NextUpdate is { } next && SessionController.Shared.IsRecording) parts.Add($"next at {next.ToLocalTime():HH:mm}");
    if (m.Model.Length > 0) parts.Add(m.Model);
    if (m.Cost is double c && c > 0) parts.Add($"about ${c:0.00} so far");
    return string.Join(" · ", parts);
  }

  void MinutesBox_TextChanged(object sender, TextChangedEventArgs e) {
    if (session.MinutesDraft != null && !MinutesBox.IsReadOnly) session.MinutesDraft = MinutesBox.Text;
  }

  void EditMinutes_Click(object sender, RoutedEventArgs e) {
    if (session.MinutesDraft == null) {
      session.BeginEditingMinutes();
      MinutesBox.Focus();
    } else {
      session.FinishEditingMinutes();
    }
    UpdateMinutes();
  }

  void CancelEdit_Click(object sender, RoutedEventArgs e) {
    session.CancelEditingMinutes();
    UpdateMinutes();
  }

  void AIMinutes_Click(object sender, RoutedEventArgs e) => session.StartAIMinutes();
  void UpdateMinutes_Click(object sender, RoutedEventArgs e) => session.UpdateMinutesNow();

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
      _ => "Fill this in before or during a meeting.",
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
      Clipboard.SetText(entry.Translation is { } t ? $"{entry.Original}\n{t}" : entry.Original);
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
