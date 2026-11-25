using System.Globalization;
using System.Text;
using System.Xml.Linq;
using RaiCore.Pdf.Documents.Model;

namespace RaiCore.Pdf.Documents.Docx;

/// <summary>Paragraphs, runs and fields.</summary>
internal sealed partial class DocxReader
{
    /// <summary>The most fields nested in each other that are tracked; deeper ones are read as plain text.</summary>
    private const int MaxFieldDepth = 32;

    private readonly List<FieldState> _fields = [];

    /// <summary>What a field does with its result.</summary>
    private enum FieldAction
    {
        /// <summary>Its last result is shown as it is.</summary>
        Keep,

        /// <summary>Its value is computed by the renderer (<see cref="FieldInline"/>); the last result is the fallback.</summary>
        Compute,

        /// <summary>Its result is a hyperlink.</summary>
        Link,
    }

    /// <summary>A complex field (<c>w:fldChar</c>) being read.</summary>
    private sealed class FieldState(RunFormat format)
    {
        public StringBuilder Instruction { get; } = new();

        /// <summary>Whether the result has started (<c>separate</c>); before it, runs are instruction text.</summary>
        public bool InResult { get; set; }

        public FieldAction Action { get; set; }

        public FieldKind Kind { get; set; }

        public string? Argument { get; set; }

        public NumberFormat? NumberFormat { get; set; }

        public Uri? Uri { get; set; }

        public string? Anchor { get; set; }

        public RunFormat Format { get; set; } = format;

        /// <summary>The field's start in the model, when field codes are kept (<see cref="ReadExtras.FieldCodes"/>).</summary>
        public FieldStart? Marker { get; set; }

        public bool HasResultFormat { get; set; }

        public StringBuilder Result { get; } = new();

        /// <summary>The hyperlink the result goes into, and the list it is in (a new one starts in each paragraph).</summary>
        public (HyperlinkInline Link, List<WordInline> Owner)? Link { get; set; }
    }

    /// <summary>Reads a paragraph: its format resolved through the style hierarchy, its list label and its content.</summary>
    private WordParagraph ReadParagraph(XElement p, TableContext table)
    {
        var pPr = Ooxml.Child(p, "pPr");
        var styleId = Ooxml.Val(pPr, "pStyle") ?? _styles.DefaultParagraphStyle;
        var chain = _styles.Chain(styleId, "paragraph");

        // Table style formatting comes before the paragraph style, except over the default paragraph style.
        var isDefaultStyle = chain.Count == 0 || chain[^1].Id == _styles.DefaultParagraphStyle;
        var format = _styles.DefaultParagraph;
        var run = _styles.DefaultRun;
        if (!isDefaultStyle)
        {
            (format, run) = ApplyTable(format, run, table);
        }

        string? numId = null;
        int? level = null;
        foreach (var style in chain)
        {
            format = DocxProperties.ApplyParagraph(format, style.ParagraphProperties, _theme);
            run = DocxProperties.ApplyRun(run, style.RunProperties, _theme);
            if (Ooxml.Child(style.ParagraphProperties, "numPr") is { } styleNumbering)
            {
                numId = Ooxml.Val(styleNumbering, "numId") ?? numId;
                level = Ooxml.Int(Ooxml.Child(styleNumbering, "ilvl"), "val") ?? level;
            }
        }

        if (isDefaultStyle)
        {
            (format, run) = ApplyTable(format, run, table);
        }

        if (Ooxml.Child(pPr, "numPr") is { } numbering)
        {
            numId = Ooxml.Val(numbering, "numId") ?? numId;
            level = Ooxml.Int(Ooxml.Child(numbering, "ilvl"), "val") ?? level;
        }

        var levelDefinition = numId is null or "0" ? null : _numbering.Level(numId, level ?? 0, _styles);
        format = DocxProperties.ApplyParagraph(format, levelDefinition?.ParagraphProperties, _theme);
        format = DocxProperties.ApplyParagraph(format, pPr, _theme) with { StyleId = styleId };

        var paragraph = new WordParagraph { Format = format, MarkFormat = DocxProperties.ApplyRun(run, Ooxml.Child(pPr, "rPr"), _theme) };
        if (levelDefinition is not null && _numbering.Next(numId!, level ?? 0, _styles) is { } label && !paragraph.MarkFormat.Hidden)
        {
            var labelFormat = DocxProperties.ApplyRun(paragraph.MarkFormat with { Underline = UnderlineStyle.None, Background = null }, levelDefinition.RunProperties, _theme);
            if (IsSymbolFont(labelFormat.FontFamily))
            {
                labelFormat = labelFormat with { FontFamily = paragraph.MarkFormat.FontFamily };
            }

            paragraph.Label = new ListLabel(label, labelFormat, levelDefinition.Suffix);
        }

        paragraph.Inlines.AddRange(_pendingBookmarks);
        _pendingBookmarks.Clear();
        ReadInlines(p, paragraph.Inlines, run);
        EndParagraphFields(paragraph.Inlines);
        return paragraph;
    }

