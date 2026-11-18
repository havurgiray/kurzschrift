using System.Text.Json;
using System.Text.RegularExpressions;

namespace Kurzschrift;

/// Asks GitHub which release is the latest and compares it with this app's version. It only tells
/// you: the download and the install stay yours. One GET to api.github.com, with nothing about you
/// or your meetings in it, and only on "Check Now" or while Settings → Advanced has the daily check on.
public static class UpdateChecker {
  public const string ReleasesPage = "https://github.com/havurgiray/kurzschrift/releases/latest";
  const string RepositoryPrefix = "https://github.com/havurgiray/kurzschrift/";
  const string Api = "https://api.github.com/repos/havurgiray/kurzschrift/releases/latest";

  public enum Status { Idle, Checking, UpToDate, Available, Failed }

  public static Status State { get; private set; } = Status.Idle;
  /// The newer version and its page while `State` is Available.
  public static string? AvailableVersion { get; private set; }
  public static string AvailablePage { get; private set; } = ReleasesPage;
  public static string? Problem { get; private set; }
  /// Raised on the UI thread after every change.
  public static event Action? Changed;

  static System.Windows.Threading.DispatcherTimer? timer;

  static UpdateChecker() {
    // A newer version found earlier stays in the menu until it is installed.
    var known = AppSettings.Shared.UpdateKnownVersion;
    if (known.Length > 0 && IsNewer(known, Program.Version)) {
      AvailableVersion = known;
      State = Status.Available;
    }
  }

  /// Starts or stops the daily check to match the setting.
  public static void Apply(bool automatic) {
    timer?.Stop();
    timer = null;
    if (!automatic) return;
    CheckIfDue();
    timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromHours(1) };
    timer.Tick += (_, _) => CheckIfDue();
    timer.Start();
  }

  static void CheckIfDue() {
    // Not during a meeting: nothing should happen there that the meeting doesn't need.
    if (!SessionController.Shared.IsIdle) return;
    if (DateTime.UtcNow - AppSettings.Shared.UpdateLastChecked < TimeSpan.FromDays(1)) return;
    _ = Check(announce: true);
  }

  /// `announce` shows a notification when a version is found that wasn't known before (daily check).
  public static async Task Check(bool announce = false) {
    if (State == Status.Checking) return;
    var before = AvailableVersion;
    State = Status.Checking;
    Changed?.Invoke();
    try {
      var (version, page) = await Latest();
      AppSettings.Shared.UpdateLastChecked = DateTime.UtcNow;
      if (IsNewer(version, Program.Version)) {
        AppSettings.Shared.UpdateKnownVersion = version;
        AvailableVersion = version;
        AvailablePage = page;
        State = Status.Available;
        Log.Write($"update check: {version} is available (this is {Program.Version})");
        if (announce && before != version)
          App.Instance.Tray?.ShowBalloon($"Kurzschrift {version} is available", "Right-click the speech bubble to get it.");
      } else {
        AppSettings.Shared.UpdateKnownVersion = "";
        AvailableVersion = null;
        State = Status.UpToDate;
        Log.Write($"update check: {Program.Version} is the latest");
      }
    } catch (Exception e) {
      Problem = Message(e);
      State = Status.Failed;
      Log.Write($"update check failed: {e.Message}");
    }
    Changed?.Invoke();
  }

  /// For the menu's "Check for Updates…": checks, then says the result in a dialog.
  public static async Task CheckAndTell() {
    await Check();
    switch (State) {
      case Status.Available:
        if (System.Windows.MessageBox.Show(
              $"Kurzschrift {AvailableVersion} is available. You have {Program.Version}.\n\nOpen the download page?",
              "Kurzschrift", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Information) ==
            System.Windows.MessageBoxResult.Yes) TrayIcon.Open(AvailablePage);
        break;
      case Status.Failed:
        System.Windows.MessageBox.Show($"Couldn't check for updates.\n\n{Problem}", "Kurzschrift",
          System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        break;
      default:
        System.Windows.MessageBox.Show($"Kurzschrift is up to date. {Program.Version} is the latest version.", "Kurzschrift",
          System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
        break;
    }
  }

  static async Task<(string Version, string Page)> Latest() {
    // Testing override, like the minutes': KURZSCHRIFT_UPDATE_URL=http://localhost:…, honoured
    // only while Settings → Advanced allows developer links.
    var url = Api;
    if (Environment.GetEnvironmentVariable("KURZSCHRIFT_UPDATE_URL") is { Length: > 0 } o) {
      if (AppSettings.Shared.DeveloperLinks) {
        url = o;
        Log.Write($"testing override: the update check goes to {(Uri.TryCreate(o, UriKind.Absolute, out var u) ? u.Host : "?")}");
      } else {
        Log.Write("testing override ignored: KURZSCHRIFT_UPDATE_URL is set, but developer links are off (Settings → Advanced)");
      }
    }
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    using var request = new HttpRequestMessage(HttpMethod.Get, url);
    request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
    request.Headers.TryAddWithoutValidation("User-Agent", $"Kurzschrift/{Program.Version}");
    using var response = await http.SendAsync(request);
    if (!response.IsSuccessStatusCode) throw new UpdateException((int)response.StatusCode);
    using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    var tag = json.RootElement.GetProperty("tag_name").GetString() ?? "";
    var version = tag.StartsWith('v') ? tag[1..] : tag;
    if (!Regex.IsMatch(version, @"^\d+(\.\d+){0,3}$")) throw new FormatException();
    // Only ever open a page of this project, whatever the answer says.
    var page = json.RootElement.TryGetProperty("html_url", out var p) ? p.GetString() ?? "" : "";
    return (version, page.StartsWith(RepositoryPrefix, StringComparison.Ordinal) ? page : ReleasesPage);
  }

  sealed class UpdateException(int status) : Exception($"HTTP {status}") {
    public int Status { get; } = status;
  }

  static string Message(Exception e) => e switch {
    UpdateException { Status: 403 or 429 } => "GitHub is limiting requests from your network right now. Try again in an hour.",
    UpdateException u => $"GitHub answered with an error (HTTP {u.Status}).",
    FormatException or JsonException or KeyNotFoundException => "GitHub's answer couldn't be read.",
    TaskCanceledException => "GitHub didn't answer in time.",
    _ => $"GitHub couldn't be reached. {e.Message}",
  };

  /// "1.10.0" is newer than "1.9.2": compared number by number.
  public static bool IsNewer(string version, string current) {
    int[] Parts(string s) => s.Split('.').Select(p => int.TryParse(p, out int n) ? n : 0).ToArray();
    var (a, b) = (Parts(version), Parts(current));
    for (int i = 0; i < Math.Max(a.Length, b.Length); i++) {
      int x = i < a.Length ? a[i] : 0, y = i < b.Length ? b[i] : 0;
      if (x != y) return x > y;
    }
    return false;
  }
}
