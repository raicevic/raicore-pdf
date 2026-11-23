using System.Text;
using System.Xml.Linq;
using RaiCore.Pdf.Documents.Model;

namespace RaiCore.Pdf.Documents.Odt;

/// <summary>Paragraphs, headings, inline content, links, notes and fields.</summary>
internal sealed partial class OdtReader
{
    private readonly ListCounter _outline = new();
    private int _notes;
    private int _comments;
    private Dictionary<string, XElement>? _deletions;
    private readonly HashSet<string> _readingDeletions = new(StringComparer.Ordinal);
    private int _noteDepth;

    /// <summary>Whether the text read last ended with white space (or a paragraph started), for collapsing white space.</summary>
    private bool _afterSpace = true;

    /// <summary>Whether the cached number of a heading or list item (<c>text:number</c>) is left out (a label was made).</summary>
    private bool _skipNumber;

    /// <summary>Reads a paragraph or heading, with its list label when <paramref name="item"/> is given.</summary>
    private void ReadParagraph(XElement element, List<WordBlock> output, bool body, ListItem? item)
    {
        var heading = Odf.Is(element, Odf.Text, "h");
        var style = _styles.Paragraph(Odf.Attr(element, Odf.Text, "style-name"), _scope);
        var format = style.Format;
        var text = style.Text;
        ListLabel? label = null;
        double? labelTab = null;
        if (heading)
        {
            var level = Math.Clamp(Odf.Int(element, Odf.Text, "outline-level") ?? style.OutlineLevel ?? 1, 1, 10);
            format = format with { OutlineLevel = level - 1 };
            if (item is null && Odf.Attr(element, Odf.Text, "is-list-header") != "true" && _styles.OutlineStyle is { } outline)
            {
                var start = Odf.Attr(element, Odf.Text, "restart-numbering") == "true" ? Odf.Int(element, Odf.Text, "start-value") ?? 1 : (int?)null;
                (format, label, labelTab) = ListParagraph(format, text, style, new ListItem(new ListContext(outline, level - 1, _outline), true, start), outlineOnly: true);
            }
        }

        if (item is not null)
        {
            (format, label, labelTab) = ListParagraph(format, text, style, item, outlineOnly: false);
        }

        if (format.TabStops.Count > 0 && _tabsRelativeToIndent && format.IndentStart != 0)
        {
            format = format with { TabStops = [.. format.TabStops.Select(s => s with { Position = s.Position + format.IndentStart })] };
        }

        if (labelTab is { } tab && !format.TabStops.Any(s => Math.Abs(s.Position - tab) < 0.01))
        {
            format = format with { TabStops = [.. format.TabStops.Append(new TabStop(tab, TabAlignment.Left)).OrderBy(s => s.Position)] };
        }

        var paragraph = new WordParagraph { Format = format, MarkFormat = text.Run, Label = text.Run.Hidden ? null : label };
        _afterSpace = true;
        _skipNumber = paragraph.Label is not null;
        ReadInlines(element, paragraph.Inlines, text);
        _skipNumber = false;
        AddWithBreak(paragraph, output, body, style.MasterPage, style.PageNumber, format.PageBreakBefore, style.BreakAfter);
    }

    /// <summary>Reads the inline content of a paragraph, span, link or other inline container.</summary>
    private void ReadInlines(XElement container, List<WordInline> target, TextFormat format)
    {
        foreach (var node in container.Nodes())
        {
            if (node is XText text)
            {
                AddText(text.Value, format, target, collapse: true);
            }
            else if (node is XElement element)
            {
                ReadInline(element, target, format);
            }
        }
    }

