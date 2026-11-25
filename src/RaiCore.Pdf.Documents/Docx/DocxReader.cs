using System.Xml.Linq;
using RaiCore.Pdf.Documents.Model;

namespace RaiCore.Pdf.Documents.Docx;

/// <summary>
/// Reads a Word document (WordprocessingML) into the format-independent <see cref="WordDocument"/>: the main document
/// part with its sections, the styles, numbering, settings and theme it relates to, its headers, footers, notes and
/// images. Content controls, inserted and moved text are unwrapped and deleted text is dropped (tracked changes as if
/// accepted); fields keep their last result, except page numbers and page references, which the renderer computes.
/// </summary>
internal sealed partial class DocxReader
{
    /// <summary>How many blocks are read between checkpoints.</summary>
    private const int CheckpointInterval = 256;

    private readonly DocxPackage _package;
    private readonly ConversionContext _context;
    private readonly WordDocument _document = new();
    private readonly Dictionary<string, List<WordBlock>> _parts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, byte[]?> _media = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<BookmarkInline> _pendingBookmarks = [];
    private DocxTheme _theme = DocxTheme.Read(null);
    private DocxStyles _styles = DocxStyles.Read(null, DocxTheme.Read(null));
    private DocxNumbering _numbering = DocxNumbering.Read(null);
    private Dictionary<string, Relationship> _relationships = [];
    private string _part = string.Empty;
    private int _blocks;
    private int _tableDepth;

    private DocxReader(DocxPackage package, ConversionContext context)
    {
        _package = package;
        _context = context;
    }

    /// <summary>A section break while the body is read: the section properties that end the blocks before it.</summary>
    private sealed class SectionMark(XElement properties) : WordBlock
    {
        public XElement Properties { get; } = properties;
    }

    /// <summary>Reads a document.</summary>
    /// <exception cref="DocumentFormatException">The package has no main document part, or it is not a Word document.</exception>
    public static WordDocument Read(DocxPackage package, ConversionContext context) => new DocxReader(package, context).ReadDocument();

    private WordDocument ReadDocument()
    {
        var packageRelationships = _package.Relationships(string.Empty);
        var main = packageRelationships.Values.FirstOrDefault(r => r.Type == "officeDocument" && !r.External).Target ?? "word/document.xml";
        if (!_package.Contains(main))
        {
            throw new DocumentFormatException("The package has no main document part: it is not a Word document.");
        }

        if (packageRelationships.Values.FirstOrDefault(r => r.Type == "core-properties" && !r.External).Target is { } core
            && _package.Load(core) is { } properties)
        {
            _document.Title = properties.Elements().FirstOrDefault(e => e.Name.LocalName == "title")?.Value is { Length: > 0 } title ? title.Trim() : null;
        }

        _part = main;
        _relationships = _package.Relationships(main);
        _theme = DocxTheme.Read(LoadRelated("theme"));
        _styles = DocxStyles.Read(LoadRelated("styles"), _theme);
        _numbering = DocxNumbering.Read(LoadRelated("numbering"));
        ReadSettings(LoadRelated("settings"));
        _document.Language = _styles.DefaultRun.Language;

        var root = _package.Load(main);
        if (root is null || root.Name.LocalName != "document" || !Ooxml.IsW(root))
        {
            throw new DocumentFormatException($"The main document part '{main}' is not a WordprocessingML document.");
        }

        ReadNotes(RelatedPath("footnotes"), "footnote", _document.Footnotes);
        ReadNotes(RelatedPath("endnotes"), "endnote", _document.Endnotes);
        if (_context.Has(ReadExtras.Comments))
        {
            ReadNotes(RelatedPath("comments"), "comment", _document.Comments);
        }

        var body = Ooxml.Child(root, "body");
        var blocks = new List<WordBlock>();
        if (body is not null)
        {
            ReadBlocks(body, blocks, TableContext.None);
        }

        var current = new List<WordBlock>();
        WordSection? previous = null;
        foreach (var block in blocks)
        {
            if (block is SectionMark mark)
            {
                previous = AddSection(mark.Properties, current, previous);
                current = [];
            }
            else
            {
                current.Add(block);
            }
        }

        AddSection(Ooxml.Child(body, "sectPr"), current, previous);
        return _document;
    }

