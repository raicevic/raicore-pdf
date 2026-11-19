using System.Xml.Linq;
using RaiCore.Pdf.Documents.Model;

namespace RaiCore.Pdf.Documents.Odt;

/// <summary>Lists: list styles and their levels, numbering across lists, labels and indents; outline numbering.</summary>
internal sealed partial class OdtReader
{
    /// <summary>The number of list levels ODF has.</summary>
    private const int Levels = 10;

    private readonly Dictionary<string, ListCounter> _listsById = new(StringComparer.Ordinal);
    private ListCounter? _lastList;

    /// <summary>The numbers of a list's levels, which continued lists share.</summary>
    private sealed class ListCounter
    {
        public int[] Counts { get; } = new int[Levels];

        public bool[] Started { get; } = new bool[Levels];
    }

    /// <summary>A list being read: its list style, level (0-based) and numbering.</summary>
    private sealed record ListContext(XElement? Style, int Level, ListCounter Counter);

    /// <summary>A paragraph in a list item: whether it gets the item's label, and the number the item restarts at.</summary>
    private sealed record ListItem(ListContext List, bool Numbered, int? Start);

    /// <summary>
    /// Reads a list: a top-level list starts new numbering, unless it continues the list before it
    /// (<c>text:continue-numbering</c>) or a list by id (<c>text:continue-list</c>); a nested list is a level deeper
    /// in the numbering of its list.
    /// </summary>
    private void ReadList(XElement list, List<WordBlock> output, bool body, ListContext? parent)
    {
        var styleName = Odf.Attr(list, Odf.Text, "style-name");
        ListContext context;
        if (parent is null)
        {
            var counter = Odf.Attr(list, Odf.Text, "continue-list") is { } continues && _listsById.TryGetValue(continues, out var continued) ? continued
                : Odf.Attr(list, Odf.Text, "continue-numbering") == "true" && _lastList is not null ? _lastList
                : new ListCounter();
            _lastList = counter;
            context = new ListContext(_styles.ListStyle(styleName, _scope), 0, counter);
        }
        else
        {
            context = new ListContext(styleName is null ? parent.Style : _styles.ListStyle(styleName, _scope) ?? parent.Style, Math.Min(parent.Level + 1, Levels - 1), parent.Counter);
        }

        if (Odf.Attr(list, Odf.Xml, "id") is { } id)
        {
            _listsById.TryAdd(id, context.Counter);
        }

        foreach (var element in list.Elements())
        {
            if (Odf.Is(element, Odf.Text, "list-item"))
            {
                ReadListItem(element, output, body, context, header: false);
            }
            else if (Odf.Is(element, Odf.Text, "list-header"))
            {
                ReadListItem(element, output, body, context, header: true);
            }
        }
    }

    /// <summary>Reads a list item (or a list header, whose paragraphs have no label): its paragraphs and nested lists.</summary>
    private void ReadListItem(XElement item, List<WordBlock> output, bool body, ListContext list, bool header)
    {
        var start = Odf.Int(item, Odf.Text, "start-value");
        var first = !header;
        foreach (var element in item.Elements())
        {
            if (++_blocks % CheckpointInterval == 0)
            {
                _context.Checkpoint();
            }

            if (Odf.Is(element, Odf.Text, "p") || Odf.Is(element, Odf.Text, "h"))
            {
                ReadParagraph(element, output, body, new ListItem(list, first, start));
                first = false;
            }
            else if (Odf.Is(element, Odf.Text, "list"))
            {
                ReadList(element, output, body, list);
                first = false;
            }
            else if (Odf.Is(element, Odf.Table, "table"))
            {
                ReadTableBlock(element, output, body);
            }
        }
    }