    /// <summary>Reads one inline element.</summary>
    private void ReadInline(XElement element, List<WordInline> target, TextFormat format)
    {
        var ns = element.Name.Namespace;
        var name = element.Name.LocalName;
        if (ns == Odf.Draw)
        {
            ReadDrawing(element, target, format);
            return;
        }

        if (ns == Odf.Text)
        {
            ReadTextElement(element, name, target, format);
            return;
        }

        if (ns == Odf.Office && name == "annotation" && _context.Has(ReadExtras.Comments))
        {
            ReadComment(element, target);
            return;
        }

        if (ns == Odf.Office || ns == Odf.Style || ns == Odf.Table || ns == Odf.Svg || ns == Odf.Dc || ns == Odf.Meta || ns == Odf.Fo
            || ns.NamespaceName.StartsWith("urn:oasis:names:tc:opendocument:xmlns:", StringComparison.Ordinal))
        {
            return; // Annotations (comments) and other content that is not text.
        }

        ReadInlines(element, target, format); // Extensions (such as LibreOffice's): their text.
    }

    private void ReadTextElement(XElement element, string name, List<WordInline> target, TextFormat format)
    {
        switch (name)
        {
            case "span":
                ReadInlines(element, target, _styles.Text(format, Odf.Attr(element, Odf.Text, "style-name"), _scope));
                break;
            case "s":
                AddText(new string(' ', Math.Clamp(Odf.Int(element, Odf.Text, "c") ?? 1, 1, 1024)), format, target, collapse: false);
                break;
            case "tab":
                if (Shown(format))
                {
                    target.Add(new TabInline(format.Run));
                }

                _afterSpace = false;
                break;
            case "line-break":
                if (Shown(format))
                {
                    target.Add(new BreakInline(BreakKind.Line, format.Run));
                }

                _afterSpace = true;
                break;
            case "a":
                ReadLink(element, target, format);
                break;
            case "bookmark":
            case "bookmark-start":
                if (Odf.Attr(element, Odf.Text, "name") is { Length: > 0 } bookmark)
                {
                    target.Add(new BookmarkInline(bookmark));
                }

                break;
            case "note":
                ReadNote(element, target, format);
                break;
            case "page-number" when Odf.Attr(element, Odf.Text, "select-page") is null or "current":
                AddField(FieldKind.Page, element, target, format);
                break;
            case "page-count":
                AddField(FieldKind.NumPages, element, target, format);
                break;
            case "bookmark-ref" or "reference-ref" or "sequence-ref" or "note-ref" when Odf.Attr(element, Odf.Text, "reference-format") == "page":
                AddField(FieldKind.PageRef, element, target, format, Odf.Attr(element, Odf.Text, "ref-name"));
                break;
            case "ruby":
                if (Odf.Child(element, Odf.Text, "ruby-base") is { } rubyBase)
                {
                    ReadInlines(rubyBase, target, format);
                }

                break;
            case "hidden-text":
                if (Odf.Attr(element, Odf.Text, "string-value") is { } hiddenText)
                {
                    if (Odf.Attr(element, Odf.Text, "is-hidden") == "false")
                    {
                        AddText(hiddenText, format, target, collapse: true);
                    }
                    else if (_context.Has(ReadExtras.HiddenText))
                    {
                        AddText(hiddenText, format with { Run = format.Run with { Hidden = true } }, target, collapse: true);
                    }
                }

                break;
            case "change" when _context.Has(ReadExtras.DeletedText):
                ReadDeletion(Odf.Attr(element, Odf.Text, "change-id"), target, format);
                break;
            case "number" when _skipNumber:
            case "tracked-changes":
            case "hidden-paragraph":
            case "soft-page-break":
            case "p":
            case "h":
            case "list":
                break;
            default:
                ReadInlines(element, target, format); // Fields as their text, and other inline containers.
                break;
        }
    }

    /// <summary>
    /// Adds text: white space collapsed to single spaces and left out at the start of the paragraph (as ODF requires
    /// of the text in its XML, but not of <c>text:s</c>), the letter case changed; hidden text is left out.
    /// </summary>
    private void AddText(string text, TextFormat format, List<WordInline> target, bool collapse)
    {
        if (text.Length == 0)
        {
            return;
        }

        string value;
        var startsWord = _afterSpace;
        if (collapse)
        {
            var builder = new StringBuilder(text.Length);
            foreach (var c in text)
            {
                if (c is ' ' or '\t' or '\n' or '\r')
                {
                    if (!_afterSpace)
                    {
                        builder.Append(' ');
                        _afterSpace = true;
                    }
                }
                else
                {
                    builder.Append(c);
                    _afterSpace = false;
                }
            }

            value = builder.ToString();
        }
        else
        {
            value = text;
            _afterSpace = false;
        }

        if (value.Length == 0 || !Shown(format))
        {
            return;
        }

        value = format.Casing switch
        {
            Casing.Lower => value.ToLowerInvariant(),
            Casing.Capitalize => Capitalize(value, startsWord),
            _ => value,
        };
        target.Add(new TextRun(value, format.Run));
    }