    /// <summary>The path of the part the current part relates to by type (<c>styles</c>), or <c>null</c>.</summary>
    private string? RelatedPath(string type) =>
        _relationships.Values.Where(r => r.Type == type && !r.External).Select(r => r.Target).FirstOrDefault();

    private XElement? LoadRelated(string type) => RelatedPath(type) is { } path ? _package.Load(path) : null;

    private void ReadSettings(XElement? settings)
    {
        if (settings is null)
        {
            return;
        }

        _document.EvenAndOddHeaders = Ooxml.OnOff(settings, "evenAndOddHeaders") ?? false;
        if (Ooxml.Twips(Ooxml.Child(settings, "defaultTabStop"), "val") is { } tab && tab >= 1)
        {
            _document.DefaultTabStop = Math.Min(tab, 1584);
        }

        if (Ooxml.Val(Ooxml.Child(settings, "footnotePr"), "numFmt") is { } footnoteFormat)
        {
            _document.FootnoteFormat = Ooxml.NumberFormatOf(footnoteFormat);
        }

        if (Ooxml.Val(Ooxml.Child(settings, "endnotePr"), "numFmt") is { } endnoteFormat)
        {
            _document.EndnoteFormat = Ooxml.NumberFormatOf(endnoteFormat);
        }
    }

    /// <summary>Reads the notes of a footnotes or endnotes part, without the separators.</summary>
    private void ReadNotes(string? path, string element, Dictionary<string, List<WordBlock>> notes)
    {
        if (path is null)
        {
            return;
        }

        WithPart(path, root =>
        {
            foreach (var note in Ooxml.Children(root, element))
            {
                if (Ooxml.Attr(note, "id") is not { } id || Ooxml.Attr(note, "type") is "separator" or "continuationSeparator" or "continuationNotice")
                {
                    continue;
                }

                var blocks = new List<WordBlock>();
                ReadBlocks(note, blocks, TableContext.None);
                notes.TryAdd(id, blocks);
            }
        });
    }

    /// <summary>Reads another part (a header, footer or notes) with its own relationships, then returns to the current one.</summary>
    private void WithPart(string path, Action<XElement> read)
    {
        var (part, relationships) = (_part, _relationships);
        try
        {
            _part = path;
            _relationships = _package.Relationships(path);
            if (_package.Load(path) is { } root)
            {
                read(root);
            }
        }
        finally
        {
            (_part, _relationships) = (part, relationships);
        }
    }

    /// <summary>The blocks of a header or footer part, read once.</summary>
    private List<WordBlock> HeaderFooter(string path)
    {
        if (_parts.TryGetValue(path, out var blocks))
        {
            return blocks;
        }

        blocks = [];
        _parts[path] = blocks; // Before reading: a part that refers to itself is read once.
        WithPart(path, root => ReadBlocks(root, blocks, TableContext.None));
        return blocks;
    }

