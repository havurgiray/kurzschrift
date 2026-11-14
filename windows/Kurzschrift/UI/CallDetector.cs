using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Kurzschrift;

/// Notices when a meeting app starts using the microphone (a call began) and offers to start
/// recording; stops the recording when that app lets go of the microphone (the call ended).
/// Reads Windows' own record of which apps use the microphone (Settings → Privacy → Microphone);
/// needs no permission and hears no audio.
public static class CallDetector {
  static readonly (string Match, string Name)[] MeetingApps = {
    ("MSTeams", "Teams"), ("Teams.exe", "Teams"), ("ms-teams.exe", "Teams"), ("Zoom.exe", "Zoom"),
    ("CiscoCollabHost.exe", "Webex"), ("webex", "Webex"), ("atmgr.exe", "Webex"), ("Skype", "Skype"),
    ("slack.exe", "Slack"), ("Discord.exe", "Discord"),
  };
  const string Store = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";

  static DispatcherTimer? timer;
  static string? callApp;
  static bool recordingDuringCall;
  static Window? prompt;

  public static void Apply() {
    timer?.Stop();
    timer = null;
    if (!AppSettings.Shared.DetectCalls) return;
    callApp = AppUsingMicrophone();  // a call already running at launch doesn't prompt
    timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
    timer.Tick += (_, _) => Poll();
    timer.Start();
  }

  static void Poll() {
    var s = SessionController.Shared;
    var app = AppUsingMicrophone();
    if (callApp != null && s.IsRecording) recordingDuringCall = true;
    if (app != null && callApp == null) {
      callApp = app;
      recordingDuringCall = s.IsRecording;
      Log.Write($"call started in {app}");
      if (s.IsIdle) ShowPrompt(app);
    } else if (app == null && callApp != null) {
      Log.Write($"call ended in {callApp}");
      callApp = null;
      prompt?.Close();
      if (recordingDuringCall && s.IsRecording && AppSettings.Shared.StopWhenCallEnds) _ = s.Stop();
      recordingDuringCall = false;
    }
  }

  /// The meeting app that's using the microphone right now, if any.
  static string? AppUsingMicrophone() {
    try {
      using var root = Registry.CurrentUser.OpenSubKey(Store);
      if (root == null) return null;
      foreach (var (path, key) in Keys(root)) {
        if (path.Contains("Kurzschrift", StringComparison.OrdinalIgnoreCase)) continue;
        if (key.GetValue("LastUsedTimeStop") is long stop && stop == 0 && key.GetValue("LastUsedTimeStart") is long start && start > 0) {
          foreach (var (match, name) in MeetingApps)
            if (path.Contains(match, StringComparison.OrdinalIgnoreCase)) return name;
        }
      }
    } catch (Exception e) {
      Log.Write($"call detection: {e.Message}");
    }
    return null;
  }

  static IEnumerable<(string Path, RegistryKey Key)> Keys(RegistryKey root) {
    foreach (var name in root.GetSubKeyNames()) {
      using var sub = root.OpenSubKey(name);
      if (sub == null) continue;
      if (name == "NonPackaged") {
        foreach (var exe in sub.GetSubKeyNames()) {
          using var k = sub.OpenSubKey(exe);
          if (k != null) yield return (exe, k);
        }
      } else {
        yield return (name, sub);
      }
    }
  }

  /// Like the Mac's notification with Translate and Transcribe buttons.
  static void ShowPrompt(string app) {
    prompt?.Close();
    var s = AppSettings.Shared;
    var panel = new StackPanel { Margin = new Thickness(16) };
    panel.Children.Add(new TextBlock { Text = $"{app} call started", FontWeight = FontWeights.SemiBold, FontSize = 15 });
    panel.Children.Add(new TextBlock { Text = "Translate or transcribe it with Kurzschrift?", Margin = new Thickness(0, 4, 0, 12) });
    var buttons = new StackPanel { Orientation = Orientation.Horizontal };
    Window? w = null;
    Button B(string text, Action action) {
      var b = new Button { Content = text, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(12, 4, 12, 4) };
      b.Click += (_, _) => { w?.Close(); action(); };
      buttons.Children.Add(b);
      return b;
    }
    B($"Translate ({Languages.Name(s.TranslateSpoken)} → {Languages.Name(s.TranslateTarget)})", () => _ = SessionController.Shared.Start(MeetingMode.Translate));
    B("Transcribe", () => _ = SessionController.Shared.Start(MeetingMode.Transcribe));
    B("Not now", () => { });
    panel.Children.Add(buttons);
    w = new Window {
      Title = "Kurzschrift", Content = panel, SizeToContent = SizeToContent.WidthAndHeight, Topmost = true,
      ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, WindowStyle = WindowStyle.ToolWindow,
      ShowActivated = false,
    };
    w.Loaded += (_, _) => {
      var area = SystemParameters.WorkArea;
      w.Left = area.Right - w.ActualWidth - 16;
      w.Top = area.Bottom - w.ActualHeight - 16;
    };
    var close = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
    close.Tick += (_, _) => { close.Stop(); w.Close(); };
    close.Start();
    prompt = w;
    w.Closed += (_, _) => { if (prompt == w) prompt = null; };
    w.Show();
  }
}
