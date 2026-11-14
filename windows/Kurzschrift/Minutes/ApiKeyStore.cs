using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Kurzschrift;

/// API keys are kept in a file only this user can read (Paths.ApiKeysFile, under %APPDATA%, which
/// Windows already limits to the user), in the same JSON as the Mac's api-keys.json:
/// {"anthropic": "…", "openai": "…"}. Never log a key.
public static class ApiKeyStore {
  static readonly object Gate = new();
  static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

  /// Tests point this at a scratch file so they never read or write the real keys.
  internal static string? TestFile { get; set; }

  static string FilePath => TestFile ?? Paths.ApiKeysFile;

  public static string? Key(MinutesProvider provider) {
    lock (Gate) {
      if (Read() is not { } keys || !keys.TryGetValue(provider.RawValue(), out var key) || key == null) return null;
      key = key.Trim();
      return key.Length == 0 ? null : key;
    }
  }

  /// An empty key removes the provider's key.
  public static void SetKey(string key, MinutesProvider provider) {
    lock (Gate) {
      var keys = Read() ?? [];
      var trimmed = key.Trim();
      if (trimmed.Length == 0) keys.Remove(provider.RawValue()); else keys[provider.RawValue()] = trimmed;
      var path = FilePath;
      var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
      try {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Written next to the file and moved over it, so the file is never half written.
        File.WriteAllText(temp, JsonSerializer.Serialize(keys, Json));
        File.Move(temp, path, overwrite: true);
      } catch (Exception e) {
        try { File.Delete(temp); } catch { }
        Log.Write($"saving API key failed: {e.Message}");
      }
    }
  }

  static Dictionary<string, string?>? Read() {
    try {
      return JsonSerializer.Deserialize<Dictionary<string, string?>>(File.ReadAllBytes(FilePath));
    } catch {
      return null;
    }
  }
}
