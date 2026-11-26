using System.Xml.Linq;
using RaiCore.Pdf.Documents.Model;

namespace RaiCore.Pdf.Documents.Odt;

/// <summary>
/// Reads an OpenDocument text document into the format-independent <see cref="WordDocument"/>: the body's paragraphs,
/// headings, lists, tables, sections and indexes with their styles resolved; a section per change of master page, with
/// the master page's page layout, headers and footers; notes, frames with images and text boxes, links, bookmarks and
/// fields. Tracked changes are read as if accepted: inserted text stays and deleted text (kept apart, in
/// <c>text:tracked-changes</c>) is left out.
/// </summary>
internal sealed partial class OdtReader
{
    /// <summary>How many blocks are read between checkpoints.</summary>
    private const int CheckpointInterval = 256;

    /// <summary>The margin used where a page layout does not give one: 2 cm, as word processors use.</summary>
    private const double DefaultMargin = 72 / 2.54 * 2;

    private readonly OdfPackage _package;
    private readonly ConversionContext _context;
    private readonly OdtStyles _styles;
    private readonly WordDocument _document = new();
    private readonly Dictionary<XElement, List<WordBlock>> _headerFooters = [];
    private StyleScope _scope = StyleScope.Content;
    private bool _tabsRelativeToIndent = true;
    private bool _breakBeforeNext;
    private double _width;
    private int _blocks;

    private OdtReader(OdfPackage package, ConversionContext context)
    {
        _package = package;
        _context = context;
        _styles = new OdtStyles(package.Styles, package.Content);
    }

    /// <summary>A change of master page (or a page break that restarts page numbers) while the body is read.</summary>
    private sealed class SectionMark(string? master, int? pageNumber) : WordBlock
    {
        /// <summary>The master page of the section that starts, or <c>null</c> for the current one.</summary>
        public string? Master { get; } = master;

        /// <summary>The page number the section starts at, or <c>null</c> to continue.</summary>
        public int? PageNumber { get; } = pageNumber;

        /// <summary>
        /// Whether a text section with columns starts or ends here: a section of the document on the same page, with
        /// <see cref="Columns"/>.
        /// </summary>
        public bool TextSection { get; init; }

        /// <summary>The columns after a <see cref="TextSection"/> mark; <c>null</c> for the page style's.</summary>
        public WordColumns? Columns { get; init; }
    }

    /// <summary>The columns of the text sections being read in the body, innermost last.</summary>
    private readonly Stack<WordColumns> _sectionColumns = [];

    /// <summary>Reads a document.</summary>
    public static WordDocument Read(OdfPackage package, ConversionContext context) => new OdtReader(package, context).ReadDocument();

    private WordDocument ReadDocument()
    {
        ReadMeta();
        ReadSettings();
        _document.DefaultTabStop = _styles.DefaultTabStop;
        _document.Language ??= _styles.Language;
        _document.FootnoteFormat = Odf.NumberFormatOf(Odf.Attr(_styles.NotesConfiguration("footnote"), Odf.Style, "num-format") ?? "1");
        _document.EndnoteFormat = Odf.NumberFormatOf(Odf.Attr(_styles.NotesConfiguration("endnote"), Odf.Style, "num-format") ?? "i");
        if (_package.HasScripts)
        {
            _context.Report(DocumentDiagnosticKind.UnsupportedContent, "The document's scripts (macros) are ignored.");
        }

        var defaultMaster = _styles.MasterPage("Standard") ?? _styles.MasterPages.FirstOrDefault();
        _width = ContentWidth(defaultMaster);
        var text = Odf.Child(Odf.Child(_package.Content, Odf.Office, "body"), Odf.Office, "text")!;
        var blocks = new List<WordBlock>();
        ReadBlocks(text, blocks, body: true);

        var master = Odf.Attr(defaultMaster, Odf.Style, "name");
        int? pageNumber = null;
        WordColumns? columns = null; // The text section's columns, or null for the page style's.
        var continuous = false;
        var current = new List<WordBlock>();
        foreach (var block in blocks)
        {
            if (block is not SectionMark mark)
            {
                current.Add(block);
                continue;
            }

            if (current.Count > 0 || _document.Sections.Count > 0)
            {
                AddSection(master, pageNumber, current, columns, continuous);
                current = [];
                pageNumber = null;
            }

            master = mark.Master ?? master;
            pageNumber = mark.PageNumber ?? pageNumber;
            (columns, continuous) = mark.TextSection ? (mark.Columns, true) : (null, false);
        }

        if (current.Count > 0 || !continuous || _document.Sections.Count == 0)
        {
            AddSection(master, pageNumber, current, columns, continuous);
        }
        ShareEvenHeaders();
        return _document;
    }

