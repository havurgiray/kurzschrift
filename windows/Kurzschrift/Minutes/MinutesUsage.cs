namespace Kurzschrift;

/// Tokens the minutes have used.
public readonly record struct MinutesUsage(int Input = 0, int CacheRead = 0, int CacheWrite = 0, int Output = 0) {
  public static MinutesUsage operator +(MinutesUsage a, MinutesUsage b) =>
    new(a.Input + b.Input, a.CacheRead + b.CacheRead, a.CacheWrite + b.CacheWrite, a.Output + b.Output);

  /// Rough cost in USD for the Claude models whose list prices are known here; null otherwise.
  public double? EstimatedCost(string model, bool longCache) {
    (double Input, double Output, double CacheRead)? prices = model switch {
      "claude-opus-5-5" => (4, 20, 0.20),
      "claude-sonnet-5-5" => (2, 10, 0.20),
      "claude-haiku-5-5" => (0.10, 0.50, 0.01),
      _ => null,
    };
    if (prices is not { } p) return null;
    var writeFactor = longCache ? 2.0 : 1.25;
    var dollars =
      (double)Input * p.Input + (double)CacheWrite * p.Input * writeFactor + (double)CacheRead * p.CacheRead
      + (double)Output * p.Output;
    return dollars / 1_000_000;
  }
}
