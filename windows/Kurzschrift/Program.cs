using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace Kurzschrift;

public static class Program {
  const string MutexName = @"Local\Kurzschrift-single-instance";
  static string PipeName => $"Kurzschrift-{Environment.UserName}";

  [STAThread]
  public static int Main(string[] args) {
    // Child process for speaker separation (see Diarization.cs).
    if (args.Length >= 3 && args[0] == Diarization.Flag) {
      try {
        return Diarization.RunChild(args[1], args[2]);
      } catch (Exception e) {
        Console.Error.WriteLine(e.Message);
        return 1;
      }
    }
    using var mutex = new Mutex(true, MutexName, out bool first);
    if (!first) {
      Forward(args);
      return 0;
    }
    var app = new App(args);
    app.Run();
    return 0;
  }

  /// Hands this launch's arguments (a kurzschrift:// link, or nothing to just show the app) to the
  /// copy that's already running.
  static void Forward(string[] args) {
    try {
      using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
      pipe.Connect(3000);
      using var w = new StreamWriter(pipe);
      w.Write(args.Length == 0 ? "--show" : string.Join("\n", args));
    } catch {
      // The running copy is shutting down or hung; nothing sensible to do here.
    }
  }

  public static void Listen(Action<string[]> handle) {
    var thread = new Thread(() => {
      while (true) {
        try {
          using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte);
          pipe.WaitForConnection();
          using var r = new StreamReader(pipe);
          var text = r.ReadToEnd();
          handle(text.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        } catch (Exception e) {
          Log.Write($"instance pipe: {e.Message}");
          Thread.Sleep(1000);
        }
      }
    }) { IsBackground = true, Name = "instance pipe" };
    thread.Start();
  }

  public static string Version =>
    System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";
}

public sealed class App : Application {
  readonly string[] launchArgs;
  public TrayIcon? Tray { get; private set; }
  public static bool ShuttingDown { get; private set; }
  public static App Instance => (App)Current;

  public App(string[] args) => launchArgs = args;

  protected override void OnStartup(StartupEventArgs e) {
    base.OnStartup(e);
    ShutdownMode = ShutdownMode.OnExplicitShutdown;
#pragma warning disable WPF0001
    ThemeMode = ThemeMode.System;
#pragma warning restore WPF0001
    DispatcherUnhandledException += (_, ex) => {
      Log.Write($"unhandled: {ex.Exception}");
      ex.Handled = true;
    };
    AppDomain.CurrentDomain.UnhandledException += (_, ex) => Log.Write($"fatal: {ex.ExceptionObject}");
    Log.Write($"launch {Program.Version} {RuntimeInformation.ProcessArchitecture} on {Environment.OSVersion.VersionString}");
    MinutesProviderExtensions.LogOverrides();

    var session = SessionController.Shared;
    session.ModelsNeeded = models => DownloadWindow.Run(models);
    session.ShowWindowRequested += () => LiveWindow.ShowShared();
    session.ErrorOccurred += (message, suggestion) =>
      MessageBox.Show(suggestion.Length > 0 ? $"{message}\n\n{suggestion}" : message, "Kurzschrift",
        MessageBoxButton.OK, MessageBoxImage.Warning);
    session.BookmarkAdded += () => System.Media.SystemSounds.Asterisk.Play();

    Tray = new TrayIcon();
    HotKeys.Apply(AppSettings.Shared.GlobalShortcuts);
    AppSettings.Shared.PropertyChanged += (_, p) => {
      if (p.PropertyName == nameof(AppSettings.GlobalShortcuts)) HotKeys.Apply(AppSettings.Shared.GlobalShortcuts);
      if (p.PropertyName == nameof(AppSettings.MinutesIntervalMinutes)) session.MinutesIntervalChanged();
      if (p.PropertyName is nameof(AppSettings.Subtitles) or nameof(AppSettings.SubtitlesAtTop)) SubtitlesWindow.Update();
      if (p.PropertyName == nameof(AppSettings.DetectCalls)) CallDetector.Apply();
    };
    CallDetector.Apply();
    SubtitlesWindow.Update();
    Program.Listen(args => Dispatcher.BeginInvoke(() => HandleArgs(args)));

    bool firstRun = !File.Exists(Paths.SettingsFile);
    AppSettings.Shared.Save();
    HandleArgs(launchArgs);
    if (firstRun) {
      Tray.ShowBalloon("Kurzschrift is ready",
        "Find it in the notification area at the bottom right. Right-click the speech bubble to start a meeting.");
    }
  }

