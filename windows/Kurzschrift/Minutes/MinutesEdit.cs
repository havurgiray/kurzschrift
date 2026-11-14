using System.Globalization;

namespace Kurzschrift;

/// One round of your changes to the minutes: the lines you removed and the lines you added.
/// A changed line shows up as both, which is how the model sees a correction.
public sealed class MinutesEdit : IEquatable<MinutesEdit> {
  public IReadOnlyList<string> Removed { get; }
  public IReadOnlyList<string> Added { get; }

  MinutesEdit(IReadOnlyList<string> removed, IReadOnlyList<string> added) {
    Removed = removed;
    Added = added;
  }

  /// null when nothing changed (moved lines don't count).
  public static MinutesEdit? Between(string old, string @new) {
    var oldLines = Lines(old);
    var newLines = Lines(@new);
    var (removedOffsets, insertedOffsets) = Difference(oldLines, newLines);
    // In reading order.
    var removed = removedOffsets.Order().Select(offset => oldLines[offset]).ToList();
    var added = insertedOffsets.Order().Select(offset => newLines[offset]).ToList();
    var moved = new HashSet<string>(removed, StringComparer.Ordinal);
    moved.IntersectWith(added);
    removed.RemoveAll(moved.Contains);
    added.RemoveAll(moved.Contains);
    return removed.Count == 0 && added.Count == 0 ? null : new MinutesEdit(removed, added);
  }

  /// Non-empty lines without the spaces around them. A \r is dropped too, for text from a Windows
  /// text box (the Mac only ever has \n).
  static string[] Lines(string text) =>
    text.Split('\n').Select(TrimSpaces).Where(line => line.Length > 0).ToArray();

  static string TrimSpaces(string line) {
    static bool IsSpace(char c) => c is '\t' or '\r' || char.GetUnicodeCategory(c) == UnicodeCategory.SpaceSeparator;
    int start = 0, end = line.Length;
    while (start < end && IsSpace(line[start])) start++;
    while (end > start && IsSpace(line[end - 1])) end--;
    return line[start..end];
  }

  /// The offsets of removed lines in `old` and inserted lines in `new`, picked exactly as Swift's
  /// `difference(from:)` picks them (the linear-space Myers search in Swift 6.3's stdlib, step for
  /// step), so when there's a choice (repeated lines) the model gets the same edits as on the Mac.
  internal static (List<int> Removed, List<int> Inserted) Difference(string[] old, string[] @new) {
    var removed = new List<int>();
    var inserted = new List<int>();
    var initial = Box.Shrunk(0, 0, old.Length, @new.Length, old, @new);
    var search = new LinearMyers(initial.Size, old, @new);
    var stack = new Stack<Box>();
    var current = initial;
    while (true) {
      var (edit, snake) = search.MiddleSnake(current);
      if (edit is { } e) (e.IsInsertion ? inserted : removed).Add(e.Offset);
      if (snake is not { } s) {
        // No new middle snake: on to the next area on the stack (the right side of an earlier snake).
        if (!stack.TryPop(out current)) return (removed, inserted);
        continue;
      }
      // The left side of the snake next, its right side later.
      var head = Box.Shrunk(current.Left, current.Top, s.Left, s.Top, old, @new);
      var tail = Box.Shrunk(s.Right, s.Bottom, current.Right, current.Bottom, old, @new);
      current = head;
      stack.Push(tail);
    }
  }

  /// A region of the edit graph: old lines Left..<Right, new lines Top..<Bottom.
  readonly record struct Box(int Left, int Top, int Right, int Bottom) {
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public int Size => Width + Height;
    public int Delta => Width - Height;
    public bool IsOdd => Delta % 2 != 0;
    public int Max => (Size + 1) / 2;

    /// Without the equal lines at its start and end.
    public static Box Shrunk(int left, int top, int right, int bottom, string[] old, string[] @new) {
      while (left < right && top < bottom && old[left] == @new[top]) {
        left++;
        top++;
      }
      while (right > left && bottom > top && old[right - 1] == @new[bottom - 1]) {
        right--;
        bottom--;
      }
      return new Box(left, top, right, bottom);
    }
  }