    private void ReadMeta()
    {
        var meta = Odf.Child(_package.Meta, Odf.Office, "meta");
        static string? Value(XElement? element) => element?.Value.Trim() is { Length: > 0 } value ? value : null;
        _document.Title = Value(Odf.Child(meta, Odf.Dc, "title"));
        _document.Author = Value(Odf.Child(meta, Odf.Dc, "creator")) ?? Value(Odf.Child(meta, Odf.Meta, "initial-creator"));
        _document.Language = Value(Odf.Child(meta, Odf.Dc, "language")) is { Length: <= 35 } language ? language : null;
    }

    /// <summary>Reads the settings that change the layout: whether tab stops are relative to the paragraph indent.</summary>
    private void ReadSettings()
    {
        var settings = Odf.Child(_package.Settings, Odf.Office, "settings");
        var item = settings?.Descendants(Odf.Config + "config-item").FirstOrDefault(i => Odf.Attr(i, Odf.Config, "name") == "TabsRelativeToIndent");
        _tabsRelativeToIndent = item?.Value.Trim() != "false";
    }

    /// <summary>The width of the text area of a master page's page layout.</summary>
    private double ContentWidth(XElement? master)
    {
        if (master is null)
        {
            // No master page: the section keeps the default page setup.
            var defaults = new WordSection();
            return defaults.PageWidth - defaults.MarginLeft - defaults.MarginRight;
        }

        var properties = Odf.Child(_styles.PageLayout(Odf.Attr(master, Odf.Style, "page-layout-name")), Odf.Style, "page-layout-properties");
        var (width, _) = PageSize(properties);
        var (_, right, _, left) = PageMargins(properties);
        return Math.Max(36, width - left - right);
    }

    private static (double Width, double Height) PageSize(XElement? properties)
    {
        var width = Math.Clamp(Odf.Length(properties, Odf.Fo, "page-width") ?? 612, 72, 14400);
        var height = Math.Clamp(Odf.Length(properties, Odf.Fo, "page-height") ?? 792, 72, 14400);
        if (Odf.Attr(properties, Odf.Style, "print-orientation") == "landscape" && width < height)
        {
            (width, height) = (height, width);
        }

        return (width, height);
    }

    private static (double Top, double Right, double Bottom, double Left) PageMargins(XElement? properties)
    {
        var all = Odf.Length(properties, Odf.Fo, "margin") ?? DefaultMargin;
        double Side(string name) => Math.Max(0, Odf.Length(properties, Odf.Fo, name) ?? all);
        return (Side("margin-top"), Side("margin-right"), Side("margin-bottom"), Side("margin-left"));
    }

    /// <summary>Adds a section with a master page's page setup, headers and footers.</summary>
    private void AddSection(string? masterName, int? pageNumber, List<WordBlock> blocks, WordColumns? columns = null, bool continuous = false)
    {
        var section = new WordSection { PageNumberStart = pageNumber, Start = continuous ? SectionStart.Continuous : SectionStart.NextPage };
        section.Blocks.AddRange(blocks);
        var master = _styles.MasterPage(masterName) ?? _styles.MasterPage("Standard") ?? _styles.MasterPages.FirstOrDefault();
        if (master is not null)
        {
            ApplyMaster(section, master);
        }

        if (columns is not null)
        {
            section.Columns = columns; // A text section's own columns, instead of the page style's.
        }

        _document.Sections.Add(section);
    }

