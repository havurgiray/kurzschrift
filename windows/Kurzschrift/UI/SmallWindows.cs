using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Kurzschrift;

/// Asks before downloading the models a meeting needs (about 0.7 GB the first time) and shows progress.
public static class DownloadWindow {
  public static async Task<bool> Run(List<ModelStore.Model> models) {
    long total = models.Sum(m => m.ApproxBytes);
    var list = string.Join("\n", models.Select(m => $"•  {m.Title}: about {Mb(m.ApproxBytes)}"));
    var text = new TextBlock {
      Text = $"Kurzschrift needs these models before it can start. They run on this PC; nothing you say is uploaded.\n\n{list}\n\nTotal about {Mb(total)}. They're downloaded once and kept in {Paths.Models}.",
      TextWrapping = TextWrapping.Wrap, MaxWidth = 520,
    };
    var bar = new ProgressBar { Height = 8, Margin = new Thickness(0, 14, 0, 4), Visibility = Visibility.Collapsed, Maximum = 1 };
    var status = new TextBlock { Opacity = 0.7, FontSize = 12 };
    var ok = new Button { Content = "Download", IsDefault = true, Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(0, 14, 8, 0) };
    var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(0, 14, 0, 0) };
    var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
    buttons.Children.Add(ok);
    buttons.Children.Add(cancel);
    var panel = new StackPanel { Margin = new Thickness(18) };
    panel.Children.Add(new TextBlock { Text = "Download speech models", FontSize = 17, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
    panel.Children.Add(text);
    panel.Children.Add(bar);
    panel.Children.Add(status);
    panel.Children.Add(buttons);
    var w = new Window {
      Title = "Kurzschrift", Content = panel, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize,
      WindowStartupLocation = WindowStartupLocation.CenterScreen, Topmost = true,
    };
    var cts = new CancellationTokenSource();
    var done = new TaskCompletionSource<bool>();
    bool downloading = false;
    ok.Click += async (_, _) => {
      downloading = true;
      ok.IsEnabled = false;
      bar.Visibility = Visibility.Visible;
      var progress = new Progress<(long Done, long Total, string Title)>(p => {
        bar.Value = p.Total > 0 ? (double)p.Done / p.Total : 0;
        status.Text = p.Title.Length > 0 ? $"{p.Title}: {Mb(p.Done)} of {Mb(p.Total)}" : "Done";
      });
      try {
        await ModelStore.Download(models, progress, cts.Token);
        done.TrySetResult(true);
        w.Close();
      } catch (OperationCanceledException) {
        done.TrySetResult(false);
      } catch (Exception e) {
        Log.Write($"model download failed: {e.Message}");
        status.Text = $"The download failed: {e.Message}";
        ok.IsEnabled = true;
        ok.Content = "Try Again";
        downloading = false;
      }
    };
    cancel.Click += (_, _) => {
      cts.Cancel();
      done.TrySetResult(false);
      w.Close();
    };
    w.Closed += (_, _) => {
      if (downloading) cts.Cancel();
      done.TrySetResult(false);
    };
    w.Show();
    w.Activate();
    return await done.Task;
  }

  static string Mb(long bytes) => bytes >= 1_000_000_000 ? $"{bytes / 1e9:0.0} GB" : $"{bytes / 1e6:0} MB";
}

/// TV-style captions along the bottom (or top) of the screen while recording. Clicks pass through.
public sealed class SubtitlesWindow : Window {
  static SubtitlesWindow? shared;
  readonly TextBlock text = new() { Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
  readonly Border border;

  SubtitlesWindow() {
    WindowStyle = WindowStyle.None;
    AllowsTransparency = true;
    Background = Brushes.Transparent;
    Topmost = true;
    ShowInTaskbar = false;
    ShowActivated = false;
    ResizeMode = ResizeMode.NoResize;
    SizeToContent = SizeToContent.Height;
    border = new Border {
      Background = new SolidColorBrush(Color.FromArgb(190, 0, 0, 0)), CornerRadius = new CornerRadius(10),
      Padding = new Thickness(18, 10, 18, 12), Child = text, HorizontalAlignment = HorizontalAlignment.Center,
    };
    Content = border;
    SourceInitialized += (_, _) => {
      var h = new WindowInteropHelper(this).Handle;
      SetWindowLong(h, -20, GetWindowLong(h, -20) | 0x20 | 0x80000 | 0x80);  // transparent, layered, tool window
    };
    var s = SessionController.Shared;
    s.Entries.CollectionChanged += (_, _) => Refresh();
    s.Partials.CollectionChanged += (_, _) => Refresh();
    s.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(SessionController.Phase)) Update(); };
    refresher.Tick += (_, _) => Refresh();
    IsVisibleChanged += (_, _) => { if (IsVisible) refresher.Start(); else refresher.Stop(); };
  }

  readonly System.Windows.Threading.DispatcherTimer refresher = new() { Interval = TimeSpan.FromMilliseconds(300) };

  void Refresh() {
    var s = SessionController.Shared;
    var p = s.Partials.LastOrDefault();
    var e = s.Entries.LastOrDefault(x => !x.IsBookmark);
    var line = p != null ? (p.Translation ?? p.Original) : e != null ? (e.Translation ?? e.Original) : "";
    if (line.Length > 220) line = "…" + line[^220..];
    text.Text = line;
    text.FontSize = AppSettings.Shared.SubtitleFontSize;
    border.Visibility = line.Length > 0 ? Visibility.Visible : Visibility.Hidden;
    Place();
  }

  void Place() {
    var area = SystemParameters.WorkArea;
    Width = Math.Min(1100, area.Width - 80);
    Left = area.Left + (area.Width - Width) / 2;
    Top = AppSettings.Shared.SubtitlesAtTop ? area.Top + 40 : area.Bottom - ActualHeight - 40;
  }

  public static void Update() {
    bool on = AppSettings.Shared.Subtitles && SessionController.Shared.IsRecording;
    if (on) {
      shared ??= new SubtitlesWindow();
      shared.Show();
      shared.Refresh();
    } else {
      shared?.Hide();
    }
  }

  public static void Snapshot(string path) {
    if (shared is not { IsVisible: true } w) return;
    var el = (FrameworkElement)w.Content;
    var bmp = new RenderTargetBitmap((int)el.ActualWidth, (int)el.ActualHeight, 96, 96, PixelFormats.Pbgra32);
    bmp.Render(el);
    var enc = new PngBitmapEncoder();
    enc.Frames.Add(BitmapFrame.Create(bmp));
    using var f = File.Create(path);
    enc.Save(f);
  }

  [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int index);
  [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int index, int value);
}
