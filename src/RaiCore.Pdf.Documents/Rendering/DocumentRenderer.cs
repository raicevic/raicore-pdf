using RaiCore.Pdf.Documents.Model;
using RaiCore.Pdf.Layout;

namespace RaiCore.Pdf.Documents.Rendering;

/// <summary>
/// Lays out a <see cref="WordDocument"/> with the layout engine: each section in a container whose pages get the
/// section's size and margins (by page name), headers and footers drawn on every page with their page numbers,
/// footnotes after their section and endnotes at the end. Page references (<c>PAGEREF</c>) and section page counts are
/// resolved by laying the document out twice.
/// </summary>
internal sealed partial class DocumentRenderer
{
    private readonly WordDocument _document;
    private readonly DocumentConverterOptions _options;
    private readonly ConversionContext _context;
    private readonly TextStyles _styles;
    private readonly Dictionary<byte[], PdfImage?> _images = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, int> _sectionOfPageName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PageSetup> _setups = new(StringComparer.Ordinal);
    private readonly List<string> _pageNames = [];

    // Per layout pass.
    private Dictionary<string, int>? _pageReferences;
    private Dictionary<string, int>? _sectionPageCounts;
    private readonly List<(NoteKind Kind, string Id, int Number)> _pendingFootnotes = [];
    private readonly List<(string Id, int Number)> _endnotes = [];
    private int _footnoteNumber;
    private int _endnoteNumber;
    private (int Number, NumberFormat Format)? _currentNote;
    private bool _usesComputedPages;

    // While a header or footer is built for a page.
    private PageFields? _page;
    private int _depth;

    /// <summary>The values of the page fields on the page whose header or footer is being drawn.</summary>
    private sealed record PageFields(int Number, NumberFormat Format, int Count, int SectionPages);

    public DocumentRenderer(WordDocument document, DocumentConverterOptions options, ConversionContext context)
    {
        _document = document;
        _options = options;
        _context = context;
        _styles = new TextStyles(options);
        if (_document.Sections.Count == 0)
        {
            _document.Sections.Add(new WordSection());
        }
    }

    /// <summary>Renders the document to a new PDF.</summary>
    public PdfDocument Render(string creator)
    {
        var flow = Build();
        if (_usesComputedPages)
        {
            // Page references and section page counts need the pages: lay out once, then again with the numbers.
            var map = flow.MapPages();
            var numbers = PageNumbers(map.Pages);
            _pageReferences = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var (name, page) in map.Destinations)
            {
                if (page >= 1 && page <= numbers.Count)
                {
                    _pageReferences[name] = numbers[page - 1];
                }
            }

            _sectionPageCounts = map.Pages.GroupBy(p => p.Name ?? _pageNames[0]).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
            flow = Build();
        }

        flow.Diagnostics = d => _context.Report(DocumentDiagnosticKind.Layout, d.Kind switch
        {
            LayoutDiagnosticKind.Overflow => $"Content is wider than its box on page {d.Page + 1}.",
            LayoutDiagnosticKind.Clipped => $"Content is taller than a page on page {d.Page + 1}.",
            _ => $"An image is scaled down to fit page {d.Page + 1}.",
        });
        var pdf = new PdfDocument { Tagged = _options.Tagged };
        flow.RenderTo(pdf);
        pdf.Info.Title = _options.Title ?? _document.Title;
        pdf.Info.Author = _document.Author;
        pdf.Info.Creator = creator;
        if (_options.Tagged && _document.Language is { } language)
        {
            pdf.Language = language;
        }