    /// <summary>Reads a numbered paragraph (<c>text:numbered-paragraph</c>): a list item outside a list element.</summary>
    private void ReadNumberedParagraph(XElement element, List<WordBlock> output, bool body)
    {
        var id = Odf.Attr(element, Odf.Text, "list-id") ?? string.Empty;
        if (!_listsById.TryGetValue(id, out var counter))
        {
            counter = new ListCounter();
            _listsById[id] = counter;
        }

        var level = Math.Clamp(Odf.Int(element, Odf.Text, "level") ?? 1, 1, Levels) - 1;
        var list = new ListContext(_styles.ListStyle(Odf.Attr(element, Odf.Text, "style-name"), _scope), level, counter);
        foreach (var paragraph in element.Elements().Where(e => Odf.Is(e, Odf.Text, "p") || Odf.Is(e, Odf.Text, "h")))
        {
            ReadParagraph(paragraph, output, body, new ListItem(list, true, Odf.Int(element, Odf.Text, "start-value")));
        }
    }

    /// <summary>The level style of a list style (<c>text:list-level-style-*</c> with <c>text:level</c>, 1-based), or <c>null</c>.</summary>
    private static XElement? LevelStyle(XElement? listStyle, int level) =>
        listStyle?.Elements().FirstOrDefault(e => e.Name.Namespace == Odf.Text && (Odf.Int(e, Odf.Text, "level") ?? 1) == level + 1);

    /// <summary>
    /// A list paragraph's indents and label: the level's indents (replacing the paragraph style's, unless the paragraph
    /// sets its own), its number (with the higher levels it displays, prefix and suffix) or bullet, and the position
    /// of the tab after the label. For outline numbering (<paramref name="outlineOnly"/>), a level without a number
    /// changes nothing.
    /// </summary>
    private (ParagraphFormat Format, ListLabel? Label, double? Tab) ListParagraph(ParagraphFormat format, TextFormat text, ParagraphStyle style, ListItem item, bool outlineOnly)
    {
        var list = item.List;
        var listStyle = list.Style ?? _styles.ListStyle(style.ListStyle, _scope);
        var level = LevelStyle(listStyle, list.Level);
        if (level is null)
        {
            return (format, null, null);
        }

        var isNumber = level.Name.LocalName == "list-level-style-number" || Odf.Is(level, Odf.Text, "outline-level-style");
        string? labelText = null;
        if (item.Numbered)
        {
            labelText = isNumber ? Number(list, item.Start, listStyle!, level) : Bullet(level);
        }

        if (outlineOnly && string.IsNullOrEmpty(labelText))
        {
            return (format, null, null);
        }

        var properties = Odf.Child(level, Odf.Style, "list-level-properties");
        var suffix = LabelSuffix.Tab;
        double? tab = null;
        if (Odf.Attr(properties, Odf.Text, "list-level-position-and-space-mode") == "label-alignment")
        {
            var alignment = Odf.Child(properties, Odf.Style, "list-level-label-alignment");
            format = format with
            {
                IndentStart = Odf.Length(alignment, Odf.Fo, "margin-left") ?? format.IndentStart,
                FirstLineIndent = Odf.Length(alignment, Odf.Fo, "text-indent") ?? format.FirstLineIndent,
            };
            if (style.OwnProperties is { } own)
            {
                format = format with
                {
                    IndentStart = Odf.Length(own, Odf.Fo, "margin-left") ?? format.IndentStart,
                    FirstLineIndent = Odf.Length(own, Odf.Fo, "text-indent") ?? format.FirstLineIndent,
                };
            }

            suffix = Odf.Attr(alignment, Odf.Text, "label-followed-by") switch
            {
                "space" => LabelSuffix.Space,
                "nothing" or "newline" => LabelSuffix.Nothing,
                _ => LabelSuffix.Tab,
            };
            if (suffix == LabelSuffix.Tab && Odf.Length(alignment, Odf.Text, "list-tab-stop-position") is { } position)
            {
                tab = Math.Clamp(position, -1584, 1584);
            }
        }
        else
        {
            var before = Odf.Length(properties, Odf.Text, "space-before") ?? 0;
            var width = Math.Max(0, Odf.Length(properties, Odf.Text, "min-label-width") ?? 0);
            format = format with { IndentStart = format.IndentStart + before + width, FirstLineIndent = item.Numbered ? -width : 0 };
            suffix = width > 0 ? LabelSuffix.Tab : LabelSuffix.Space;
        }

        if (string.IsNullOrEmpty(labelText))
        {
            return (item.Numbered ? format : format with { FirstLineIndent = 0 }, null, null);
        }

        return (format, new ListLabel(labelText, LabelFormat(text, level, !isNumber), suffix), tab);
    }