  void HandleArgs(string[] args) {
    foreach (var a in args) {
      if (a.StartsWith("kurzschrift:", StringComparison.OrdinalIgnoreCase)) {
        _ = Links.Handle(a);
      } else if (a == "--show") {
        if (SessionController.Shared.IsIdle) SettingsWindow.ShowShared(); else LiveWindow.ShowShared();
      }
    }
  }

  /// Quitting mid-meeting saves the transcript first.
  public async void Quit() {
    var session = SessionController.Shared;
    while (!session.IsIdle) {
      if (session.IsRecording) await session.Stop(); else await Task.Delay(200);
    }
    Tray?.Dispose();
    HotKeys.Apply(false);
    Log.Write("quit");
    ShuttingDown = true;
    Shutdown();
  }
}

/// kurzschrift://start?mode=translate|transcribe, kurzschrift://stop, kurzschrift://toggle?mode=…,
/// kurzschrift://bookmark, kurzschrift://minutes, kurzschrift://ask?q=…, kurzschrift://rename?from=…&to=….
/// The developer links (start?audio=, snapshot, edit-minutes, window, context) only work while
/// Settings → Advanced allows them, because any web page can try to open a kurzschrift:// link.
public static class Links {
  public static async Task Handle(string link) {
    if (!Uri.TryCreate(link, UriKind.Absolute, out var uri)) return;
    var action = uri.Host.ToLowerInvariant();
    var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
    string? Q(string name) => query[name];
    MeetingMode? mode = Q("mode")?.ToLowerInvariant() switch {
      "translate" => MeetingMode.Translate, "transcribe" => MeetingMode.Transcribe, _ => null,
    };
    bool developer = new[] { "snapshot", "edit-minutes", "window", "context" }.Contains(action) ||
                     (action == "start" && Q("audio") != null);
    // Parameter names only: a question, a name or minutes text doesn't belong in the log.
    Log.Write($"url: {action} {string.Join(",", query.AllKeys)}");
    if (developer && !AppSettings.Shared.DeveloperLinks) {
      Log.Write("url ignored: developer links are off (Settings → Advanced)");
      return;
    }
    var s = SessionController.Shared;
    switch (action) {
      case "start":
        if (mode is { } m) await s.Start(m, Q("audio"));
        break;
      case "stop": await s.Stop(); break;
      case "toggle":
        if (s.IsRecording) await s.Stop(); else if (mode is { } m2) await s.Start(m2);
        break;
      case "minutes":
        if (s.CanStartAIMinutes) s.StartAIMinutes(); else if (s.CanRedoMinutes) s.RedoMinutes(); else s.UpdateMinutesNow();
        break;
      case "bookmark": s.AddBookmark(); break;
      case "ask":
        if (Q("q") is { } question) s.Ask(question);
        break;
      case "rename":
        if (Q("from") is { } from && Q("to") is { } to) s.RenameSpeaker(from, to, Q("remember") != "no");
        break;
      case "snapshot":
        if (Q("path") is { } path && path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) {
          if (Q("window") == "subtitles") SubtitlesWindow.Snapshot(path); else LiveWindow.Snapshot(path);
        }
        break;
      case "edit-minutes":
        switch (Q("step")) {
          case "begin": s.BeginEditingMinutes(); break;
          case "cancel": s.CancelEditingMinutes(); break;
          default:
            s.BeginEditingMinutes();
            if (Q("text") is { } text) s.MinutesDraft = text;
            s.FinishEditingMinutes();
            break;
        }
        break;
      case "window":
        if (Q("size") is { } size) AppSettings.Shared.LiveWindowSmall = size == "small";
        if (Q("tab") is { } tab) LiveWindow.SelectTab(tab);
        if (Q("show") == "settings") SettingsWindow.ShowShared(); else LiveWindow.ShowShared();
        break;
      case "context":
        s.Context = new MeetingContext(Q("participants") ?? s.Context.Participants, Q("about") ?? s.Context.About);
        break;
    }
  }
}