    /// <summary>Adds the section that <paramref name="properties"/> (a <c>w:sectPr</c>, or <c>null</c>) ends.</summary>
    private WordSection AddSection(XElement? properties, List<WordBlock> blocks, WordSection? previous)
    {
        var section = new WordSection();
        section.Blocks.AddRange(blocks);
        if (properties is not null)
        {
            ReadPageSetup(section, properties);
            section.Start = Ooxml.Val(properties, "type") switch
            {
                "continuous" or "nextColumn" => SectionStart.Continuous,
                "evenPage" => SectionStart.EvenPage,
                "oddPage" => SectionStart.OddPage,
                _ => SectionStart.NextPage,
            };
            section.TitlePage = Ooxml.OnOff(properties, "titlePg") ?? false;
            var pageNumbers = Ooxml.Child(properties, "pgNumType");
            section.PageNumberStart = Ooxml.Int(pageNumbers, "start") is { } start ? Math.Clamp(start, 0, 1_000_000) : null;
            section.PageNumberFormat = Ooxml.NumberFormatOf(Ooxml.Attr(pageNumbers, "fmt"));
            section.Columns = Math.Clamp(Ooxml.Int(Ooxml.Child(properties, "cols"), "num") ?? 1, 1, 45);
            if (section.Columns > 1)
            {
                _context.Report(DocumentDiagnosticKind.UnsupportedContent, $"A section with {section.Columns} text columns is laid out in one column.");
            }

            ReadHeaderFooterReferences(properties, "headerReference", section.Headers);
            ReadHeaderFooterReferences(properties, "footerReference", section.Footers);
        }

        if (previous is not null)
        {
            foreach (var kind in Enum.GetValues<HeaderFooterKind>())
            {
                if (!section.Headers.ContainsKey(kind) && previous.Headers.TryGetValue(kind, out var header))
                {
                    section.Headers[kind] = header;
                }

                if (!section.Footers.ContainsKey(kind) && previous.Footers.TryGetValue(kind, out var footer))
                {
                    section.Footers[kind] = footer;
                }
            }
        }

        _document.Sections.Add(section);
        return section;
    }

    /// <summary>Reads the page size, orientation and margins, keeping at least an inch of room for the text both ways.</summary>
    private static void ReadPageSetup(WordSection section, XElement properties)
    {
        var size = Ooxml.Child(properties, "pgSz");
        var width = Math.Clamp(Ooxml.Twips(size, "w") ?? section.PageWidth, 72, 14400);
        var height = Math.Clamp(Ooxml.Twips(size, "h") ?? section.PageHeight, 72, 14400);
        if (Ooxml.Attr(size, "orient") == "landscape" && width < height)
        {
            (width, height) = (height, width);
        }

        section.PageWidth = width;
        section.PageHeight = height;

        var margins = Ooxml.Child(properties, "pgMar");
        var gutter = Math.Max(0, Ooxml.Twips(margins, "gutter") ?? 0);
        var top = Math.Abs(Ooxml.Twips(margins, "top") ?? section.MarginTop);
        var bottom = Math.Abs(Ooxml.Twips(margins, "bottom") ?? section.MarginBottom);
        var left = Math.Max(0, Ooxml.Twips(margins, "left") ?? section.MarginLeft) + gutter;
        var right = Math.Max(0, Ooxml.Twips(margins, "right") ?? section.MarginRight);
        (left, right) = Fit(left, right, width);
        (top, bottom) = Fit(top, bottom, height);
        section.MarginTop = top;
        section.MarginBottom = bottom;
        section.MarginLeft = left;
        section.MarginRight = right;
        section.HeaderDistance = Math.Clamp(Ooxml.Twips(margins, "header") ?? section.HeaderDistance, 0, height / 3);
        section.FooterDistance = Math.Clamp(Ooxml.Twips(margins, "footer") ?? section.FooterDistance, 0, height / 3);

        // Margins that leave less than an inch are scaled down to leave it.
        static (double, double) Fit(double a, double b, double extent)
        {
            var room = extent - 72;
            return a + b > room ? (a * room / (a + b), b * room / (a + b)) : (a, b);
        }
    }

    private void ReadHeaderFooterReferences(XElement properties, string element, Dictionary<HeaderFooterKind, List<WordBlock>> target)
    {
        foreach (var reference in Ooxml.Children(properties, element))
        {
            var kind = Ooxml.Attr(reference, "type") switch
            {
                "first" => HeaderFooterKind.First,
                "even" => HeaderFooterKind.Even,
                _ => HeaderFooterKind.Default,
            };
            if (RelationshipId(reference) is { } id && _relationships.TryGetValue(id, out var relationship) && !relationship.External
                && _package.Contains(relationship.Target))
            {
                target[kind] = HeaderFooter(relationship.Target);
            }
        }
    }