    private (ParagraphFormat, RunFormat) ApplyTable(ParagraphFormat format, RunFormat run, TableContext table)
    {
        foreach (var properties in table.ParagraphProperties)
        {
            format = DocxProperties.ApplyParagraph(format, properties, _theme);
        }

        foreach (var properties in table.RunProperties)
        {
            run = DocxProperties.ApplyRun(run, properties, _theme);
        }

        return (format, run);
    }

    private static bool IsSymbolFont(string? font) => font is not null
        && (font.Equals("Symbol", StringComparison.OrdinalIgnoreCase) || font.StartsWith("Wingdings", StringComparison.OrdinalIgnoreCase)
            || font.StartsWith("Webdings", StringComparison.OrdinalIgnoreCase));

    /// <summary>Reads the inline content of a paragraph, hyperlink or other inline container.</summary>
    private void ReadInlines(XElement container, List<WordInline> target, RunFormat paragraphRun)
    {
        foreach (var element in container.Elements())
        {
            var ns = element.Name.NamespaceName;
            if (ns == Ooxml.MarkupCompatibility && element.Name.LocalName == "AlternateContent")
            {
                if (Choose(element) is { } chosen)
                {
                    ReadInlines(chosen, target, paragraphRun);
                }

                continue;
            }

            if (element.Name.LocalName is "oMath" or "oMathPara" && ns.Contains("math", StringComparison.OrdinalIgnoreCase))
            {
                ReadMath(element, target, paragraphRun);
                continue;
            }

            if (!Ooxml.IsW(element))
            {
                continue;
            }

            switch (element.Name.LocalName)
            {
                case "r":
                    ReadRun(element, target, paragraphRun);
                    break;
                case "hyperlink":
                    ReadHyperlink(element, target, paragraphRun);
                    break;
                case "fldSimple":
                    ReadSimpleField(element, target, paragraphRun);
                    break;
                case "sdt":
                    if (Ooxml.Child(element, "sdtContent") is { } content)
                    {
                        ReadInlines(content, target, paragraphRun);
                    }

                    break;
                case "del":
                case "moveFrom":
                    if (_context.Has(ReadExtras.DeletedText))
                    {
                        ReadInlines(element, target, paragraphRun with { Deleted = true });
                    }

                    break;
                case "ins":
                case "moveTo":
                case "smartTag":
                case "customXml":
                case "dir":
                case "bdo":
                    ReadInlines(element, target, paragraphRun);
                    break;
                case "commentRangeStart" when _context.Has(ReadExtras.Comments) && Ooxml.Attr(element, "id") is { } start:
                    Emit(new CommentStart(start), target);
                    break;
                case "commentRangeEnd" when _context.Has(ReadExtras.Comments) && Ooxml.Attr(element, "id") is { } end:
                    Emit(new CommentEnd(end), target);
                    break;
                case "bookmarkStart":
                    if (Ooxml.Attr(element, "name") is { Length: > 0 } name && !name.StartsWith("_GoBack", StringComparison.Ordinal))
                    {
                        Emit(new BookmarkInline(name), target);
                    }

                    break;
            }
        }
    }

