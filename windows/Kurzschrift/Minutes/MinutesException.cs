namespace Kurzschrift;

public enum MinutesErrorKind {
  NoKey,
  Http,
  Refused,
  EmptyResponse,
  Network,
}

/// What went wrong writing minutes or answering a question. `Message` is the text for the pane.
public sealed class MinutesException : Exception {
  public MinutesErrorKind Kind { get; }
  /// The HTTP status (Http only).
  public int Status { get; }
  /// The provider's error message (Http), the refusal category if any (Refused), or the network
  /// error (Network).
  public string? Detail { get; }

  MinutesException(MinutesErrorKind kind, int status, string? detail, string message) : base(message) {
    Kind = kind;
    Status = status;
    Detail = detail;
  }

  public static MinutesException NoKey() =>
    new(MinutesErrorKind.NoKey, 0, null, "No API key is set (Settings → Minutes).");

  public static MinutesException Http(int status, string message) =>
    new(MinutesErrorKind.Http, status, message, status switch {
      401 => "The API key was rejected. Check it in Settings → Minutes.",
      404 => $"Model not available to this key: {message}",
      429 => "Rate limited by the provider. Will retry at the next update.",
      529 or 503 => "The provider is overloaded. Will retry at the next update.",
      _ => $"HTTP {status}: {message}",
    });

  public static MinutesException Refused(string? category) =>
    new(MinutesErrorKind.Refused, 0, category,
      $"The model declined to write minutes{(category != null ? $" ({category})" : "")}.");

  public static MinutesException EmptyResponse() =>
    new(MinutesErrorKind.EmptyResponse, 0, null, "The model returned no text.");

  public static MinutesException Network(string message) =>
    new(MinutesErrorKind.Network, 0, message, $"Network error: {message}");

  public bool IsRetryable => Kind switch {
    MinutesErrorKind.Http => Status is 408 or 429 or 500 or 502 or 503 or 504 or 529,
    MinutesErrorKind.Network => true,
    _ => false,
  };

  /// For the log, like the Mac's description of the error: http(500, "…").
  public override string ToString() => Kind switch {
    MinutesErrorKind.NoKey => "noKey",
    MinutesErrorKind.Http => $"http({Status}, \"{Detail}\")",
    MinutesErrorKind.Refused => $"refused({(Detail == null ? "nil" : $"\"{Detail}\"")})",
    MinutesErrorKind.EmptyResponse => "emptyResponse",
    _ => $"network(\"{Detail}\")",
  };

  /// One line for the log about any error, without a stack trace.
  internal static string LogText(Exception error) =>
    error is MinutesException ? error.ToString() : $"{error.GetType().Name}: {error.Message}";
}
