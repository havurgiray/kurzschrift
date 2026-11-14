namespace Kurzschrift;

// Live meeting minutes written by an LLM (Anthropic or OpenAI) from the growing transcript.
// This is the only part of Kurzschrift that sends anything off the computer, and only when switched on.

public enum MinutesProvider {
  Anthropic,
  OpenAI,
}

public static class MinutesProviderExtensions {
  public static IReadOnlyList<MinutesProvider> All { get; } = [MinutesProvider.Anthropic, MinutesProvider.OpenAI];

  /// "anthropic" or "openai": the name in the settings and in api-keys.json.
  public static string RawValue(this MinutesProvider provider) => provider switch {
    MinutesProvider.Anthropic => "anthropic",
    MinutesProvider.OpenAI => "openai",
    _ => throw new ArgumentOutOfRangeException(nameof(provider)),
  };

  /// The provider with this raw value, or null.
  public static MinutesProvider? FromRawValue(string? rawValue) => rawValue switch {
    "anthropic" => MinutesProvider.Anthropic,
    "openai" => MinutesProvider.OpenAI,
    _ => null,
  };

  public static string DisplayName(this MinutesProvider provider) => provider switch {
    MinutesProvider.Anthropic => "Anthropic (Claude)",
    _ => "OpenAI",
  };

  public static string DefaultModel(this MinutesProvider provider) => provider switch {
    MinutesProvider.Anthropic => "claude-opus-5-5",
    _ => "",  // picked from the account's model list ("Test key")
  };

  public static Uri KeysPage(this MinutesProvider provider) => provider switch {
    MinutesProvider.Anthropic => new Uri("https://console.anthropic.com/settings/keys"),
    _ => new Uri("https://platform.openai.com/api-keys"),
  };

  /// Hidden override for testing against a local mock server: the environment variable
  /// KURZSCHRIFT_MINUTES_BASE_URL_ANTHROPIC or KURZSCHRIFT_MINUTES_BASE_URL_OPENAI (on the Mac,
  /// `defaults write local.kurzschrift minutesBaseURL.<provider> …`).
  public static string OverrideVariable(this MinutesProvider provider) =>
    "KURZSCHRIFT_MINUTES_BASE_URL_" + provider.RawValue().ToUpperInvariant();

  public static bool IsOverridden(this MinutesProvider provider) =>
    Environment.GetEnvironmentVariable(provider.OverrideVariable()) != null;

  public static Uri BaseUrl(this MinutesProvider provider) {
    var value = Environment.GetEnvironmentVariable(provider.OverrideVariable());
    if (value != null && Uri.TryCreate(value, UriKind.Absolute, out var url)) return url;
    return provider switch {
      MinutesProvider.Anthropic => new Uri("https://api.anthropic.com"),
      _ => new Uri("https://api.openai.com"),
    };
  }

  /// Call once at launch, as the Mac app does: the log says which providers go to a mock server.
  public static void LogOverrides() {
    foreach (var provider in All) {
      if (provider.IsOverridden()) {
        Log.Write($"testing override: {provider.RawValue()} requests go to {provider.BaseUrl().OriginalString}");
      }
    }
  }
}
