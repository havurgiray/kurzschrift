using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Forms = System.Windows.Forms;

namespace Kurzschrift;

/// The speech bubble in the notification area, with the same menu as the Mac's menu bar item.
public sealed class TrayIcon : IDisposable {
  readonly Forms.NotifyIcon icon;
  readonly Icon idleIcon = Draw(false), recordingIcon = Draw(true), busyIcon = Draw(false, dimmed: true);
  readonly Forms.Timer timer;

  public TrayIcon() {
    icon = new Forms.NotifyIcon { Icon = idleIcon, Text = "Kurzschrift", Visible = true };
    icon.ContextMenuStrip = new Forms.ContextMenuStrip();
    icon.ContextMenuStrip.Opening += (_, _) => BuildMenu();
    icon.MouseClick += (_, e) => {
      if (e.Button == Forms.MouseButtons.Left) {
        if (SessionController.Shared.IsIdle && SessionController.Shared.Finished == null) ShowMenu(); else LiveWindow.ShowShared();
      }
    };
    BuildMenu();
    timer = new Forms.Timer { Interval = 1000 };
    timer.Tick += (_, _) => Refresh();
    timer.Start();
  }

  void ShowMenu() {
    // NotifyIcon only opens its menu on right-click; this is the documented way to open it on left-click.
    typeof(Forms.NotifyIcon).GetMethod("ShowContextMenu", System.Reflection.BindingFlags.Instance |
      System.Reflection.BindingFlags.NonPublic)?.Invoke(icon, null);
  }

  void Refresh() {
    var s = SessionController.Shared;
    icon.Icon = s.Phase switch { Phase.Recording => recordingIcon, Phase.Idle => idleIcon, _ => busyIcon };
    var text = s.Phase switch {
      Phase.Recording => $"Kurzschrift: {(s.Config?.Mode == MeetingMode.Translate ? "translating" : "transcribing")} {s.ElapsedText}",
      Phase.Starting or Phase.Finishing => $"Kurzschrift: {s.PhaseText}",
      _ => "Kurzschrift",
    };
    icon.Text = text.Length > 63 ? text[..63] : text;
  }

  /// The same entries in the same order as the Mac's menu bar menu.
  void BuildMenu() {
    var s = SessionController.Shared;
    var settings = AppSettings.Shared;
    var m = icon.ContextMenuStrip!;
    m.Items.Clear();
    Forms.ToolStripMenuItem Item(string text, Action action, bool enabled = true, string? keys = null) {
      // The shortcuts are only shown while they work.
      var item = new Forms.ToolStripMenuItem(text) { Enabled = enabled, ShortcutKeyDisplayString = settings.GlobalShortcuts ? keys : null };
      item.Click += (_, _) => action();
      m.Items.Add(item);
      return item;
    }

    if (s.IsRecording) {
      var title = s.Config?.Mode == MeetingMode.Translate ? "Translating…" : "Transcribing…";
      Item($"{title} {s.ElapsedText}", () => LiveWindow.ShowShared()).Font = new Font(m.Font, System.Drawing.FontStyle.Bold);
      Item("Stop and Save", () => _ = s.Stop());
      Item("Bookmark This Moment", s.AddBookmark, keys: "Ctrl+Alt+B");
    } else if (!s.IsIdle) {
      Item(s.PhaseText, () => LiveWindow.ShowShared());
    } else {
      Item(settings.MenuTitle(MeetingMode.Translate), () => _ = s.Start(MeetingMode.Translate), keys: "Ctrl+Alt+T");
      Item(settings.MenuTitle(MeetingMode.Transcribe), () => _ = s.Start(MeetingMode.Transcribe), keys: "Ctrl+Alt+R");
      if (settings.SecondTranscribeTitle is { } second)
        Item(second, () => _ = s.Start(MeetingMode.Transcribe, secondLanguage: true));
    }

    m.Items.Add(new Forms.ToolStripSeparator());
    Item("Show Live Window", () => LiveWindow.ShowShared());
    Item(settings.LiveWindowSmall ? "Expand Live Window" : "Minimize Live Window to a Bar", () => {
      settings.LiveWindowSmall = !settings.LiveWindowSmall;
      LiveWindow.ShowShared();
    });
    if (s.CanExport) {
      var export = new Forms.ToolStripMenuItem("Export Transcript");
      var save = new Forms.ToolStripMenuItem("Save as Markdown…");
      save.Click += (_, _) => LiveWindow.SaveAs(markdown: true);
      var saveText = new Forms.ToolStripMenuItem("Save as Text…");
      saveText.Click += (_, _) => LiveWindow.SaveAs(markdown: false);
      var copy = new Forms.ToolStripMenuItem("Copy to Clipboard");
      copy.Click += (_, _) => LiveWindow.Copy(s.ExportText());
      export.DropDownItems.AddRange(new Forms.ToolStripItem[] { save, saveText, copy });
      m.Items.Add(export);
    }
    Item("Open Last Transcript", () => Open(s.LastTranscript!), s.LastTranscript != null && File.Exists(s.LastTranscript));
    Item("Subtitles", () => settings.Subtitles = !settings.Subtitles).Checked = settings.Subtitles;
    Item("Open Transcripts Folder", () => {
      try {
        Directory.CreateDirectory(Environment.ExpandEnvironmentVariables(settings.SaveFolder));
      } catch (Exception e) {
        Log.Write($"creating the transcripts folder failed: {e.Message}");
      }
      Open(Environment.ExpandEnvironmentVariables(settings.SaveFolder));
    });

    m.Items.Add(new Forms.ToolStripSeparator());
    if (UpdateChecker.AvailableVersion is { } version)
      Item($"Get Kurzschrift {version}…", () => Open(UpdateChecker.AvailablePage)).Font = new Font(m.Font, System.Drawing.FontStyle.Bold);
    Item("Check for Updates…", () => _ = UpdateChecker.CheckAndTell(), UpdateChecker.State != UpdateChecker.Status.Checking);
    Item("Settings…", SettingsWindow.ShowShared);
    Item("Quit Kurzschrift", App.Instance.Quit);
  }