    /// <summary>
    /// A section's page setup and headers and footers from a master page. A master page followed by another one
    /// (<c>style:next-style-name</c>, as a first page style) gives the section a first page with its own headers and
    /// footers, the other pages those of the next master page.
    /// </summary>
    private void ApplyMaster(WordSection section, XElement master)
    {
        var layout = _styles.PageLayout(Odf.Attr(master, Odf.Style, "page-layout-name"));
        var properties = Odf.Child(layout, Odf.Style, "page-layout-properties");
        (section.PageWidth, section.PageHeight) = PageSize(properties);
        var (top, right, bottom, left) = PageMargins(properties);
        section.PageNumberFormat = Odf.Attr(properties, Odf.Style, "num-format") is { } format ? Odf.NumberFormatOf(format) : NumberFormat.Decimal;
        section.Columns = Columns(Odf.Child(properties, Odf.Style, "columns"), section.PageWidth - left - right);

        var next = _styles.MasterPage(Odf.Attr(master, Odf.Style, "next-style-name"));
        var pages = next is not null && next != master ? next : master;
        HeaderFooter(section.Headers, pages, "header", "header-left");
        HeaderFooter(section.Footers, pages, "footer", "footer-left");
        if (pages != master)
        {
            section.TitlePage = true;
            if (HeaderFooterPart(master, "header") is { } firstHeader)
            {
                section.Headers[HeaderFooterKind.First] = firstHeader;
            }

            if (HeaderFooterPart(master, "footer") is { } firstFooter)
            {
                section.Footers[HeaderFooterKind.First] = firstFooter;
            }
        }

        if (HeaderFooterPart(master, "header-first") is { } headerFirst)
        {
            section.TitlePage = true;
            section.Headers[HeaderFooterKind.First] = headerFirst;
        }

        if (HeaderFooterPart(master, "footer-first") is { } footerFirst)
        {
            section.TitlePage = true;
            section.Footers[HeaderFooterKind.First] = footerFirst;
        }

        // The header is inside the page margin: the text starts below it and the space after it.
        section.HeaderDistance = top;
        section.FooterDistance = bottom;
        if (section.Headers.Count > 0)
        {
            top += HeaderFooterExtent(layout, "header-style", "margin-bottom");
        }

        if (section.Footers.Count > 0)
        {
            bottom += HeaderFooterExtent(layout, "footer-style", "margin-top");
        }

        (left, right) = Fit(left, right, section.PageWidth);
        (top, bottom) = Fit(top, bottom, section.PageHeight);
        (section.MarginTop, section.MarginRight, section.MarginBottom, section.MarginLeft) = (top, right, bottom, left);
        section.HeaderDistance = Math.Min(section.HeaderDistance, section.PageHeight / 3);
        section.FooterDistance = Math.Min(section.FooterDistance, section.PageHeight / 3);

        // Margins that leave less than an inch are scaled down to leave it.
        static (double, double) Fit(double a, double b, double extent)
        {
            var room = extent - 72;
            return a + b > room ? (a * room / (a + b), b * room / (a + b)) : (a, b);
        }
    }

    /// <summary>The height of a header or footer area (its least height) and its spacing from the text.</summary>
    private static double HeaderFooterExtent(XElement? layout, string style, string spacing)
    {
        var properties = Odf.Child(Odf.Child(layout, Odf.Style, style), Odf.Style, "header-footer-properties");
        var height = Odf.Length(properties, Odf.Svg, "height") ?? Odf.Length(properties, Odf.Fo, "min-height") ?? 0;
        return Math.Clamp(height, 0, 2000) + Math.Clamp(Odf.Length(properties, Odf.Fo, spacing) ?? 0, 0, 2000);
    }

    /// <summary>Sets a master page's header (or footer) and its left-page variant, if shown.</summary>
    private void HeaderFooter(Dictionary<HeaderFooterKind, List<WordBlock>> target, XElement master, string name, string left)
    {
        if (HeaderFooterPart(master, name) is { } blocks)
        {
            target[HeaderFooterKind.Default] = blocks;
        }

        if (HeaderFooterPart(master, left) is { } leftBlocks)
        {
            target[HeaderFooterKind.Even] = leftBlocks;
        }
    }

