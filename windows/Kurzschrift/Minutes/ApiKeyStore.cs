using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Kurzschrift;

/// API keys are kept in Paths.ApiKeysFile (under %APPDATA%), in the same JSON shape as the Mac's
/// api-keys.json: {"anthropic": "…", "openai": "…"}. On Windows each value is encrypted with DPAPI
/// for the signed-in Windows account and stored as "dpapi:<base64>", so the file is of no use to
/// another account, on another PC or in a backup. A plain-text value (written by an earlier
/// version, or put there by hand) is still read, and encrypted the next time a key is saved.
/// Never log a key.
public static class ApiKeyStore {
  static readonly object Gate = new();
  static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
  const string Prefix = "dpapi:";
  /// Ties the encrypted values to this use; not a secret.
  static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Kurzschrift API key");

  static bool undecryptableLogged;

  /// Tests point this at a scratch file so they never read or write the real keys.
  internal static string? TestFile { get; set; }

  static string FilePath => TestFile ?? Paths.ApiKeysFile;

  public static string? Key(MinutesProvider provider) {
    lock (Gate) {
      if (Read() is not { } keys || !keys.TryGetValue(provider.RawValue(), out var stored) || stored == null) return null;
      var key = Unprotect(stored)?.Trim();
      return string.IsNullOrEmpty(key) ? null : key;
    }
  }

  /// An empty key removes the provider's key. False when the key couldn't be saved: the file
  /// couldn't be written, or it is there but couldn't be read (then it isn't overwritten, or one
  /// bad read would cost the other provider's key).
  public static bool SetKey(string key, MinutesProvider provider) {
    lock (Gate) {
      if (Read() is not { } keys) {
        Log.Write("API key not saved: the key file couldn't be read and isn't overwritten");
        return false;
      }
      var trimmed = key.Trim();
      if (trimmed.Length == 0) keys.Remove(provider.RawValue()); else keys[provider.RawValue()] = trimmed;
      try {
        // Everything is written encrypted, also a key that was still in plain text.
        var stored = new Dictionary<string, string?>();
        foreach (var (name, value) in keys) {
          stored[name] = value == null || value.StartsWith(Prefix, StringComparison.Ordinal) ? value : Protect(value);
        }
        if (SafeFile.Write(FilePath, JsonSerializer.Serialize(stored, Json), out var error)) return true;
        Log.Write($"saving API key failed: {error}");
      } catch (Exception e) {
        Log.Write($"saving API key failed: {e.Message}");
      }
      return false;
    }
  }

  static string Protect(string key) =>
    Prefix + Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(key), Entropy, DataProtectionScope.CurrentUser));

  /// The key behind a stored value; null when it was encrypted for another account or PC.
  static string? Unprotect(string stored) {
    if (!stored.StartsWith(Prefix, StringComparison.Ordinal)) return stored;  // plain text from an earlier version
    try {
      var data = ProtectedData.Unprotect(Convert.FromBase64String(stored[Prefix.Length..]), Entropy, DataProtectionScope.CurrentUser);
      return Encoding.UTF8.GetString(data);
    } catch (Exception e) when (e is CryptographicException or FormatException) {
      // Once: the key is looked up often.
      if (!undecryptableLogged)
        Log.Write("an API key couldn't be decrypted (saved by another Windows account or on another PC); enter it again in Settings → Minutes");
      undecryptableLogged = true;
      return null;
    }
  }

  /// Empty when there's no file yet; null when the file is there but can't be read right now. A
  /// file that isn't JSON is set aside under another name (not deleted) and the store starts empty.
  static Dictionary<string, string?>? Read() {
    try {
      if (!File.Exists(FilePath)) return new();
      return JsonSerializer.Deserialize<Dictionary<string, string?>>(File.ReadAllText(FilePath)) ?? new();
    } catch (JsonException) {
      return SafeFile.SetAside(FilePath) ? new() : null;
    } catch (Exception e) {
      Log.Write($"reading the API key file failed: {e.Message}");
      return null;
    }
  }
}
