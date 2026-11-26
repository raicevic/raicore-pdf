using RaiCore.Pdf.Documents.Model;
using RaiCore.Pdf.Layout;

namespace RaiCore.Pdf.Documents.Rendering;

/// <summary>Inline content: runs, list labels, tabs, breaks, inline images, fields, links, bookmarks and note marks.</summary>
internal sealed partial class DocumentRenderer
{
    /// <summary>The least width of a tab, in points, when its stop leaves no room.</summary>
    private const double MinimumTab = 1.5;

    /// <summary>
    /// A piece of inline content with the link it is in, and the text it shows when that is decided before layout
    /// (note numbers).
    /// </summary>
    private readonly record struct Atom(WordInline Inline, LinkTarget? Link, string? Text = null);

    /// <summary>
    /// Flattens inline content: hyperlinks become the link of their atoms, anchored images and text boxes are set
    /// aside, and note references get their numbers (in document order).
    /// </summary>
    private void Flatten(List<WordInline> inlines, LinkTarget? link, List<Atom> atoms, List<AnchoredBlocks> anchored, List<ImageInline> images)
    {
        foreach (var inline in inlines)
        {
            if (FlattenComment(inline, link, atoms))
            {
                continue;
            }

            switch (inline)
            {
                case HyperlinkInline hyperlink:
                    var target = hyperlink.Uri is { } uri ? LinkTarget.ToUri(uri)
                        : hyperlink.Anchor is { Length: > 0 } anchor ? LinkTarget.ToDestination(anchor)
                        : null;
                    Flatten(hyperlink.Inlines, target ?? link, atoms, anchored, images);
                    break;
                case AnchoredBlocks blocks:
                    anchored.Add(blocks);
                    break;
                case TextRun { Format.Hidden: true } or TextRun { Format.Deleted: true }:
                    break; // Hidden text and deleted text (of a tracked change) are not shown.
                case ImageInline { Placement: not ImagePlacement.Inline } image:
                    images.Add(image);
                    break;
                case NoteReference reference:
                    if (_page is not null || _depth > 0)
                    {
                        break; // Notes are numbered and listed from the body only.
                    }

                    int number;
                    string prefix;
                    if (reference.Kind == NoteKind.Footnote)
                    {
                        number = ++_footnoteNumber;
                        prefix = "fn";
                        _pendingFootnotes.Add((reference.Kind, reference.Id, number));
                    }
                    else
                    {
                        number = ++_endnoteNumber;
                        prefix = "en";
                        _endnotes.Add((reference.Id, number));
                    }

                    var format = reference.Kind == NoteKind.Footnote ? _document.FootnoteFormat : _document.EndnoteFormat;
                    var text = reference.CustomMark ?? Numbering.Format(number, format);
                    if (text.Length > 0)
                    {
                        atoms.Add(new Atom(reference, LinkTarget.ToDestination(NoteDestination(prefix, number)), text));
                    }

                    break;
                default:
                    atoms.Add(new Atom(inline, link));
                    break;
            }
        }
    }