    /// <summary>Whether text in a format is shown: hidden text only when text extraction keeps it.</summary>
    private bool Shown(TextFormat format) => !format.Run.Hidden || _context.Has(ReadExtras.HiddenText);

    /// <summary>
    /// For text extraction: the text a tracked change deleted (<c>text:change</c>, whose content is kept apart in
    /// <c>text:tracked-changes</c>), its paragraphs joined by spaces, as deleted text.
    /// </summary>
    private void ReadDeletion(string? id, List<WordInline> target, TextFormat format)
    {
        _deletions ??= Deletions();
        if (id is null || !_deletions.TryGetValue(id, out var deletion) || !_readingDeletions.Add(id))
        {
            return;
        }

        try
        {
            var deleted = format with { Run = format.Run with { Deleted = true } };
            var first = true;
            static bool IsParagraph(XElement e) => Odf.Is(e, Odf.Text, "p") || Odf.Is(e, Odf.Text, "h");
            foreach (var paragraph in deletion.Descendants().Where(e => IsParagraph(e) && !e.Ancestors().TakeWhile(a => a != deletion).Any(IsParagraph)))
            {
                if (!first)
                {
                    target.Add(new TextRun(" ", deleted.Run));
                }

                first = false;
                ReadInlines(paragraph, target, deleted);
            }
        }
        finally
        {
            _readingDeletions.Remove(id);
        }
    }

    /// <summary>The deletions of the document's tracked changes (<c>text:deletion</c> in a <c>text:changed-region</c>), by id.</summary>
    private Dictionary<string, XElement> Deletions()
    {
        var deletions = new Dictionary<string, XElement>(StringComparer.Ordinal);
        var text = Odf.Child(Odf.Child(_package.Content, Odf.Office, "body"), Odf.Office, "text");
        foreach (var region in Odf.Child(text, Odf.Text, "tracked-changes")?.Elements() ?? [])
        {
            if (Odf.Is(region, Odf.Text, "changed-region") && Odf.Child(region, Odf.Text, "deletion") is { } deletion
                && (Odf.Attr(region, Odf.Text, "id") ?? Odf.Attr(region, Odf.Xml, "id")) is { } id)
            {
                deletions.TryAdd(id, deletion);
            }
        }

        return deletions;
    }

    /// <summary>
    /// For text extraction: a comment (<c>office:annotation</c>), its paragraphs kept in the document's comments and
    /// referred to here (a comment in a comment is kept too, but not listed: the text follows references from the body).
    /// </summary>
    private void ReadComment(XElement element, List<WordInline> target)
    {
        var id = "o" + (++_comments).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var blocks = new List<WordBlock>();
        var (afterSpace, breakBefore, skipNumber) = (_afterSpace, _breakBeforeNext, _skipNumber);
        try
        {
            _skipNumber = false;
            ReadBlocks(element, blocks, body: false);
        }
        finally
        {
            (_afterSpace, _breakBeforeNext, _skipNumber) = (afterSpace, breakBefore, skipNumber);
        }

        _document.Comments[id] = blocks;
        target.Add(new CommentReference(id));
    }

    /// <summary>Text with the first letter of each word in capitals.</summary>
    private static string Capitalize(string text, bool startsWord)
    {
        var chars = text.ToCharArray();
        var start = startsWord;
        for (var i = 0; i < chars.Length; i++)
        {
            if (char.IsWhiteSpace(chars[i]))
            {
                start = true;
            }
            else
            {
                if (start)
                {
                    chars[i] = char.ToUpperInvariant(chars[i]);
                }

                start = false;
            }
        }

        return new string(chars);
    }