    /// <summary>Reads a run: its format (character style and direct properties) and its content.</summary>
    private void ReadRun(XElement r, List<WordInline> target, RunFormat paragraphRun)
    {
        var rPr = Ooxml.Child(r, "rPr");
        var format = paragraphRun;
        foreach (var style in _styles.Chain(Ooxml.Val(rPr, "rStyle"), "character"))
        {
            format = DocxProperties.ApplyRun(format, style.RunProperties, _theme);
        }

        format = DocxProperties.ApplyRun(format, rPr, _theme);
        ReadRunContent(r, target, format);
    }

    private void ReadRunContent(XElement r, List<WordInline> target, RunFormat format)
    {
        foreach (var element in r.Elements())
        {
            if (format.Hidden && element.Name.LocalName is not ("fldChar" or "instrText" or "t") && !_context.Has(ReadExtras.HiddenText))
            {
                continue; // Hidden text is not shown; its text still sets a field result's format.
            }

            if (element.Name.NamespaceName == Ooxml.MarkupCompatibility && element.Name.LocalName == "AlternateContent")
            {
                if (Choose(element) is { } chosen)
                {
                    ReadRunContent(chosen, target, format);
                }

                continue;
            }

            if (!Ooxml.IsW(element))
            {
                continue;
            }

            switch (element.Name.LocalName)
            {
                case "t":
                    AddText(element.Value, format, target);
                    break;
                case "delText":
                    if (format.Deleted)
                    {
                        AddText(element.Value, format, target);
                    }

                    break;
                case "commentReference":
                    if (!_context.Has(ReadExtras.Comments))
                    {
                        _context.ReportCommentsLeftOut();
                    }
                    else if (Ooxml.Attr(element, "id") is { } comment)
                    {
                        Emit(new CommentReference(comment), target);
                    }

                    break;
                case "instrText":
                    if (_fields.Count > 0 && !_fields[^1].InResult)
                    {
                        _fields[^1].Instruction.Append(element.Value);
                    }

                    break;
                case "tab":
                    Emit(new TabInline(format), target);
                    break;
                case "ptab":
                    Emit(new TabInline(format)
                    {
                        PositionalAlignment = Ooxml.Attr(element, "alignment") switch
                        {
                            "center" => WordAlignment.Center,
                            "right" => WordAlignment.End,
                            _ => WordAlignment.Start,
                        },
                    }, target);
                    break;
                case "br":
                    var kind = Ooxml.Attr(element, "type") switch
                    {
                        "page" => BreakKind.Page,
                        "column" => BreakKind.Column,
                        _ => BreakKind.Line,
                    };
                    Emit(new BreakInline(kind, format), target);
                    break;
                case "cr":
                    Emit(new BreakInline(BreakKind.Line, format), target);
                    break;
                case "noBreakHyphen":
                    AddText("\u2011", format, target);
                    break;
                case "softHyphen":
                    AddText("\u00AD", format, target);
                    break;
                case "sym":
                    if (Ooxml.Attr(element, "char") is { } code && int.TryParse(code, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
                    {
                        AddText(Ooxml.SymbolCharacter(value, Ooxml.Attr(element, "font")), format, target);
                    }

                    break;
                case "fldChar":
                    FieldCharacter(Ooxml.Attr(element, "fldCharType"), format, target);
                    break;
                case "pgNum":
                    Emit(new FieldInline(FieldKind.Page, format, null), target);
                    break;
                case "drawing":
                    ReadDrawing(element, format, target);
                    break;
                case "pict":
                case "object":
                    ReadVml(element, target);
                    break;
                case "footnoteReference":
                case "endnoteReference":
                    if (Ooxml.Attr(element, "id") is { } id && !format.Hidden)
                    {
                        var customMark = Ooxml.Attr(element, "customMarkFollows") is { } follows && Ooxml.IsOn(follows);
                        Emit(new NoteReference(element.Name.LocalName == "footnoteReference" ? NoteKind.Footnote : NoteKind.Endnote, id, format)
                        {
                            CustomMark = customMark ? string.Empty : null,
                        }, target);
                    }

                    break;
                case "footnoteRef":
                case "endnoteRef":
                    Emit(new NoteMark(format), target);
                    break;
                case "ruby":
                    if (Ooxml.Child(element, "rubyBase") is { } rubyBase)
                    {
                        ReadInlines(rubyBase, target, format);
                    }

                    break;
            }
        }
    }

    /// <summary>Adds text (tabs and line feeds in it become tabs and line breaks); hidden text is left out.</summary>
    private void AddText(string text, RunFormat format, List<WordInline> target)
    {
        if (text.Length == 0)
        {
            return;
        }

        if (_fields.Count > 0 && _fields[^1] is { InResult: true, HasResultFormat: false } field)
        {
            field.Format = format;
            field.HasResultFormat = true;
        }

        if (format.Hidden && !_context.Has(ReadExtras.HiddenText))
        {
            return;
        }

        var start = 0;
        for (var i = 0; i <= text.Length; i++)
        {
            if (i == text.Length || text[i] is '\t' or '\n' or '\r')
            {
                if (i > start)
                {
                    Emit(new TextRun(text[start..i], format), target);
                }

                if (i < text.Length)
                {
                    Emit(text[i] == '\t' ? new TabInline(format) : new BreakInline(BreakKind.Line, format), target);
                }

                start = i + 1;
            }
        }
    }

    /// <summary>
    /// Adds inline content where the fields being read put it: nowhere inside a field's instruction, into the fallback
    /// text of a computed field, into the hyperlink of a link field, otherwise into <paramref name="target"/>.
    /// </summary>
    /// <returns>Whether the content was added to the model (not to a computed field's fallback text, nor left out).</returns>
    private bool Emit(WordInline inline, List<WordInline> target)
    {
        FieldState? link = null;
        foreach (var field in _fields)
        {
            if (!field.InResult)
            {
                return false;
            }

            if (field.Action == FieldAction.Compute)
            {
                if (inline is TextRun { Format.Hidden: false, Format.Deleted: false } text)
                {
                    field.Result.Append(text.Text);
                }

                return false;
            }

            if (field.Action == FieldAction.Link)
            {
                link = field;
            }
        }

        if (link is not null)
        {
            if (link.Link is not { } current || !ReferenceEquals(current.Owner, target))
            {
                var hyperlink = new HyperlinkInline { Uri = link.Uri, Anchor = link.Anchor };
                target.Add(hyperlink);
                link.Link = (hyperlink, target);
            }

            link.Link.Value.Link.Inlines.Add(inline);
            return true;
        }

        target.Add(inline);
        return true;
    }

    /// <summary>Handles a <c>w:fldChar</c>: the begin, separator and end of a complex field.</summary>
    private void FieldCharacter(string? type, RunFormat format, List<WordInline> target)
    {
        switch (type)
        {
            case "begin":
                if (_fields.Count < MaxFieldDepth)
                {
                    var marker = _context.Has(ReadExtras.FieldCodes) ? new FieldStart() : null;
                    var placed = marker is not null && Emit(marker, target);
                    _fields.Add(new FieldState(format) { Marker = placed ? marker : null });
                }

                break;
            case "separate":
                if (_fields.Count > 0 && !_fields[^1].InResult)
                {
                    Classify(_fields[^1]);
                    _fields[^1].InResult = true;
                }

                break;
            case "end":
                if (_fields.Count > 0)
                {
                    var field = _fields[^1];
                    _fields.RemoveAt(_fields.Count - 1);
                    if (!field.InResult)
                    {
                        Classify(field);
                    }

                    if (field.Action == FieldAction.Compute)
                    {
                        Emit(Computed(field), target);
                    }

                    if (field.Marker is not null)
                    {
                        Emit(new FieldEnd(), target);
                    }
                }

                break;
        }
    }

    private static FieldInline Computed(FieldState field) =>
        new(field.Kind, field.Format, field.Result.Length > 0 ? field.Result.ToString() : null) { Argument = field.Argument, NumberFormat = field.NumberFormat };

    /// <summary>
    /// At the end of a paragraph: fields still in their instruction or computing their value (which never span
    /// paragraphs in valid documents) are closed, so a damaged field cannot swallow the rest of the document.
    /// </summary>
    private void EndParagraphFields(List<WordInline> paragraph)
    {
        while (_fields.Count > 0 && (!_fields[^1].InResult || _fields[^1].Action == FieldAction.Compute))
        {
            if (_fields[^1].Marker is { } marker)
            {
                marker.Code = _fields[^1].Instruction.ToString();
                paragraph.Add(new FieldEnd());
            }

            _fields.RemoveAt(_fields.Count - 1);
        }
    }

    /// <summary>Decides what a field does from its instruction (<c>PAGE</c>, <c>HYPERLINK "..."</c>).</summary>
    private static void Classify(FieldState field)
    {
        if (field.Marker is { } marker)
        {
            marker.Code = field.Instruction.ToString();
        }

        var tokens = Tokenize(field.Instruction.ToString());
        if (tokens.Count == 0)
        {
            return;
        }

        switch (tokens[0].ToUpperInvariant())
        {
            case "PAGE":
                (field.Action, field.Kind) = (FieldAction.Compute, FieldKind.Page);
                field.NumberFormat = FormatSwitch(tokens);
                break;
            case "NUMPAGES":
                (field.Action, field.Kind) = (FieldAction.Compute, FieldKind.NumPages);
                field.NumberFormat = FormatSwitch(tokens) ?? NumberFormat.Decimal;
                break;
            case "SECTIONPAGES":
                (field.Action, field.Kind) = (FieldAction.Compute, FieldKind.SectionPages);
                field.NumberFormat = FormatSwitch(tokens) ?? NumberFormat.Decimal;
                break;
            case "PAGEREF":
                if (tokens.Count > 1 && !tokens[1].StartsWith('\\'))
                {
                    (field.Action, field.Kind, field.Argument) = (FieldAction.Compute, FieldKind.PageRef, tokens[1]);
                    field.NumberFormat = FormatSwitch(tokens);
                }

                break;
            case "HYPERLINK":
                string? address = null, anchor = null;
                for (var i = 1; i < tokens.Count; i++)
                {
                    if (tokens[i] == "\\l" && i + 1 < tokens.Count)
                    {
                        anchor = tokens[++i];
                    }
                    else if (tokens[i].StartsWith('\\'))
                    {
                        i += tokens[i] is "\\o" or "\\t" ? 1 : 0;
                    }
                    else
                    {
                        address ??= tokens[i];
                    }
                }

                var uri = address is null ? null : LinkUri(address, anchor);
                if (uri is not null || anchor is not null)
                {
                    (field.Action, field.Uri, field.Anchor) = (FieldAction.Link, uri, uri is null ? anchor : null);
                }

                break;
        }
    }

    /// <summary>The number format of a field's <c>\*</c> switch, or <c>null</c>.</summary>
    internal static NumberFormat? FormatSwitch(List<string> tokens)
    {
        for (var i = 1; i + 1 < tokens.Count; i++)
        {
            if (tokens[i] == "\\*")
            {
                switch (tokens[i + 1])
                {
                    case "roman":
                        return NumberFormat.LowerRoman;
                    case "ROMAN":
                        return NumberFormat.UpperRoman;
                    case "alphabetic":
                        return NumberFormat.LowerLetter;
                    case "ALPHABETIC":
                        return NumberFormat.UpperLetter;
                    case "Arabic":
                        return NumberFormat.Decimal;
                    case "Ordinal":
                        return NumberFormat.Ordinal;
                }
            }
        }

        return null;
    }

    /// <summary>Splits a field instruction into words and quoted strings (without the quotes).</summary>
    internal static List<string> Tokenize(string instruction)
    {
        var tokens = new List<string>();
        var i = 0;
        while (i < instruction.Length && tokens.Count < 64)
        {
            if (char.IsWhiteSpace(instruction[i]))
            {
                i++;
                continue;
            }

            if (instruction[i] == '"')
            {
                var end = instruction.IndexOf('"', i + 1);
                end = end < 0 ? instruction.Length : end;
                tokens.Add(instruction[(i + 1)..end]);
                i = end + 1;
                continue;
            }

            var start = i;
            while (i < instruction.Length && !char.IsWhiteSpace(instruction[i]) && instruction[i] != '"')
            {
                i++;
            }

            tokens.Add(instruction[start..i]);
        }

        return tokens;
    }

    /// <summary>
    /// A link's address as a URI for the PDF: absolute web, mail, FTP, telephone and news addresses (with the anchor as
    /// fragment); relative paths, files and other schemes give <c>null</c>.
    /// </summary>
    internal static Uri? LinkUri(string address, string? anchor)
    {
        address = address.Trim();
        if (anchor is { Length: > 0 } && !address.Contains('#', StringComparison.Ordinal))
        {
            address += "#" + anchor;
        }

        return Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "mailto" or "ftp" or "tel" or "news"
            ? uri
            : null;
    }

    /// <summary>Reads a <c>w:hyperlink</c>: to an external address (by relationship) or to a bookmark (<c>w:anchor</c>).</summary>
    private void ReadHyperlink(XElement element, List<WordInline> target, RunFormat paragraphRun)
    {
        var anchor = Ooxml.Attr(element, "anchor");
        Uri? uri = null;
        if (RelationshipId(element) is { } id && _relationships.TryGetValue(id, out var relationship) && relationship.External)
        {
            uri = LinkUri(relationship.Target, anchor);
        }

        if (uri is null && anchor is null)
        {
            ReadInlines(element, target, paragraphRun);
            return;
        }

        var link = new HyperlinkInline { Uri = uri, Anchor = uri is null ? anchor : null };
        ReadInlines(element, link.Inlines, paragraphRun);
        if (link.Inlines.Count > 0)
        {
            Emit(link, target);
        }
    }

    /// <summary>Reads a <c>w:fldSimple</c>: a field whose instruction is an attribute and whose result is its content.</summary>
    private void ReadSimpleField(XElement element, List<WordInline> target, RunFormat paragraphRun)
    {
        var field = new FieldState(paragraphRun);
        field.Instruction.Append(Ooxml.Attr(element, "instr"));
        Classify(field);
        var marked = _context.Has(ReadExtras.FieldCodes) && Emit(new FieldStart { Code = field.Instruction.ToString() }, target);
        ReadSimpleFieldResult(element, target, paragraphRun, field);
        if (marked)
        {
            Emit(new FieldEnd(), target);
        }
    }

    private void ReadSimpleFieldResult(XElement element, List<WordInline> target, RunFormat paragraphRun, FieldState field)
    {
        switch (field.Action)
        {
            case FieldAction.Compute:
                var result = new List<WordInline>();
                ReadInlines(element, result, paragraphRun);
                var shown = result.OfType<TextRun>().Where(r => !r.Format.Hidden && !r.Format.Deleted).ToList();
                field.Format = shown.FirstOrDefault()?.Format ?? paragraphRun;
                foreach (var text in shown)
                {
                    field.Result.Append(text.Text);
                }

                Emit(Computed(field), target);
                break;
            case FieldAction.Link:
                var link = new HyperlinkInline { Uri = field.Uri, Anchor = field.Anchor };
                ReadInlines(element, link.Inlines, paragraphRun);
                if (link.Inlines.Count > 0)
                {
                    Emit(link, target);
                }

                break;
            default:
                ReadInlines(element, target, paragraphRun);
                break;
        }
    }

    /// <summary>Reads an equation (Office Math) as its text, in the paragraph's format.</summary>
    private void ReadMath(XElement math, List<WordInline> target, RunFormat format)
    {
        var text = new StringBuilder();
        foreach (var element in math.Descendants())
        {
            if (element.Name.LocalName == "t")
            {
                text.Append(element.Value);
            }
        }

        if (text.Length > 0)
        {
            _context.Report(DocumentDiagnosticKind.UnsupportedContent, "An equation is shown as plain text.");
            AddText(text.ToString(), format, target);
        }
    }
}