    /// <summary>The blocks of a master page's header or footer element (read once), or <c>null</c> if it has none or hides it.</summary>
    private List<WordBlock>? HeaderFooterPart(XElement master, string name)
    {
        var element = Odf.Child(master, Odf.Style, name) ?? Odf.Child(master, Odf.LibreOffice, name);
        if (element is null || Odf.Attr(element, Odf.Style, "display") == "false")
        {
            return null;
        }

        if (_headerFooters.TryGetValue(element, out var blocks))
        {
            return blocks;
        }

        blocks = [];
        _headerFooters[element] = blocks;
        var (scope, width, breakBefore) = (_scope, _width, _breakBeforeNext);
        try
        {
            _scope = StyleScope.Master;
            _width = ContentWidth(master);
            ReadBlocks(element, blocks, body: false);
        }
        finally
        {
            (_scope, _width, _breakBeforeNext) = (scope, width, breakBefore);
        }

        return blocks;
    }

    /// <summary>
    /// With different headers on left pages in any section, the sections without their own left-page header or footer
    /// show the other pages' on left pages too.
    /// </summary>
    private void ShareEvenHeaders()
    {
        _document.EvenAndOddHeaders = _document.Sections.Any(s => s.Headers.ContainsKey(HeaderFooterKind.Even) || s.Footers.ContainsKey(HeaderFooterKind.Even));
        if (!_document.EvenAndOddHeaders)
        {
            return;
        }

        foreach (var section in _document.Sections)
        {
            if (!section.Headers.ContainsKey(HeaderFooterKind.Even) && section.Headers.TryGetValue(HeaderFooterKind.Default, out var header))
            {
                section.Headers[HeaderFooterKind.Even] = header;
            }

            if (!section.Footers.ContainsKey(HeaderFooterKind.Even) && section.Footers.TryGetValue(HeaderFooterKind.Default, out var footer))
            {
                section.Footers[HeaderFooterKind.Even] = footer;
            }
        }
    }

    /// <summary>
    /// Reads the blocks of a container (the body, a section, an index body, a cell, a header, a note, a text box) in
    /// order. In the body (<paramref name="body"/>), master page changes become section marks.
    /// </summary>
    private void ReadBlocks(XElement container, List<WordBlock> output, bool body)
    {
        foreach (var element in container.Elements())
        {
            if (++_blocks % CheckpointInterval == 0)
            {
                _context.Checkpoint();
            }

            var ns = element.Name.Namespace;
            var name = element.Name.LocalName;
            if (ns == Odf.Text)
            {
                switch (name)
                {
                    case "p":
                    case "h":
                        ReadParagraph(element, output, body, null);
                        break;
                    case "list":
                        ReadList(element, output, body, null);
                        break;
                    case "numbered-paragraph":
                        ReadNumberedParagraph(element, output, body);
                        break;
                    case "section":
                        ReadSection(element, output, body);
                        break;
                    case "table-of-content":
                    case "illustration-index":
                    case "table-index":
                    case "object-index":
                    case "user-index":
                    case "alphabetical-index":
                    case "bibliography":
                        if (Odf.Child(element, Odf.Text, "index-body") is { } index)
                        {
                            ReadBlocks(index, output, body);
                        }

                        break;
                    case "index-title":
                        ReadBlocks(element, output, body);
                        break;
                }
            }
            else if (ns == Odf.Table && name == "table")
            {
                ReadTableBlock(element, output, body);
            }
            else if (ns == Odf.Draw)
            {
                ReadBlockDrawing(element, output);
            }
            else if (ns == Odf.Office && name == "forms" && element.HasElements)
            {
                _context.Report(DocumentDiagnosticKind.UnsupportedContent, "Forms are left out.");
            }
        }
    }