  public static void Open(string path) {
    try {
      Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    } catch (Exception e) {
      Log.Write($"opening failed: {e.Message}");
    }
  }

  public void ShowBalloon(string title, string text) => icon.ShowBalloonTip(8000, title, text, Forms.ToolTipIcon.None);

  /// A speech bubble with three lines; a red dot while recording; dimmed while a meeting is
  /// starting or being saved.
  static Icon Draw(bool recording, bool dimmed = false) {
    using var bmp = new Bitmap(32, 32);
    using (var g = Graphics.FromImage(bmp)) {
      g.SmoothingMode = SmoothingMode.AntiAlias;
      g.Clear(Color.Transparent);
      var fill = Color.FromArgb(dimmed ? 120 : 255, 0x2B, 0x6C, 0xD8);
      using var path = new GraphicsPath();
      path.AddArc(2, 3, 8, 8, 180, 90);
      path.AddArc(22, 3, 8, 8, 270, 90);
      path.AddArc(22, 15, 8, 8, 0, 90);
      path.AddLine(14, 23, 9, 29);
      path.AddLine(9, 29, 10, 23);
      path.AddArc(2, 15, 8, 8, 90, 90);
      path.CloseFigure();
      using var brush = new SolidBrush(fill);
      g.FillPath(brush, path);
      using var pen = new Pen(Color.FromArgb(dimmed ? 170 : 255, Color.White), 2.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
      g.DrawLine(pen, 8, 9, 24, 9);
      g.DrawLine(pen, 8, 13.5f, 24, 13.5f);
      g.DrawLine(pen, 8, 18, 18, 18);
      if (recording) {
        using var red = new SolidBrush(Color.FromArgb(255, 0xE5, 0x39, 0x35));
        using var ring = new Pen(Color.White, 2);
        g.FillEllipse(red, 19, 18, 12, 12);
        g.DrawEllipse(ring, 19, 18, 12, 12);
      }
    }
    var h = bmp.GetHicon();
    var result = (Icon)Icon.FromHandle(h).Clone();
    DestroyIcon(h);
    return result;
  }

  [DllImport("user32.dll")]
  static extern bool DestroyIcon(IntPtr handle);

  public void Dispose() {
    timer.Dispose();
    icon.Visible = false;
    icon.Dispose();
  }
}

/// Ctrl+Alt+T translate, Ctrl+Alt+R transcribe (start or stop), Ctrl+Alt+B bookmark, in any app.
public static class HotKeys {
  static HwndSource? source;
  const int WM_HOTKEY = 0x0312, MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_NOREPEAT = 0x4000;

  public static void Apply(bool enabled) {
    if (source != null) {
      for (int id = 1; id <= 3; id++) UnregisterHotKey(source.Handle, id);
      source.Dispose();
      source = null;
    }
    if (!enabled) return;
    source = new HwndSource(new HwndSourceParameters("Kurzschrift hotkeys") { WindowStyle = 0, Width = 0, Height = 0 });
    source.AddHook(Hook);
    var keys = new[] { (1, 'T'), (2, 'R'), (3, 'B') };
    foreach (var (id, key) in keys) {
      if (!RegisterHotKey(source.Handle, id, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, key))
        Log.Write($"shortcut Ctrl+Alt+{key} is taken by another app");
    }
  }

  static IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) {
    if (msg != WM_HOTKEY) return IntPtr.Zero;
    handled = true;
    var s = SessionController.Shared;
    switch (wParam.ToInt32()) {
      case 1: _ = s.Toggle(MeetingMode.Translate); break;
      case 2: _ = s.Toggle(MeetingMode.Transcribe); break;
      case 3: s.AddBookmark(); break;
    }
    return IntPtr.Zero;
  }

  [DllImport("user32.dll", SetLastError = true)]
  static extern bool RegisterHotKey(IntPtr hWnd, int id, int fsModifiers, int vk);

  [DllImport("user32.dll")]
  static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