    /// <summary>The relationship id an element refers to (<c>r:id</c>, <c>r:embed</c>), by namespace, or <c>null</c>.</summary>
    private static string? RelationshipId(XElement element, string name = "id")
    {
        foreach (var attribute in element.Attributes())
        {
            if (attribute.Name.LocalName == name && attribute.Name.NamespaceName.Contains("relationships", StringComparison.Ordinal))
            {
                return attribute.Value;
            }
        }

        return null;
    }

    /// <summary>
    /// The table style formatting that applies to a cell's paragraphs: paragraph and run properties applied after the
    /// document defaults and before the paragraph's style.
    /// </summary>
    private sealed record TableContext(IReadOnlyList<XElement> ParagraphProperties, IReadOnlyList<XElement> RunProperties)
    {
        public static TableContext None { get; } = new([], []);
    }

    /// <summary>Reads the blocks of a container (body, cell, header, note, text box) in order.</summary>
    private void ReadBlocks(XElement container, List<WordBlock> output, TableContext table)
    {
        foreach (var element in container.Elements())
        {
            if (++_blocks % CheckpointInterval == 0)
            {
                _context.Checkpoint();
            }

            if (element.Name.NamespaceName == Ooxml.MarkupCompatibility && element.Name.LocalName == "AlternateContent")
            {
                if (Choose(element) is { } chosen)
                {
                    ReadBlocks(chosen, output, table);
                }

                continue;
            }

            if (!Ooxml.IsW(element))
            {
                continue;
            }

            switch (element.Name.LocalName)
            {
                case "p":
                    var paragraph = ReadParagraph(element, table);
                    output.Add(paragraph);
                    if (Ooxml.Child(Ooxml.Child(element, "pPr"), "sectPr") is { } properties && IsInBody(container))
                    {
                        output.Add(new SectionMark(properties));
                    }

                    break;
                case "tbl":
                    output.Add(ReadTable(element));
                    break;
                case "sdt":
                    if (Ooxml.Child(element, "sdtContent") is { } content)
                    {
                        ReadBlocks(content, output, table);
                    }

                    break;
                case "customXml":
                case "ins":
                case "moveTo":
                case "smartTag":
                    ReadBlocks(element, output, table);
                    break;
                case "bookmarkStart":
                    if (Ooxml.Attr(element, "name") is { Length: > 0 } name && !name.StartsWith("_GoBack", StringComparison.Ordinal))
                    {
                        _pendingBookmarks.Add(new BookmarkInline(name));
                    }

                    break;
                case "altChunk":
                    _context.Report(DocumentDiagnosticKind.UnsupportedContent, "An embedded document part (altChunk) is left out.");
                    break;
            }
        }
    }

    /// <summary>Whether a container is the body or a block-level wrapper inside it (where section breaks count).</summary>
    private static bool IsInBody(XElement container)
    {
        for (var element = container; element is not null; element = element.Parent)
        {
            if (Ooxml.Is(element, "body"))
            {
                return true;
            }

            if (Ooxml.Is(element, "tc") || Ooxml.Is(element, "txbxContent") || Ooxml.Is(element, "hdr") || Ooxml.Is(element, "ftr"))
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// The branch of <c>mc:AlternateContent</c> to read: the first <c>mc:Choice</c> (the markup this reader knows,
    /// such as WordprocessingML shapes and newer drawing extensions, all read the same), else the <c>mc:Fallback</c>.
    /// A choice with a newer chart type (chartex) the chart engine cannot draw (a region map, a missing or damaged
    /// part) gives way to the fallback, its stored picture.
    /// </summary>
    private XElement? Choose(XElement alternate)
    {
        var choice = alternate.Elements().FirstOrDefault(e => e.Name.LocalName == "Choice");
        var fallback = alternate.Elements().FirstOrDefault(e => e.Name.LocalName == "Fallback");
        if (choice is not null && fallback is not null && RequiresChartEx(choice) && !CanDrawChartEx(choice))
        {
            _context.Report(DocumentDiagnosticKind.UnsupportedContent, "A chart of a newer type (such as a waterfall or treemap chart) is shown as its stored picture.");
            return fallback;
        }

        return choice ?? fallback;
    }
}
