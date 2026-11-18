using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace Kurzschrift;

/// Shows minutes and answers the way they are meant to be read instead of as raw Markdown. Just
/// enough Markdown for that, like the Mac's MarkdownView: #/##/### headings, bullets, "- [ ]" and
/// "- [x]" checkboxes, **bold**, *italic* and `code`. Read-only; the text can be selected and copied.
public sealed class MarkdownView : FlowDocumentScrollViewer {
  public static readonly DependencyProperty MarkdownProperty = DependencyProperty.Register(
    nameof(Markdown), typeof(string), typeof(MarkdownView), new PropertyMetadata("", (d, _) => ((MarkdownView)d).Rebuild()));

  public string? Markdown {
    get => (string?)GetValue(MarkdownProperty);
    set => SetValue(MarkdownProperty, value);
  }

  bool inline;
  /// One of several in a scrolling list: as tall as its text, and the mouse wheel scrolls the list.
  public bool Inline {
    get => inline;
    set {
      inline = value;
      VerticalScrollBarVisibility = value ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
      Rebuild();
    }
  }

  public MarkdownView() {
    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
    VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
    Background = Brushes.Transparent;
    BorderThickness = new Thickness(0);
    Padding = new Thickness(0);
    IsToolBarVisible = false;
  }

  protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e) {
    base.OnPropertyChanged(e);
    if (e.Property == FontSizeProperty || e.Property == FontFamilyProperty) Rebuild();
  }

  protected override void OnPreviewMouseWheel(MouseWheelEventArgs e) {
    if (inline && !e.Handled && VisualTreeHelper.GetParent(this) is UIElement parent) {
      e.Handled = true;
      parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = MouseWheelEvent, Source = this });
      return;
    }
    base.OnPreviewMouseWheel(e);
  }

  void Rebuild() {
    double size = FontSize > 0 ? FontSize : 14;
    var doc = new FlowDocument {
      FontFamily = FontFamily, FontSize = size, TextAlignment = TextAlignment.Left,
      PagePadding = new Thickness(inline ? 0 : 6, inline ? 2 : 4, inline ? 0 : 6, inline ? 0 : 4),
      Background = Brushes.Transparent, IsOptimalParagraphEnabled = false, IsHyphenationEnabled = false,
    };
    // The window's text colour, so it follows the light and the dark theme.
    doc.SetBinding(FlowDocument.ForegroundProperty, new Binding { Source = this, Path = new PropertyPath(ForegroundProperty) });
    foreach (var raw in (Markdown ?? "").Replace("\r\n", "\n").Split('\n')) {
      var line = raw.Trim();
      if (line.Length == 0) continue;
      Paragraph p;
      if (Strip(line, "### ", "## ", "# ") is { } heading) {
        p = new Paragraph { FontWeight = FontWeights.SemiBold, FontSize = size + 1, Margin = new Thickness(0, 9, 0, 2) };
        AddInlines(p, heading);
      } else if (Strip(line, "- [ ] ", "* [ ] ") is { } task) {
        p = Bullet("☐", task);
      } else if (Strip(line, "- [x] ", "- [X] ", "* [x] ", "* [X] ") is { } done) {
        p = Bullet("☑", done);
      } else if (Strip(line, "- ", "* ", "• ") is { } item) {
        p = Bullet("•", item);
      } else {
        p = new Paragraph { Margin = new Thickness(0, 2, 0, 3) };
        AddInlines(p, line);
      }
      doc.Blocks.Add(p);
    }
    Document = doc;
  }

  static Paragraph Bullet(string mark, string text) {
    // A hanging indent: the mark sticks out to the left of the text, which wraps under itself.
    var p = new Paragraph { Margin = new Thickness(18, 1, 0, 2), TextIndent = -16 };
    // One font for the marks, so the empty and the ticked box are the same size. \u2002 is an en space.
    p.Inlines.Add(new Run(mark + "\u2002") { FontFamily = Marks });
    AddInlines(p, text);
    return p;
  }

  static readonly FontFamily Marks = new("Segoe UI Symbol");

  static string? Strip(string line, params string[] prefixes) {
    foreach (var prefix in prefixes) if (line.StartsWith(prefix, StringComparison.Ordinal)) return line[prefix.Length..];
    return null;
  }

  static readonly Regex InlineSyntax = new(@"(\*\*.+?\*\*|__.+?__|`[^`]+`|(?<![\w*])\*(?=\S)[^*]+?(?<=\S)\*(?![\w*]))");

  static void AddInlines(Paragraph p, string text) {
    foreach (var part in InlineSyntax.Split(text)) {
      if (part.Length == 0) continue;
      if (part.Length > 4 && (part.StartsWith("**") && part.EndsWith("**") || part.StartsWith("__") && part.EndsWith("__")))
        p.Inlines.Add(new Run(part[2..^2]) { FontWeight = FontWeights.SemiBold });
      else if (part.Length > 2 && part.StartsWith('`') && part.EndsWith('`'))
        p.Inlines.Add(new Run(part[1..^1]) { FontFamily = new FontFamily("Consolas") });
      else if (part.Length > 2 && part.StartsWith('*') && part.EndsWith('*') && !part.StartsWith("**"))
        p.Inlines.Add(new Run(part[1..^1]) { FontStyle = FontStyles.Italic });
      else
        p.Inlines.Add(new Run(part));
    }
  }
}