    /// <summary>
    /// The paragraph's text as layout paragraphs, one per page or column break (with the break before it): the list
    /// label, runs, tabs measured to their stops, line breaks, inline images, fields, links, bookmarks and note marks.
    /// A column break starts a new column in a section with columns, and a new page in others, as in Word.
    /// </summary>
    private List<(Paragraph Paragraph, PageBreak AfterBreak)> Segments(WordParagraph paragraph, List<Atom> atoms, double textWidth)
    {
        var format = paragraph.Format;
        var segments = new List<(Paragraph, PageBreak)>();
        var current = NewParagraph(paragraph, first: true);
        var afterBreak = PageBreak.Auto;
        var hasContent = false;
        LinkTarget? openLink = null;
        var lineAtoms = new List<Atom>();
        var origin = format.IndentStart + format.FirstLineIndent;
        var columnWidth = textWidth + Math.Max(0, format.IndentStart) + Math.Max(0, format.IndentEnd);

        if (paragraph.Label is { } label && label.Text.Length > 0)
        {
            var labelRun = new TextRun(label.Text, label.Format);
            var labelAtom = new Atom(labelRun, null);
            AddAtom(current, labelAtom, format);
            hasContent = true;
            switch (label.Suffix)
            {
                case LabelSuffix.Tab:
                    var x = origin + Measure([labelAtom], format);
                    var (stop, _, _) = NextStop(x, format);
                    var width = Math.Max(MinimumTab, stop - x);
                    AddSpacer(current, width);
                    origin = x + width;
                    break;
                case LabelSuffix.Space:
                    AddAtom(current, new Atom(new TextRun(" ", label.Format), null), format);
                    origin += Measure([labelAtom, new Atom(new TextRun(" ", label.Format), null)], format);
                    break;
                default:
                    origin += Measure([labelAtom], format);
                    break;
            }
        }

        for (var i = 0; i < atoms.Count; i++)
        {
            var atom = atoms[i];
            if (atom.Inline is BreakInline { Kind: not BreakKind.Line } breakInline)
            {
                CloseLink(current, ref openLink);
                if (hasContent)
                {
                    segments.Add((current, afterBreak));
                }

                // A page break wins over a column break before the same text.
                afterBreak = breakInline.Kind == BreakKind.Column && _inColumns && afterBreak != PageBreak.Page ? PageBreak.Column : PageBreak.Page;
                current = NewParagraph(paragraph, first: false);
                hasContent = false;
                lineAtoms.Clear();
                origin = format.IndentStart;
                continue;
            }

            if (!ReferenceEquals(openLink, atom.Link))
            {
                CloseLink(current, ref openLink);
                if (atom.Link is { } link)
                {
                    current.BeginLink(link);
                    openLink = link;
                }
            }

            switch (atom.Inline)
            {
                case TabInline tab:
                    var x = origin + Measure(lineAtoms, format);
                    var following = new List<Atom>();
                    for (var j = i + 1; j < atoms.Count && atoms[j].Inline is not (TabInline or BreakInline); j++)
                    {
                        following.Add(atoms[j]);
                    }

                    origin = x + AddTab(current, tab, x, following, format, columnWidth);
                    lineAtoms.Clear();
                    hasContent = true;
                    break;
                case BreakInline:
                    current.Add("\n", _styles.Style(atom.Inline is BreakInline line ? line.Format : paragraph.MarkFormat, format));
                    lineAtoms.Clear();
                    origin = format.IndentStart;
                    hasContent = true;
                    break;
                case BookmarkInline bookmark:
                    if (_page is null)
                    {
                        current.AddAnchor(bookmark.Name);
                    }

                    break;
                case CommentStart when atom.Text is { } noteAnchor:
                    current.AddAnchor(noteAnchor); // Where a comment's note goes (DocumentComments.Annotations).
                    break;
                default:
                    if (AddAtom(current, atom, format, textWidth))
                    {
                        lineAtoms.Add(atom);
                        hasContent = true;
                    }

                    break;
            }
        }

        CloseLink(current, ref openLink);
        if (!hasContent)
        {
            // An empty paragraph (or what follows a page break at its end) still takes a line.
            current.Add("\u200B", _styles.Style(paragraph.MarkFormat, format));
        }

        segments.Add((current, afterBreak));
        return segments;
    }

    private static void CloseLink(Paragraph paragraph, ref LinkTarget? openLink)
    {
        if (openLink is not null)
        {
            paragraph.EndLink();
            openLink = null;
        }
    }

    private Paragraph NewParagraph(WordParagraph paragraph, bool first)
    {
        var format = paragraph.Format;
        return new Paragraph(_styles.Style(paragraph.MarkFormat, format))
        {
            Alignment = format.Alignment switch
            {
                WordAlignment.Center => TextAlignment.Center,
                WordAlignment.End => TextAlignment.End,
                WordAlignment.Justify => TextAlignment.Justify,
                _ => TextAlignment.Start,
            },
            Direction = format.RightToLeft ? ParagraphDirection.RightToLeft : ParagraphDirection.LeftToRight,
            FirstLineIndent = first ? format.FirstLineIndent : 0,
            BreakLongWords = true,
        };
    }