    /// <summary>
    /// Reads a section: its blocks, unless it is hidden. In the body, a section with several text columns becomes a
    /// section of the document of its own, on the same page as what comes before and after it (where the columns of
    /// the section around it, or of the page style, go on).
    /// </summary>
    private void ReadSection(XElement section, List<WordBlock> output, bool body)
    {
        if (Odf.Attr(section, Odf.Text, "display") == "none")
        {
            return;
        }

        var properties = _styles.Properties("section", Odf.Attr(section, Odf.Text, "style-name"), _scope, "section-properties");
        var element = properties.Select(p => Odf.Child(p, Odf.Style, "columns")).LastOrDefault(c => c is not null);
        var columns = Columns(element, _width);
        if (columns.Count <= 1)
        {
            ReadBlocks(section, output, body);
            return;
        }

        if (!body)
        {
            _context.Report(DocumentDiagnosticKind.UnsupportedContent, $"A section with {columns.Count} text columns outside the body text is laid out in one column.");
            ReadBlocks(section, output, body);
            return;
        }

        output.Add(new SectionMark(null, null) { TextSection = true, Columns = columns });
        _sectionColumns.Push(columns);
        try
        {
            ReadBlocks(section, output, body);
        }
        finally
        {
            _sectionColumns.Pop();
        }

        output.Add(new SectionMark(null, null) { TextSection = true, Columns = _sectionColumns.Count > 0 ? _sectionColumns.Peek() : null });
    }

    /// <summary>
    /// Text columns (<c>style:columns</c>) in a text area <paramref name="width"/> wide: <c>fo:column-count</c> columns
    /// <c>fo:column-gap</c> apart, or each <c>style:column</c>'s share of the width (<c>style:rel-width</c>) less its
    /// indents, which make the gaps; a line between them with a visible <c>style:column-sep</c>.
    /// </summary>
    private static WordColumns Columns(XElement? columns, double width)
    {
        var count = Odf.Int(columns, Odf.Fo, "column-count") ?? 1;
        if (columns is null || count <= 1)
        {
            return WordColumns.One;
        }

        var sep = Odf.Child(columns, Odf.Style, "column-sep");
        var separator = sep is not null && Odf.Attr(sep, Odf.Style, "style") != "none" && (Odf.Length(sep, Odf.Style, "width") ?? 0.5) > 0;
        var each = Odf.Children(columns, Odf.Style, "column").Take(WordColumns.MaxCount).ToList();
        if (each.Count == Math.Min(count, WordColumns.MaxCount) && each.Count > 1)
        {
            var shares = each.Select(c => Math.Max(0, double.TryParse(Odf.Attr(c, Odf.Style, "rel-width")?.TrimEnd('*'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var share) ? share : 0)).ToList();
            var total = shares.Sum();
            if (total > 0)
            {
                var starts = each.Select(c => Math.Max(0, Odf.Length(c, Odf.Fo, "start-indent") ?? 0)).ToList();
                var ends = each.Select(c => Math.Max(0, Odf.Length(c, Odf.Fo, "end-indent") ?? 0)).ToList();
                var widths = Enumerable.Range(0, each.Count)
                    .Select(i => (Math.Max(1, (width * shares[i] / total) - starts[i] - ends[i]), i + 1 < each.Count ? ends[i] + starts[i + 1] : 0))
                    .ToList();
                return new WordColumns(widths.Count, widths[0].Item2, separator, widths);
            }
        }

        return WordColumns.Equal(count, Odf.Length(columns, Odf.Fo, "column-gap") ?? 0, separator);
    }

    /// <summary>
    /// Adds a block, after a section mark when it starts a new master page (or restarts the page numbers at a page
    /// break) in the body.
    /// </summary>
    private void AddWithBreak(WordBlock block, List<WordBlock> output, bool body, string? master, int? pageNumber, bool breakBefore, bool breakAfter)
    {
        if (body)
        {
            // A page break after the block before (in the body: not taken by the paragraphs of a table's cells).
            breakBefore |= _breakBeforeNext;
            _breakBeforeNext = breakAfter;
        }

        if (body && (master is not null || (breakBefore && (pageNumber is not null || block is WordTable))))
        {
            output.Add(new SectionMark(master, pageNumber));
            if (master is not null)
            {
                _width = ContentWidth(_styles.MasterPage(master));
            }

            breakBefore = false;
        }

        if (block is WordParagraph paragraph && breakBefore != paragraph.Format.PageBreakBefore)
        {
            paragraph.Format = paragraph.Format with { PageBreakBefore = breakBefore };
        }

        output.Add(block);
    }
}