    /// <summary>Adds a field whose value the renderer computes, with the document's text as the fallback.</summary>
    private void AddField(FieldKind kind, XElement element, List<WordInline> target, TextFormat format, string? argument = null)
    {
        _afterSpace = false;
        if (format.Run.Hidden)
        {
            return;
        }

        var numberFormat = Odf.Attr(element, Odf.Style, "num-format");
        target.Add(new FieldInline(kind, format.Run, Odf.TextOf(element))
        {
            Argument = argument,
            NumberFormat = numberFormat is null ? null : Odf.NumberFormatOf(numberFormat),
        });
    }

    /// <summary>Reads a link (<c>text:a</c>): to a web address, or to a bookmark (<c>#name</c>) in the document.</summary>
    private void ReadLink(XElement element, List<WordInline> target, TextFormat format)
    {
        var href = Odf.Attr(element, Odf.XLink, "href")?.Trim() ?? string.Empty;
        var link = new HyperlinkInline
        {
            Uri = href.StartsWith('#') ? null : LinkUri(href),
            Anchor = href.StartsWith('#') && href.Length > 1 ? Unescape(href[1..]) : null,
        };
        ReadInlines(element, link.Inlines, _styles.Text(format, Odf.Attr(element, Odf.Text, "style-name"), _scope));
        target.Add(link);
    }

    /// <summary>An absolute web, mail, FTP, telephone or news address; others (relative paths, files) give <c>null</c>.</summary>
    internal static Uri? LinkUri(string address) =>
        Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "mailto" or "ftp" or "tel" or "news" ? uri : null;

    private static string Unescape(string text)
    {
        try
        {
            return Uri.UnescapeDataString(text);
        }
        catch (UriFormatException)
        {
            return text;
        }
    }

    /// <summary>
    /// Reads a footnote or endnote: its citation in the text (the note's number, or its own label) and its body,
    /// which starts with the number.
    /// </summary>
    private void ReadNote(XElement element, List<WordInline> target, TextFormat format)
    {
        _afterSpace = false;
        if (_noteDepth > 0 || format.Run.Hidden)
        {
            return; // A note in a note is left out, as word processors do not allow it.
        }

        var noteClass = Odf.Attr(element, Odf.Text, "note-class") == "endnote" ? "endnote" : "footnote";
        var kind = noteClass == "endnote" ? NoteKind.Endnote : NoteKind.Footnote;
        var configuration = _styles.NotesConfiguration(noteClass);
        var id = "n" + (++_notes).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var blocks = new List<WordBlock>();
        var (afterSpace, breakBefore, skipNumber) = (_afterSpace, _breakBeforeNext, _skipNumber);
        _noteDepth++;
        try
        {
            _skipNumber = false;
            if (Odf.Child(element, Odf.Text, "note-body") is { } body)
            {
                ReadBlocks(body, blocks, body: false);
            }
        }
        finally
        {
            _noteDepth--;
            (_afterSpace, _breakBeforeNext, _skipNumber) = (afterSpace, breakBefore, skipNumber);
        }

        if (blocks.FirstOrDefault() is not WordParagraph first)
        {
            first = new WordParagraph { Format = _styles.Paragraph(null, _scope).Format };
            blocks.Insert(0, first);
        }

        var markFormat = _styles.Text(new TextFormat(first.MarkFormat), Odf.Attr(configuration, Odf.Text, "citation-body-style-name"), _scope).Run;
        first.Inlines.Insert(0, new NoteMark(markFormat));
        first.Inlines.Insert(1, new TextRun(" ", markFormat));
        (kind == NoteKind.Footnote ? _document.Footnotes : _document.Endnotes)[id] = blocks;

        var citationStyle = Odf.Attr(configuration, Odf.Text, "citation-style-name");
        var citation = citationStyle is not null && _styles.Find("text", citationStyle, _scope) is not null
            ? _styles.Text(format, citationStyle, _scope).Run
            : format.Run with { Position = VerticalPosition.Superscript };
        var label = Odf.Attr(Odf.Child(element, Odf.Text, "note-citation"), Odf.Text, "label");
        target.Add(new NoteReference(kind, id, citation) { CustomMark = label is { Length: > 0 } ? label : null });
    }
}