    /// <summary>
    /// The next tab stop after <paramref name="x"/> (measured from the start of the text area): a custom stop, the
    /// hanging indent's implicit stop, or the next default stop.
    /// </summary>
    private (double Position, TabAlignment Alignment, char? Leader) NextStop(double x, ParagraphFormat format)
    {
        foreach (var stop in format.TabStops)
        {
            if (stop.Position > x + 0.01)
            {
                return (stop.Position, stop.Alignment, stop.Leader);
            }
        }

        if (format.FirstLineIndent < 0 && x < format.IndentStart - 0.01)
        {
            return (format.IndentStart, TabAlignment.Left, null);
        }

        var interval = _document.DefaultTabStop;
        return ((Math.Floor((x + 0.01) / interval) + 1) * interval, TabAlignment.Left, null);
    }

    /// <summary>Adds a tab's space (and leader) and returns its width.</summary>
    private double AddTab(Paragraph paragraph, TabInline tab, double x, List<Atom> following, ParagraphFormat format, double columnWidth)
    {
        var (position, alignment, leader) = tab.PositionalAlignment switch
        {
            WordAlignment.Center => (columnWidth / 2, TabAlignment.Center, null),
            WordAlignment.End => (columnWidth, TabAlignment.Right, null),
            WordAlignment.Start => (0, TabAlignment.Left, null),
            _ => NextStop(x, format),
        };

        var after = alignment switch
        {
            TabAlignment.Right => Measure(following, format),
            TabAlignment.Center => Measure(following, format) / 2,
            TabAlignment.Decimal => Measure(BeforeDecimal(following), format),
            _ => 0,
        };
        var width = Math.Max(MinimumTab, position - x - after);
        var style = _styles.Style(tab.Format, format);
        if (leader is { } fill)
        {
            if (alignment == TabAlignment.Right && position >= columnWidth - 2)
            {
                // A leader to the right margin (a table of contents) fills the line exactly.
                paragraph.AddLeader(fill.ToString(), style);
                return width;
            }

            var unit = new Paragraph(style).Add(fill.ToString()).MeasureIntrinsicWidths().Max;
            var count = unit > 0 ? (int)Math.Min(Math.Floor((width - MinimumTab) / unit), 2000) : 0;
            if (count > 0)
            {
                paragraph.Add(new string(fill, count), style);
                AddSpacer(paragraph, width - (count * unit));
                return width;
            }
        }

        AddSpacer(paragraph, width);
        return width;
    }

    /// <summary>The atoms of the text before its decimal separator (for a decimal tab).</summary>
    private static List<Atom> BeforeDecimal(List<Atom> atoms)
    {
        var result = new List<Atom>();
        foreach (var atom in atoms)
        {
            if (atom.Inline is TextRun run && run.Text.IndexOfAny(['.', ',']) is var index and >= 0)
            {
                if (index > 0)
                {
                    result.Add(new Atom(new TextRun(run.Text[..index], run.Format), atom.Link));
                }

                return result;
            }

            result.Add(atom);
        }

        return result;
    }

    /// <summary>Empty space of a width in the line.</summary>
    private static void AddSpacer(Paragraph paragraph, double width) =>
        paragraph.AddBox(new ContainerBlock(new BoxStyle { Width = Length.FromPoints(Math.Max(0, width)) }));

    /// <summary>The width of atoms on one line, in points.</summary>
    private double Measure(List<Atom> atoms, ParagraphFormat format)
    {
        if (atoms.Count == 0)
        {
            return 0;
        }

        var paragraph = new Paragraph(_styles.Style(RunFormat.Default, format));
        foreach (var atom in atoms)
        {
            AddAtom(paragraph, atom, format, measuring: true);
        }

        return paragraph.MeasureIntrinsicWidths().Max;
    }

