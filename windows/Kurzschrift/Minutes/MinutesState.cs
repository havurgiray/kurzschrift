namespace Kurzschrift;

/// What the minutes pane shows. Immutable: every change is a new state (`with`), so a state handed
/// to the UI never changes under it.
public sealed record MinutesState(string Model, MinutesProvider Provider) {
  public string Text { get; init; } = "";
  /// false when live minutes are off: the pane then holds only your own notes.
  public bool UsesAI { get; init; } = true;
  /// You changed the minutes and no update has worked the changes in yet.
  public bool EditsPending { get; init; }
  public DateTimeOffset? UpdatedAt { get; init; }
  public DateTimeOffset? NextUpdate { get; init; }
  public bool IsUpdating { get; init; }
  public string? Error { get; init; }
  public MinutesUsage Usage { get; init; }
  /// Rough USD cost so far (known Claude models only).
  public double? Cost { get; init; }
}