        return pdf;
    }

    /// <summary>The page number shown on each page (1-based physical pages), by the sections' numbering.</summary>
    private List<int> PageNumbers(IReadOnlyList<PageContext> pages)
    {
        var numbers = new List<int>(pages.Count);
        string? last = null;
        var number = 0;
        foreach (var page in pages)
        {
            var name = page.Name ?? _pageNames[0];
            number = name != last && _sectionOfPageName.TryGetValue(name, out var index) && _document.Sections[index].PageNumberStart is { } start
                ? start
                : number + 1;
            last = name;
            numbers.Add(number);
        }

        return numbers;
    }

    /// <summary>Builds the flow document: page setups per section group, the sections' content, notes and the page decorator.</summary>
    private FlowDocument Build()
    {
        _pendingFootnotes.Clear();
        _endnotes.Clear();
        _footnoteNumber = 0;
        _endnoteNumber = 0;
        _sectionOfPageName.Clear();
        _setups.Clear();
        _pageNames.Clear();

        // Sections that continue on the same page share a page name (and so their pages); others start new pages.
        var names = new string[_document.Sections.Count];
        for (var i = 0; i < names.Length; i++)
        {
            var section = _document.Sections[i];
            if (i > 0 && section.Start == SectionStart.Continuous && section.SamePageAs(_document.Sections[i - 1]))
            {
                names[i] = names[i - 1];
                continue;
            }

            names[i] = "s" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            _sectionOfPageName[names[i]] = i;
            _pageNames.Add(names[i]);
            _setups[names[i]] = Setup(section);
        }

        var flow = new FlowDocument(_setups[names[0]])
        {
            PageSetupResolver = page => _setups.TryGetValue(page.Name ?? names[0], out var setup) ? setup : _setups[names[0]],
        };

        for (var i = 0; i < names.Length; i++)
        {
            var section = _document.Sections[i];
            var width = _setups[names[i]].ContentWidth;
            var container = new ContainerBlock(new BoxStyle
            {
                BreakBefore = section.Start switch
                {
                    SectionStart.EvenPage => PageBreak.Left,
                    SectionStart.OddPage => PageBreak.Right,
                    _ => PageBreak.Auto,
                },
            })
            { PageName = names[i] };
            foreach (var block in RenderBlocks(section.Blocks, width, inTable: false))
            {
                container.Add(block);
            }

            foreach (var block in Footnotes(width))
            {
                container.Add(block);
            }

            if (i == names.Length - 1)
            {
                foreach (var block in Endnotes(width))
                {
                    container.Add(block);
                }
            }

            if (container.Children.Count == 0)
            {
                container.Add(new ParagraphBlock(new Paragraph(_styles.Style(RunFormat.Default, ParagraphFormat.Default))));
            }

            flow.Add(container);
        }

        flow.PageDecorator = Decorator(names[0]);
        return flow;
    }

    /// <summary>
    /// A section's page setup: its size and margins, the top and bottom margins grown where its header or footer is
    /// taller than the room the margin leaves (as word processors do).
    /// </summary>
    private PageSetup Setup(WordSection section)
    {
        var width = Math.Max(1, section.PageWidth - section.MarginLeft - section.MarginRight);
        var header = HeaderFooterHeight(section.Headers, section, width);
        var footer = HeaderFooterHeight(section.Footers, section, width);
        var top = Math.Max(section.MarginTop, section.HeaderDistance + header);
        var bottom = Math.Max(section.MarginBottom, section.FooterDistance + footer);
        var room = section.PageHeight - 72;
        if (top + bottom > room)
        {
            // Headers and footers may not take the whole page: an inch is left for the text.
            (top, bottom) = (Math.Max(section.MarginTop, top * room / (top + bottom)), Math.Max(section.MarginBottom, bottom * room / (top + bottom)));
            if (top + bottom > room)
            {
                (top, bottom) = (section.MarginTop, section.MarginBottom);
            }
        }

        return new PageSetup(new PdfPageSize(section.PageWidth, section.PageHeight), new Thickness(top, section.MarginRight, bottom, section.MarginLeft));
    }

    /// <summary>The height of the tallest header (or footer) a section uses.</summary>
    private double HeaderFooterHeight(Dictionary<HeaderFooterKind, List<WordBlock>> parts, WordSection section, double width)
    {
        var height = 0.0;
        foreach (var (kind, blocks) in parts)
        {
            if ((kind == HeaderFooterKind.First && !section.TitlePage) || (kind == HeaderFooterKind.Even && !_document.EvenAndOddHeaders) || blocks.Count == 0)
            {
                continue;
            }

            _page = new PageFields(88, section.PageNumberFormat, 88, 88);
            try
            {
                height = Math.Max(height, FlowDocument.MeasureBlocks(RenderBlocks(blocks, width, inTable: false), width));
            }
            finally
            {
                _page = null;
            }
        }

        return height;
    }

    /// <summary>
    /// The page decorator: each page's header and footer (first page of the section, even page or default), with the
    /// page number counted by the sections' numbering.
    /// </summary>
    private Action<PdfCanvas, PageInfo> Decorator(string firstName)
    {
        string? last = null;
        var number = 0;
        return (canvas, page) =>
        {
            var name = page.Context.Name ?? firstName;
            if (!_sectionOfPageName.TryGetValue(name, out var index))
            {
                return;
            }

            var section = _document.Sections[index];
            var first = name != last;
            number = first && section.PageNumberStart is { } start ? start : number + 1;
            last = name;
            var kind = first && section.TitlePage ? HeaderFooterKind.First
                : _document.EvenAndOddHeaders && number % 2 == 0 ? HeaderFooterKind.Even
                : HeaderFooterKind.Default;
            var sectionPages = _sectionPageCounts is { } counts && counts.TryGetValue(name, out var count) ? count : 0;
            var width = Math.Max(1, section.PageWidth - section.MarginLeft - section.MarginRight);
            _page = new PageFields(number, section.PageNumberFormat, page.Count, sectionPages);
            try
            {
                if (section.Headers.TryGetValue(kind, out var header) && header.Count > 0)
                {
                    FlowDocument.DrawBlocks(canvas, RenderBlocks(header, width, inTable: false), section.MarginLeft, page.Setup.Size.Height - section.HeaderDistance, width);
                }

                if (section.Footers.TryGetValue(kind, out var footer) && footer.Count > 0)
                {
                    var blocks = RenderBlocks(footer, width, inTable: false);
                    var height = FlowDocument.MeasureBlocks(blocks, width);
                    FlowDocument.DrawBlocks(canvas, blocks, section.MarginLeft, section.FooterDistance + height, width);
                }
            }
            finally
            {
                _page = null;
            }
        };
    }

    /// <summary>The footnotes referenced since the last call, after a short rule, each with its number.</summary>
    private List<Block> Footnotes(double width)
    {
        var blocks = new List<Block>();
        if (_pendingFootnotes.Count == 0)
        {
            return blocks;
        }

        blocks.Add(NoteRule(width));
        foreach (var (_, id, number) in _pendingFootnotes.ToList())
        {
            blocks.Add(Note(_document.Footnotes, id, number, "fn", _document.FootnoteFormat, width));
        }

        _pendingFootnotes.Clear();
        return blocks;
    }

    /// <summary>The endnotes, after a short rule, each with its number.</summary>
    private List<Block> Endnotes(double width)
    {
        var blocks = new List<Block>();
        if (_endnotes.Count == 0)
        {
            return blocks;
        }

        blocks.Add(NoteRule(width));
        foreach (var (id, number) in _endnotes.ToList())
        {
            blocks.Add(Note(_document.Endnotes, id, number, "en", _document.EndnoteFormat, width));
        }

        return blocks;
    }

    /// <summary>The short rule above notes.</summary>
    private static ContainerBlock NoteRule(double width) => new(new BoxStyle
    {
        Width = Length.FromPoints(Math.Min(144, width / 3)),
        Margin = new Thickness(12, 0, 6, 0),
        Border = new Borders(new BorderSide(0.5, PdfColor.Black), BorderSide.None, BorderSide.None, BorderSide.None),
        BreakAfter = PageBreak.Avoid,
    });

    /// <summary>A note's blocks in a container that its references link to.</summary>
    private ContainerBlock Note(Dictionary<string, List<WordBlock>> notes, string id, int number, string prefix, NumberFormat format, double width)
    {
        var container = new ContainerBlock { Id = NoteDestination(prefix, number) };
        if (notes.TryGetValue(id, out var blocks) && _depth < 2)
        {
            _currentNote = (number, format);
            _depth++;
            try
            {
                foreach (var block in RenderBlocks(blocks, width, inTable: false))
                {
                    container.Add(block);
                }
            }
            finally
            {
                _depth--;
                _currentNote = null;
            }
        }

        return container;
    }

    private static string NoteDestination(string prefix, int number) => "_" + prefix + number.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// How WMF and EMF pictures are drawn: their text in the document's fonts (the same resolution as runs), their
    /// problems reported for the image.
    /// </summary>
    private Metafiles.MetafileOptions Metafile(string name) => new()
    {
        Fonts = (face, bold, italic) => _styles.Fonts(face, bold, italic),
        Report = (message, damaged) => _context.Report(damaged ? DocumentDiagnosticKind.ImageNotLoaded : DocumentDiagnosticKind.UnsupportedContent, $"{name}: {message}"),
    };

    /// <summary>Loads an image once (shared by its uses), or <c>null</c> with a diagnostic if it cannot be drawn.</summary>
    private PdfImage? Image(ImageInline image)
    {
        if (_images.TryGetValue(image.Data, out var loaded))
        {
            return loaded;
        }

        var name = image.Name is { } n ? $"The image '{n}'" : "An image";
        try
        {
            loaded = PdfImage.IsMetafile(image.Data) ? PdfImage.LoadMetafile(image.Data, Metafile(name)) : PdfImage.Load(image.Data);
        }
        catch (ImageFormatException)
        {
            _context.Report(DocumentDiagnosticKind.ImageNotLoaded, $"{name} is in a format that cannot be drawn (such as PICT) or is damaged; it is left out.");
            loaded = null;
        }

        _images[image.Data] = loaded;
        return loaded;
    }
}