    /// <summary>Adds an atom's content; returns whether it shows anything.</summary>
    private bool AddAtom(Paragraph paragraph, Atom atom, ParagraphFormat format, double textWidth = double.PositiveInfinity, bool measuring = false)
    {
        switch (atom.Inline)
        {
            case TextRun run:
                AddText(paragraph, atom.Text ?? run.Text, run.Format, format, measuring);
                return true;
            case NoteReference reference:
                AddText(paragraph, atom.Text ?? string.Empty, reference.Format, format, measuring);
                return true;
            case NoteMark mark:
                if (_currentNote is { } note)
                {
                    AddText(paragraph, Numbering.Format(note.Number, note.Format), mark.Format, format, measuring);
                    return true;
                }

                return false;
            case FieldInline field:
                AddText(paragraph, FieldText(field), field.Format, format, measuring);
                return true;
            case ImageInline image:
                if (Image(image) is not { } loaded)
                {
                    return false;
                }

                var (w, h) = Fit(image, loaded, double.IsFinite(textWidth) ? textWidth : double.MaxValue);
                var tagged = !measuring && _options.Tagged && image.AlternativeText is not null;
                if (tagged)
                {
                    paragraph.BeginTag(new StructureTag("Figure") { AlternateText = image.AlternativeText });
                }

                paragraph.AddImage(loaded, w, h);
                if (tagged)
                {
                    paragraph.EndTag();
                }

                return true;
            default:
                return false;
        }
    }

    /// <summary>Adds text in a run format: capitals, small capitals and a background (highlight) applied.</summary>
    private void AddText(Paragraph paragraph, string text, RunFormat format, ParagraphFormat paragraphFormat, bool measuring)
    {
        if (text.Length == 0)
        {
            return;
        }

        if (format.Caps)
        {
            text = text.ToUpperInvariant();
        }

        var style = _styles.Style(format, paragraphFormat);
        var background = !measuring && format.Background is { } color;
        if (background)
        {
            paragraph.BeginInline(new BoxStyle { Background = format.Background }, style);
        }

        if (format.SmallCaps && !format.Caps)
        {
            // Lowercase letters as smaller capitals, the rest as it is.
            var small = _styles.Style(format, paragraphFormat.LineRule, paragraphFormat.LineSpacing, TextStyles.SmallCapsScale);
            var start = 0;
            for (var i = 1; i <= text.Length; i++)
            {
                if (i == text.Length || char.IsLower(text[i]) != char.IsLower(text[start]))
                {
                    var piece = text[start..i];
                    if (char.IsLower(piece[0]))
                    {
                        paragraph.Add(piece.ToUpperInvariant(), small);
                    }
                    else
                    {
                        paragraph.Add(piece, style);
                    }

                    start = i;
                }
            }
        }
        else
        {
            paragraph.Add(text, style);
        }

        if (background)
        {
            paragraph.EndInline();
        }
    }

    /// <summary>
    /// A field's text: page numbers in headers and footers, page references by the PDF's pages (after the first layout
    /// pass), and otherwise the document's last result.
    /// </summary>
    private string FieldText(FieldInline field)
    {
        switch (field.Kind)
        {
            case FieldKind.Page when _page is { } page:
                return Numbering.Format(page.Number, field.NumberFormat ?? page.Format);
            case FieldKind.NumPages when _page is { } page:
                return Numbering.Format(page.Count, field.NumberFormat ?? NumberFormat.Decimal);
            case FieldKind.SectionPages:
                _usesComputedPages = true;
                return _page is { SectionPages: > 0 } sectionPage
                    ? Numbering.Format(sectionPage.SectionPages, field.NumberFormat ?? NumberFormat.Decimal)
                    : field.CachedResult ?? "1";
            case FieldKind.PageRef:
                _usesComputedPages = true;
                return field.Argument is { } name && _pageReferences is { } references && references.TryGetValue(name, out var number)
                    ? Numbering.Format(number, field.NumberFormat ?? NumberFormat.Decimal)
                    : field.CachedResult ?? string.Empty;
            default:
                return field.CachedResult ?? (field.Kind is FieldKind.Page or FieldKind.NumPages ? "1" : string.Empty);
        }
    }
}