    /// <summary>Counts an item at its level and writes its number with the higher levels it displays.</summary>
    private static string Number(ListContext list, int? start, XElement listStyle, XElement level)
    {
        var counter = list.Counter;
        var index = list.Level;
        if (start is { } value)
        {
            counter.Counts[index] = Math.Clamp(value, 0, 1_000_000_000);
        }
        else if (!counter.Started[index])
        {
            counter.Counts[index] = StartValue(level);
        }
        else if (counter.Counts[index] < 1_000_000_000)
        {
            counter.Counts[index]++;
        }

        counter.Started[index] = true;
        for (var deeper = index + 1; deeper < Levels; deeper++)
        {
            counter.Started[deeper] = false;
        }

        var display = Math.Clamp(Odf.Int(level, Odf.Text, "display-levels") ?? 1, 1, index + 1);
        var parts = new List<string>();
        for (var i = index - display + 1; i <= index; i++)
        {
            var style = i == index ? level : LevelStyle(listStyle, i);
            var number = i == index || counter.Started[i] ? counter.Counts[i] : StartValue(style);
            var format = Odf.NumberFormatOf(Odf.Attr(style, Odf.Style, "num-format") ?? "1");
            if (format != NumberFormat.None)
            {
                parts.Add(Numbering.Format(number, format));
            }
        }

        return Odf.Attr(level, Odf.Style, "num-prefix") + string.Join('.', parts) + Odf.Attr(level, Odf.Style, "num-suffix");
    }

    private static int StartValue(XElement? level) => Math.Clamp(Odf.Int(level, Odf.Text, "start-value") ?? 1, 0, 1_000_000_000);

    /// <summary>A bullet level's character: its bullet, or a round bullet for symbols of private fonts and pictures.</summary>
    private static string Bullet(XElement level)
    {
        if (level.Name.LocalName != "list-level-style-bullet")
        {
            return "\u2022";
        }

        var bullet = Odf.Attr(level, Odf.Text, "bullet-char");
        if (string.IsNullOrEmpty(bullet))
        {
            return "\u2022";
        }

        var rune = char.IsSurrogatePair(bullet, 0) ? bullet[..2] : bullet[..1];
        return rune[0] is >= '\uE000' and <= '\uF8FF' ? "\u2022" : rune;
    }

    /// <summary>
    /// A label's format: the paragraph's, without underline and background, with the level's text style and text
    /// properties. Symbol fonts (OpenSymbol, Wingdings) give way to the paragraph's font, and then the bullet's
    /// relative size, made for the large glyphs of those fonts, is not applied.
    /// </summary>
    private RunFormat LabelFormat(TextFormat text, XElement level, bool bullet)
    {
        var format = new TextFormat(text.Run with { Underline = UnderlineStyle.None, Background = null });
        format = _styles.Text(format, Odf.Attr(level, Odf.Text, "style-name"), _scope);
        format = _styles.ApplyText(format, Odf.Child(level, Odf.Style, "text-properties"));
        var run = format.Run;
        if (run.FontFamily is { } family && (family.Contains("Symbol", StringComparison.OrdinalIgnoreCase)
            || family.StartsWith("Wingdings", StringComparison.OrdinalIgnoreCase) || family.StartsWith("Webdings", StringComparison.OrdinalIgnoreCase)))
        {
            return run with { FontFamily = text.Run.FontFamily };
        }

        if (bullet && Odf.Percent(Odf.Attr(level, Odf.Text, "bullet-relative-size")) is { } percent && percent > 0)
        {
            run = run with { FontSize = Math.Clamp(run.FontSize * percent / 100, 1, 1638) };
        }

        return run;
    }
}