  /// The middle-snake search, with Swift's two k-vectors in one buffer that every search reuses.
  sealed class LinearMyers(int size, string[] old, string[] @new) {
    readonly int[] buffer = new int[(size * 2 + 1) * 2];
    readonly int forwardOffset = size;
    readonly int backwardOffset = size * 2 + 1 + size;

    ref int Forward(int k) => ref buffer[forwardOffset + k];
    ref int Backward(int c) => ref buffer[backwardOffset + c];

    public ((bool IsInsertion, int Offset)? Edit, Box? Snake) MiddleSnake(Box box) {
      if (box.Size <= 1) {
        // One move right or down is a removal or an insertion.
        if (box.Size == 0) return (null, null);
        return box.Width == 1 ? ((false, box.Left), null) : ((true, box.Top), null);
      }
      Forward(1) = box.Left;
      Backward(1) = box.Bottom;
      for (var depth = 0; depth <= box.Max; depth++) {
        if (box.IsOdd) {
          if (ForwardSearch(box, depth) is { } found) return found;
          BackwardSearch(box, depth);
        } else {
          ForwardSearch(box, depth);
          if (BackwardSearch(box, depth) is { } found) return found;
        }
      }
      throw new InvalidOperationException("no middle snake");
    }

    ((bool, int)?, Box?)? ForwardSearch(Box box, int depth) {
      for (var k = depth; k >= -depth; k -= 2) {
        int newLeft, newRight, newBottom, editIndex;
        bool isInsertion;
        if (k == -depth || (k != depth && Forward(k - 1) < Forward(k + 1))) {
          newRight = Forward(k + 1);
          newLeft = newRight;
          newBottom = box.Top + (newRight - box.Left) - k;
          isInsertion = true;
          editIndex = newBottom - 1;
        } else {
          newLeft = Forward(k - 1);
          newRight = newLeft + 1;
          newBottom = box.Top + (newRight - box.Left) - k;
          isInsertion = false;
          editIndex = newLeft;
        }
        var newTop = depth == 0 || newRight != newLeft ? newBottom : newBottom - 1;
        while (newRight < box.Right && newBottom < box.Bottom && old[newRight] == @new[newBottom]) {
          newRight++;
          newBottom++;
        }
        Forward(k) = newRight;
        // Overlap is only checked moving forward in odd-sized boxes.
        if (!box.IsOdd) continue;
        var c = k - box.Delta;
        if (c >= -(depth - 1) && c <= depth - 1 && newBottom >= Backward(c)) {
          return ((isInsertion, editIndex), new Box(newLeft, newTop, newRight, newBottom));
        }
      }
      return null;
    }

    ((bool, int)?, Box?)? BackwardSearch(Box box, int depth) {
      for (var c = depth; c >= -depth; c -= 2) {
        var k = c + box.Delta;
        int newTop, newBottom, newLeft, editIndex;
        bool isInsertion;
        if (c == -depth || (c != depth && Backward(c - 1) > Backward(c + 1))) {
          newTop = Backward(c + 1);
          newBottom = newTop;
          newLeft = box.Left + (newTop - box.Top) + k;
          isInsertion = false;
          editIndex = newLeft;
        } else {
          newBottom = Backward(c - 1);
          newTop = newBottom - 1;
          newLeft = box.Left + (newTop - box.Top) + k;
          isInsertion = true;
          editIndex = newTop;
        }
        var newRight = depth == 0 || newTop != newBottom ? newLeft : newLeft + 1;
        while (newLeft > box.Left && newTop > box.Top && old[newLeft - 1] == @new[newTop - 1]) {
          newLeft--;
          newTop--;
        }
        Backward(c) = newTop;
        // Overlap is only checked moving backward in even-sized boxes.
        if (box.IsOdd) continue;
        if (k >= -depth && k <= depth && newLeft <= Forward(k)) {
          return ((isInsertion, editIndex), new Box(newLeft, newTop, newRight, newBottom));
        }
      }
      return null;
    }
  }

  public bool Equals(MinutesEdit? other) =>
    other != null && Removed.SequenceEqual(other.Removed) && Added.SequenceEqual(other.Added);

  public override bool Equals(object? obj) => Equals(obj as MinutesEdit);

  public override int GetHashCode() => HashCode.Combine(Removed.Count, Added.Count);
}
