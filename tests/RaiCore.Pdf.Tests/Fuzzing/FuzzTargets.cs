using System.Text;
using RaiCore.Pdf.Documents;
using RaiCore.Pdf.Editing;
using RaiCore.Pdf.Email;
using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Fonts.OpenType;
using RaiCore.Pdf.Html;
using RaiCore.Pdf.Images;
using RaiCore.Pdf.Layout;
using RaiCore.Pdf.Layout.Text;
using RaiCore.Pdf.Sheets;
using RaiCore.Pdf.Slides;
using RaiCore.Pdf.Xfa;

namespace RaiCore.Pdf.Tests.Fuzzing;

/// <summary>
/// The fuzz targets: each feeds one input to a parser or decoder and uses the result the way the library does
/// (layout, embedding, writing). Shared by the fuzzer (tests/RaiCore.Pdf.Fuzz) and the corpus replay test.
/// </summary>
/// <remarks>
/// An input may make a target fail only with a documented exception (<see cref="IsExpected"/>): malformed data,
/// an exceeded resource limit or cancellation. Anything else (an <see cref="IndexOutOfRangeException"/>, a
/// <see cref="NullReferenceException"/>, an <see cref="OverflowException"/>, running out of memory, a hang or a stack
/// overflow) is a bug.
/// </remarks>
public static class FuzzTargets
{
    private static ResourceLimits? _limits;

    /// <summary>Tight limits, so that one input stays fast: hostile input must hit them, not stall the fuzzer.</summary>
    /// <remarks>
    /// Built on first use, not in a static initializer: under libFuzzer the library is instrumented, and instrumented
    /// code must not run before <c>Fuzzer.LibFuzzer.Run</c> has set up its coverage map (it crashes with an access
    /// violation). Choosing a target by name (<see cref="All"/>) runs no library code.
    /// </remarks>
    public static ResourceLimits Limits => _limits ??= ResourceLimits.Default with
    {
        MaxElements = 20_000,
        MaxTableCells = 100_000,
        MaxImagePixels = 4_000_000,
        MaxResourceBytes = 4 * 1024 * 1024,
        MaxFontBytes = 16 * 1024 * 1024,
        MaxDecompressedBytes = 32 * 1024 * 1024,
        MaxPages = 50,
        Timeout = TimeSpan.FromSeconds(5),
    };

    /// <summary>The targets by name: the name is also the folder of their seed corpus.</summary>
    public static IReadOnlyDictionary<string, Action<byte[]>> All { get; } = new Dictionary<string, Action<byte[]>>(StringComparer.Ordinal)
    {
        ["html"] = Html,
        ["css"] = Css,
        ["htmlfragment"] = Fragment,
        ["png"] = input => Image(WithValidPngCrcs(input)),
        ["jpeg"] = Image,
        ["gif"] = Image,
        ["bmp"] = Image,
        ["webp"] = Image,
        ["svg"] = Image,
        ["font"] = Font,
        ["woff"] = Font,
        ["woff2"] = Font,
        ["pdf"] = Pdf,
        ["extract"] = Extract,
        ["email"] = Email,
        ["tiff"] = Tiff,
        ["csv"] = Csv,
        ["edit"] = Edit,
        ["cfb"] = Cfb,
        ["xlsx"] = Xlsx,
        ["xls"] = Xls,
        ["workbook"] = WorkbookEdit,
        ["pptx"] = Pptx,
        ["ppt"] = Ppt,
        ["docx"] = Docx,
        ["odt"] = Odt,
        ["rtf"] = Rtf,
        ["doc"] = Doc,
        ["doctext"] = DocumentText,
        ["wmf"] = Metafile,
        ["emf"] = Metafile,
        ["chart"] = Chart,
        ["sheetdrawing"] = SheetDrawing,
        ["xlsdrawing"] = XlsDrawing,
        ["xfa"] = Xfa,
        ["cfformula"] = ConditionalFormula,
    };

    /// <summary>Runs a target on one input; expected exceptions are swallowed, anything else is thrown.</summary>
    public static void Run(string target, byte[] input)
    {
        try
        {
            using (Limits.Enforce(CancellationToken.None))
            {
                All[target](input);
            }
        }
        catch (Exception ex) when (IsExpected(ex))
        {
        }
    }

    /// <summary>Whether an exception is one a target may document for malformed or hostile input.</summary>
    public static bool IsExpected(Exception exception) => exception is ImageFormatException or FontFormatException
        or PdfFormatException or PdfPasswordException or ResourceLimitExceededException or OperationCanceledException or NotSupportedException
        or DocumentFormatException or SpreadsheetFormatException or PresentationFormatException or MessageFormatException
        or XfaFormatException;

    /// <summary>HTML conversion, with no resources loaded (data: URIs only).</summary>
    public static void Html(byte[] input) => Html(input, ModeOf(input));

    /// <summary>HTML conversion in the given output mode.</summary>
    public static void Html(byte[] input, OutputMode mode) => Convert(Encoding.UTF8.GetString(input), mode);

    /// <summary>
    /// An HTML fragment (<see cref="HtmlFragment"/>) placed between code-built paragraphs and in a table cell, with or
    /// without a base style and CSS, in a tagged or untagged document (chosen by the input's length).
    /// </summary>
    public static void Fragment(byte[] input)
    {
        var html = Encoding.UTF8.GetString(input);
        var fonts = BundledFonts.CreateCollection();
        var body = new TextStyle(fonts.Find("serif")!, 11) { LineHeight = LineHeight.Multiple(1.3) };
        HtmlFragmentOptions Options(double width) => new()
        {
            Fonts = fonts,
            BaseStyle = input.Length % 2 == 0 ? body : null,
            Css = input.Length % 3 == 0 ? "p { margin: 0 } td, th { padding: 2px; border: 1px solid }" : null,
            Width = width,
            Limits = Limits,
            ResourceLoader = _ => null,
        };

        var flow = new FlowDocument(new PageSetup(PdfPageSize.A5, Thickness.All(36)));
        flow.Add(new ParagraphBlock(new Paragraph(body).Add("Before the fragment.")));
        HtmlFragment.Parse(html, Options(flow.Setup.ContentWidth)).AddTo(flow);
        var cell = HtmlFragment.Parse(html, Options(150));
        flow.Add(new TableBlock().Add(new TableRow(new TableCell(new Paragraph(body).Add("Cell")), new TableCell(cell.Blocks) { Width = Length.FromPoints(150) })));
        flow.Add(new ParagraphBlock(new Paragraph(body).Add("After the fragment.")));
        var document = new PdfDocument { Tagged = input.Length % 4 == 1 };
        flow.RenderTo(document);
        document.ToArray();
    }

    /// <summary>CSS: the input as a style sheet for a document that uses most kinds of boxes.</summary>
    public static void Css(byte[] input) => Css(input, ModeOf(input));

    /// <summary>CSS in the given output mode.</summary>
    public static void Css(byte[] input, OutputMode mode) => Convert(
        $"""
        <style>{Encoding.UTF8.GetString(input)}</style>
        <h1 id="t">Title</h1>
        <div class="a"><p class="b">Some <span>inline <b>text</b></span> and <a href="#t">a link</a>.</p>
        <ul><li>one</li><li class="c">two</li></ul>
        <table><tr><th>H</th><td>1</td></tr><tr><td colspan="2">2</td></tr></table>
        <div style="display: flex"><div>a</div><div>b</div></div>
        <span class="d" style="display: inline-block">box</span></div>
        """,
        mode);

    /// <summary>An image of any supported format, drawn on a page and written.</summary>
    public static void Image(byte[] input)
    {
        var image = PdfImage.Load(input);
        var document = new PdfDocument();
        document.AddPage(PdfPageSize.A4).Canvas.DrawImage(image, 10, 10, 100, 100);
        document.ToArray();
    }

    /// <summary>
    /// A WMF or EMF picture (with its EMF+ records) as a converter draws it: its text in the bundled fonts, its problems
    /// reported; drawn on a page, cropped, and written.
    /// </summary>
    public static void Metafile(byte[] input)
    {
        var fonts = BundledFonts.CreateCollection();
        var reports = 0;
        var options = new RaiCore.Pdf.Metafiles.MetafileOptions
        {
            Fonts = (face, bold, italic) => new[] { fonts.Find(face, bold ? 700 : 400, italic), fonts.Find("sans-serif", bold ? 700 : 400, italic) }.OfType<PdfFont>().ToArray(),
            Report = (_, _) => reports++,
        };
        var image = PdfImage.LoadMetafile(input, options);
        var document = new PdfDocument();
        var canvas = document.AddPage(PdfPageSize.A4).Canvas;
        canvas.DrawImage(image, 10, 10, 100, 100);
        canvas.SaveState().Rectangle(200, 200, 50, 50).Clip().EndPath().DrawImage(image, 150, 150, 200, 100).RestoreState();
        document.ToArray();
    }

    /// <summary>A TIFF: its pages (the first four) drawn on pages and written.</summary>
    public static void Tiff(byte[] input)
    {
        var document = new PdfDocument();
        var pages = Math.Min(PdfImage.GetPageCount(input), 4);
        for (var i = 0; i < pages; i++)
        {
            document.AddPage(PdfPageSize.A4).Canvas.DrawImage(PdfImage.Load(input, i), 10, 10, 100, 100);
        }

        document.ToArray();
    }

    /// <summary>A font (TrueType, OpenType, TTC, WOFF, WOFF2): shaped, measured, embedded as a subset and written.</summary>
    public static void Font(byte[] input)
    {
        var font = PdfFont.Load(input);
        if (font is OpenTypePdfFont { Font.EmbeddingRestricted: true })
        {
            return; // Drawing it throws InvalidOperationException by design: its license forbids embedding.
        }

        TextShaper.Shape("Hello, w\u00F6rld! fi 123", font);
        var document = new PdfDocument();
        document.AddPage(PdfPageSize.A4).Canvas.DrawText("Hello, w\u00F6rld! fi 123", font, 12, 10, 10);
        document.ToArray();
    }

    /// <summary>A PDF file: read, every page's content decoded, imported into a new document and written.</summary>
    public static void Pdf(byte[] input)
    {
        var reader = PdfReader.Open(input);
        foreach (var page in reader.Pages)
        {
            page.GetContent();
        }

        var document = new PdfDocument();
        document.ImportPages(reader);
        document.ToArray();
    }

    /// <summary>
    /// Editing: the file opened in the editor (its whole object graph copied), its form read, pages rotated, moved,
    /// duplicated from the same file and deleted, form fields and annotations flattened, the result optimized, split by
    /// size and saved, with and without object streams. Every saved file must read back: a file the editor writes that
    /// the reader rejects is a bug, not malformed input.
    /// </summary>
    public static void Edit(byte[] input)
    {
        var editor = PdfEditor.Open(input);
        _ = editor.GetForm().Fields.Count;
        var pages = editor.Pages;
        if (pages.Count > 0)
        {
            pages[0].Rotation += 90;
            _ = (pages[0].MediaBox, pages[0].CropBox, pages[0].Width, pages[0].Height);
            pages.Move(pages.Count - 1, 0);
            pages.Insert(pages.Count, PdfReader.Open(input), [0, 0]);
            pages.Delete(0);
        }

        ReadBack(editor.ToArray());
        editor.FlattenForm();
        editor.FlattenAnnotations(new PdfAnnotationFlattenOptions { KeepLinks = (input.Length & 1) == 0 });
        if (pages.Count > 0)
        {
            editor.Optimize();
            foreach (var part in editor.SplitBySize(Math.Max(1024, input.Length / 2)).Take(8))
            {
                ReadBack(part.Data.ToArray());
            }
        }

        editor.UseObjectStreams = !editor.UseObjectStreams;
        ReadBack(editor.ToArray());
    }

    /// <summary>
    /// Reads a file the editor saved: its structure (cross-reference table, catalog, page tree, page dictionaries) must
    /// not be malformed. Page content is copied as it is, so a stream the source could not decode stays undecodable.
    /// </summary>
    private static void ReadBack(byte[] saved)
    {
        PdfReader reader;
        try
        {
            reader = PdfReader.Open(saved);
            foreach (var page in reader.Pages)
            {
                _ = (page.Dictionary.Count, page.CropBox, page.Rotate, page.Resources.Count);
            }
        }
        catch (PdfFormatException ex)
        {
            throw new InvalidOperationException("The editor saved a file the reader rejects: " + ex.Message, ex);
        }

        foreach (var page in reader.Pages)
        {
            page.GetContent();
        }
    }

    /// <summary>
    /// Reading pages: every page's content run through the extraction interpreter (fonts, CMaps, forms, images), its
    /// text put together in each mode (lines, columns, bidi) and searched, and its images listed and exported.
    /// </summary>
    public static void Extract(byte[] input)
    {
        var reader = PdfReader.Open(input);
        var interpreter = new RaiCore.Pdf.Extraction.Content.ContentInterpreter(reader, new CountingSink());
        foreach (var page in reader.Pages)
        {
            interpreter.Run(page);
        }

        foreach (var mode in Enum.GetValues<RaiCore.Pdf.Extraction.TextExtractionMode>())
        {
            _ = new RaiCore.Pdf.Extraction.TextExtractor(reader, new RaiCore.Pdf.Extraction.TextExtractionOptions { Mode = mode }).ExtractText();
        }

        // Search: plain text across whitespace and a regular expression, with rectangles mapped to user space.
        var search = new RaiCore.Pdf.Extraction.TextExtractor(reader);
        _ = search.Find("e a", ignoreCase: true).Count();
        _ = search.Find(SearchPattern).Count();

        // Images: every placement, and its data exported (decoded and converted to PNG or TIFF where it can be).
        foreach (var page in new RaiCore.Pdf.Extraction.ImageExtractor(reader).ExtractPages())
        {
            foreach (var image in page.Images.Take(16))
            {
                _ = image.GetData();
            }
        }
    }

    /// <summary>A pattern for the search part of <see cref="Extract"/>: non-backtracking, so its time is linear in the text.</summary>
    private static readonly System.Text.RegularExpressions.Regex SearchPattern = new(@"\w+\s\d", System.Text.RegularExpressions.RegexOptions.NonBacktracking);

    /// <summary>
    /// An email message or web archive: parsed, read like a mail client and converted, with no resources loaded but the
    /// message's own; every other input (by length) with its attachments appended, so attached PDFs, HTML files and
    /// messages are converted too.
    /// </summary>
    public static void Email(byte[] input) => EmailToPdf.ConvertToBytes(input, new EmailConverterOptions
    {
        AppendAttachments = input.Length % 2 == 0,
        Html = new HtmlConverterOptions { Limits = Limits, ResourceLoader = _ => null, Diagnostics = _ => { } },
    });

    /// <summary>
    /// A compound file (the container of .msg, .doc, .xls and .ppt): its header, FAT and directory read, every stream
    /// read (at most 64 of them), and the property set streams (names starting with U+0005) parsed.
    /// </summary>
    public static void Cfb(byte[] input)
    {
        var file = RaiCore.Pdf.Cfb.CompoundFile.Open(input, Limits);
        var storages = new Stack<RaiCore.Pdf.Cfb.CfbEntry>([file.Root]);
        var read = 0;
        while (storages.TryPop(out var storage) && read < 64)
        {
            foreach (var entry in storage.Children)
            {
                if (entry.Kind != RaiCore.Pdf.Cfb.CfbEntryKind.Stream)
                {
                    storages.Push(entry);
                }
                else if (read++ < 64)
                {
                    var data = file.Read(entry);
                    if (entry.Name.StartsWith('\u0005'))
                    {
                        _ = RaiCore.Pdf.Cfb.PropertySetStream.Read(data);
                    }
                }
            }
        }
    }

    /// <summary>
    /// CSV data: decoded (encoding detected), split into records (delimiter detected), its cells classified, and laid
    /// out as tables with fitted columns; every other input (by length) on landscape pages fitted to the width, every
    /// third one tagged, every other odd one with diagnostics.
    /// </summary>
    public static void Csv(byte[] input) => CsvToPdf.ConvertToBytes(input, new CsvConverterOptions
    {
        Sheet = new SheetOptions
        {
            Limits = Limits,
            Landscape = input.Length % 2 == 0,
            FitToWidth = input.Length % 2 == 0,
            Tagged = input.Length % 3 == 0,
            Diagnostics = input.Length % 4 == 1 ? _ => { } : null,
        },
    });

    /// <summary>
    /// An XLSX workbook: its package and parts read (shared strings, styles, theme, defined names), every visible
    /// worksheet with its values formatted, and laid out with its styles, merged cells, sizes and page setup;
    /// every other input (by length) with sheet names as headings and fitted to the width, every third one tagged, every
    /// fifth one fitted to the content; every odd one with diagnostics (its drawings and comments read too).
    /// </summary>
    public static void Xlsx(byte[] input) => XlsxToPdf.ConvertToBytes(input, new XlsxConverterOptions
    {
        Sheet = new SheetOptions
        {
            Limits = Limits,
            FitToWidth = input.Length % 2 == 0,
            Tagged = input.Length % 3 == 0,
            Diagnostics = input.Length % 2 == 1 ? _ => { } : null,
        },
        ShowSheetNames = input.Length % 2 == 0,
        AutoFit = input.Length % 5 == 0,
    });

    /// <summary>
    /// An Excel 97-2003 workbook (a compound file, or a bare BIFF stream): its records read (shared strings across
    /// CONTINUE records, formats, sheets, names), every visible worksheet with its values formatted, and laid out as an
    /// XLSX worksheet is; every other input (by length) with sheet names as headings and fitted to the width, every third
    /// one tagged, every fifth one fitted to the content, every odd one with diagnostics.
    /// </summary>
    public static void Xls(byte[] input) => XlsToPdf.ConvertToBytes(input, new XlsxConverterOptions
    {
        Sheet = new SheetOptions
        {
            Limits = Limits,
            FitToWidth = input.Length % 2 == 0,
            Tagged = input.Length % 3 == 0,
            Diagnostics = input.Length % 2 == 1 ? _ => { } : null,
        },
        ShowSheetNames = input.Length % 2 == 0,
        AutoFit = input.Length % 5 == 0,
    });

    /// <summary>
    /// The Workbook API: a workbook opened (XLSX, XLS or delimited text, told apart by the content), every worksheet's
    /// values read, displayed and searched; an XLSX one edited (values written, rows and columns inserted and deleted,
    /// where by the input's length), saved and opened again, which must work: a workbook the API writes that it cannot
    /// read is a bug. Every input is saved as delimited text. Splitting an array formula or a table header is the
    /// documented <see cref="InvalidOperationException"/>.
    /// </summary>
    public static void WorkbookEdit(byte[] input)
    {
        var options = new WorkbookOptions { Limits = Limits };
        var workbook = Workbook.Open(input, options);
        foreach (var sheet in workbook.Worksheets.Take(4))
        {
            foreach (var cell in sheet.Cells.Take(2000))
            {
                _ = (cell.Value, cell.Formula, cell.NumberFormat, cell.GetDisplayText());
            }

            _ = (sheet.Find("a").Count, sheet.Row(1).IsHidden, sheet.Column(1).IsHidden, sheet.MergedRanges.Count);
        }

        if (!workbook.IsReadOnly && workbook.Worksheets.Count > 0)
        {
            var sheet = workbook.Worksheets[input.Length % workbook.Worksheets.Count];
            var at = 1 + (input.Length % 7);
            Action[] edits =
            [
                () => sheet["B2"].SetValue("fuzz"),
                () => sheet["C3"].SetValue(new DateTime(2026, 11, 23)),
                () => sheet.InsertRows(at, 1 + (input.Length % 3)),
                () => sheet.DeleteRows(at + 1),
                () => sheet.InsertColumns(at),
                () => sheet.DeleteColumns(at, 2),
                () => sheet.Row(at).IsHidden = true,
            ];
            foreach (var edit in edits)
            {
                try
                {
                    edit();
                }
                catch (InvalidOperationException)
                {
                }
            }

            var saved = workbook.ToArray();
            try
            {
                var reopened = Workbook.Open(saved, options);
                foreach (var cell in reopened.Worksheets.SelectMany(s => s.Cells).Take(2000))
                {
                    _ = cell.Value;
                }
            }
            catch (SpreadsheetFormatException ex)
            {
                throw new InvalidOperationException("The Workbook API saved a workbook it cannot open: " + ex.Message, ex);
            }
        }

        using var csv = new MemoryStream();
        workbook.SaveAsCsv(csv, new CsvSaveOptions { Separator = input.Length % 2 == 0 ? ';' : ',', FormattedValues = input.Length % 3 != 0 });
    }

    /// <summary>
    /// A PPTX presentation: its package and parts read (presentation, slides, layouts, masters, themes, table styles,
    /// SmartArt drawings, media), every slide drawn with its shapes, text, pictures and tables; every other input (by
    /// length) with hidden slides too, every third one tagged.
    /// </summary>
    public static void Pptx(byte[] input) => PptxToPdf.ConvertToBytes(input, new PptxConverterOptions
    {
        Limits = Limits,
        IncludeHiddenSlides = input.Length % 2 == 0,
        Tagged = input.Length % 3 == 0,
        Diagnostics = _ => { },
    });

    /// <summary>
    /// A PowerPoint 97-2003 presentation: its compound file, edit history and persist directory, document, masters
    /// and slides read, every slide drawn with its shapes, text, pictures and groups; every other input (by length)
    /// with hidden slides too, every third one tagged.
    /// </summary>
    public static void Ppt(byte[] input) => PptToPdf.ConvertToBytes(input, new PptxConverterOptions
    {
        Limits = Limits,
        IncludeHiddenSlides = input.Length % 2 == 0,
        Tagged = input.Length % 3 == 0,
        Diagnostics = _ => { },
    });

    /// <summary>
    /// A Word document: its package and parts read (document, styles, numbering, settings, theme, headers, footers,
    /// notes, comments, media) and laid out; every third one (by length) tagged, every other one without heading
    /// bookmarks, comments left out, listed at the end or as notes (by length / 3).
    /// </summary>
    public static void Docx(byte[] input) => DocxToPdf.ConvertToBytes(input, new DocxConverterOptions
    {
        Limits = Limits,
        Tagged = input.Length % 3 == 0,
        HeadingBookmarks = input.Length % 2 == 0,
        Comments = CommentsFor(input),
        Diagnostics = _ => { },
    });

    /// <summary>
    /// An XFA form: an XDP document or a PDF file with XFA packets, read (no DTDs, elements and nesting bounded),
    /// prototypes resolved, data merged (repeated subforms bounded by the element limit), laid out, paginated and drawn;
    /// every other input (by length) without the fonts a PDF embeds for its form.
    /// </summary>
    public static void Xfa(byte[] input) => XfaToPdf.ConvertToBytes(input, new XfaConverterOptions
    {
        Limits = Limits,
        UseEmbeddedFonts = input.Length % 2 == 0,
        Diagnostics = _ => { },
    });

    /// <summary>
    /// A chart part (<c>c:chartSpace</c> or, for the newer chart types, <c>cx:chartSpace</c> XML, as in XLSX, DOCX and
    /// PPTX packages): parsed as the packages parse parts
    /// (no DTDs, elements bounded), read by the chart engine (every other input with a theme and cell values for
    /// references without caches), drawn on a page and as an image (every third input in a tagged document, with the
    /// bundled fonts for even lengths) and written.
    /// </summary>
    public static void Chart(byte[] input)
    {
        System.Xml.Linq.XElement root;
        try
        {
            var settings = new System.Xml.XmlReaderSettings
            {
                DtdProcessing = System.Xml.DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 4 * 1024 * 1024,
                MaxCharactersFromEntities = 1024,
            };
            using var reader = System.Xml.XmlReader.Create(new MemoryStream(input), settings);
            root = System.Xml.Linq.XElement.Load(reader);
        }
        catch (System.Xml.XmlException)
        {
            return;
        }

        if (root.DescendantsAndSelf().Take(Limits.MaxElements + 1).Count() > Limits.MaxElements)
        {
            return;
        }

        var odd = input.Length % 2 == 1;
        var chart = RaiCore.Pdf.Charts.ChartReader.Read(root, odd ? name => name == "accent1" ? PdfColor.FromRgb(200, 30, 30) : null : null, Limits, [],
            odd ? formula => new RaiCore.Pdf.Charts.ChartReference([1.0, "two", 3.0, null, -5.0, "two", 7.5], formula.Length % 2 == 0 ? "0.0%" : null, 1 + (formula.Length % 3)) : null);
        if (chart is null)
        {
            return;
        }

        var bundled = input.Length % 2 == 0 ? BundledFonts.CreateCollection() : null;
        var options = new RaiCore.Pdf.Charts.ChartRenderOptions
        {
            Fonts = (typeface, bold, italic) => bundled?.Find(typeface ?? "sans-serif", bold ? 700 : 400, italic) is { } found
                ? [found]
                : [PdfFont.Standard(bold ? StandardFont.HelveticaBold : StandardFont.Helvetica)],
            DefaultFontSize = input.Length % 5 == 0 ? 18 : 10,
            OpaqueChartArea = input.Length % 3 != 0,
        };
        var document = new PdfDocument { Tagged = input.Length % 3 == 0 };
        var page = document.AddPage(PdfPageSize.A4);
        RaiCore.Pdf.Charts.ChartRenderer.Draw(page.Canvas, chart, 40, 300, 480, 300, options);
        page.Canvas.DrawImage(RaiCore.Pdf.Charts.ChartRenderer.ToImage(chart, 200, 120, options), 40, 100, 200, 120);
        document.ToArray();
    }

    /// <summary>
    /// A worksheet's drawing part (<c>xdr:wsDr</c> XML) in a workbook with a picture (PNG) and a chart part it may refer
    /// to (<c>rId1</c>, <c>rId2</c>): its anchors, pictures, shapes, text, groups and charts read and drawn over the
    /// cells; every other input (by length) fitted to the width (so drawings cross bands of columns less often), every
    /// third one tagged.
    /// </summary>
    public static void SheetDrawing(byte[] input) => XlsxToPdf.ConvertToBytes(DrawingInputs.Xlsx(input), new XlsxConverterOptions
    {
        Sheet = new SheetOptions
        {
            Limits = Limits,
            FitToWidth = input.Length % 2 == 0,
            Tagged = input.Length % 3 == 0,
            Diagnostics = _ => { },
        },
    });

    /// <summary>
    /// A binary worksheet's drawing (the OfficeArt data of its MSODRAWING records) in a BIFF8 workbook with a BLIP store
    /// (a PNG and a DIB) and as many OBJ records (pictures, shapes, text boxes, by the input) as the input's length
    /// picks, with a text box's TXO: its shapes, groups, anchors and pictures read and drawn.
    /// </summary>
    public static void XlsDrawing(byte[] input) => XlsToPdf.ConvertToBytes(DrawingInputs.Xls(input), new XlsxConverterOptions
    {
        Sheet = new SheetOptions
        {
            Limits = Limits,
            Tagged = input.Length % 3 == 0,
            Diagnostics = _ => { },
        },
    });

    /// <summary>
    /// An OpenDocument text document: its package (or flat XML document) and parts read (content, styles, meta,
    /// settings, pictures, comments) and laid out; every third one (by length) tagged, every other one without heading
    /// bookmarks, comments left out, listed at the end or as notes (by length / 3).
    /// </summary>
    public static void Odt(byte[] input) => OdtToPdf.ConvertToBytes(input, new OdtConverterOptions
    {
        Limits = Limits,
        Tagged = input.Length % 3 == 0,
        HeadingBookmarks = input.Length % 2 == 0,
        Comments = CommentsFor(input),
        Diagnostics = _ => { },
    });

    /// <summary>
    /// RTF conversion (groups, destinations, code pages, lists, tables, fields, pictures, comments), every third input (by
    /// length) tagged, every other one without heading bookmarks, comments left out, listed at the end or as notes (by
    /// length / 3).
    /// </summary>
    public static void Rtf(byte[] input) => RtfToPdf.ConvertToBytes(input, new RtfConverterOptions
    {
        Limits = Limits,
        Tagged = input.Length % 3 == 0,
        HeadingBookmarks = input.Length % 2 == 0,
        Comments = CommentsFor(input),
        Diagnostics = _ => { },
    });

    /// <summary>
    /// Word 97-2003 conversion (compound file, FIB, piece table, formatted disk pages, style sheet, lists, tables,
    /// fields, pictures, comments), every third input (by length) tagged, every other one without heading bookmarks,
    /// comments left out, listed at the end or as notes (by length / 3).
    /// </summary>
    public static void Doc(byte[] input) => DocToPdf.ConvertToBytes(input, new DocConverterOptions
    {
        Limits = Limits,
        Tagged = input.Length % 3 == 0,
        HeadingBookmarks = input.Length % 2 == 0,
        Comments = CommentsFor(input),
        Diagnostics = _ => { },
    });

    /// <summary>How the word-processing targets print comments: by the input's length / 3, so each mode meets tagging.</summary>
    private static DocumentComments CommentsFor(byte[] input) => (DocumentComments)(input.Length / 3 % 3);

    /// <summary>
    /// Text extraction from word-processing documents of any format (DOCX, DOC, RTF, ODT, detected from the data): every
    /// other input (by length) with everything included (headers and footers, notes, comments, hidden and deleted text,
    /// field codes), the others with the defaults.
    /// </summary>
    public static void DocumentText(byte[] input) => Documents.DocumentText.Extract(input, input.Length % 2 == 0
        ? new DocumentTextOptions
        {
            Limits = Limits, IncludeHeadersAndFooters = true, IncludeNotes = true, IncludeComments = true, IncludeHiddenText = true,
            IncludeDeletedText = true, FieldCodes = true,
        }
        : new DocumentTextOptions { Limits = Limits });

    /// <summary>
    /// The PNG with the CRC of every chunk corrected, so that mutated data gets past the CRC check to the decoder
    /// (random changes would almost always be rejected there).
    /// </summary>
    public static byte[] WithValidPngCrcs(byte[] input)
    {
        var data = (byte[])input.Clone();
        var pos = 8;
        while (pos + 12 <= data.Length)
        {
            var length = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos));
            if (length > data.Length - pos - 12)
            {
                break;
            }

            var end = pos + 8 + (int)length;
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(end), Crc32(data.AsSpan(pos + 4, 4 + (int)length)));
            pos = end + 4;
        }

        return data;
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
            }
        }

        return ~crc;
    }

    /// <summary>
    /// The output mode of an HTML or CSS input, from its length, so that the fuzzer varies it along with the input (most
    /// mutations change the length): plain, tagged, PDF/UA, PDF/A-2b, or tagged and written page by page to a stream.
    /// </summary>
    public static OutputMode ModeOf(byte[] input) => (OutputMode)(input.Length % 5);

    private static void Convert(string html, OutputMode mode)
    {
        try
        {
            ConvertStrictly(html, mode);
        }
        catch (InvalidOperationException ex) when (mode is OutputMode.PdfUA or OutputMode.PdfA
            && (ex.Message.Contains("PDF/A", StringComparison.Ordinal) || ex.Message.Contains("PDF/UA", StringComparison.Ordinal)))
        {
            // A document breaking a rule of the standard (a character no font has, a standard font in SVG text):
            // documented, and not what is fuzzed here.
        }
    }

    /// <summary>Converts in the given mode; a document breaking a rule of PDF/UA or PDF/A throws.</summary>
    public static byte[] ConvertStrictly(string html, OutputMode mode)
    {
        var options = new HtmlConverterOptions
        {
            Limits = Limits,
            ResourceLoader = _ => null,
            Diagnostics = _ => { }, // Exercises the diagnostics too (they only run when someone listens).
            Tagged = mode is OutputMode.Tagged or OutputMode.PdfUA or OutputMode.TaggedStream,
            PdfUA = mode == OutputMode.PdfUA,
            PdfAConformance = mode == OutputMode.PdfA ? PdfAConformance.PdfA2B : PdfAConformance.None,

            // PDF/UA needs a title and a language: given here, so that the document's structure is what is checked.
            Title = mode == OutputMode.PdfUA ? "Fuzz" : null,
            Language = mode == OutputMode.PdfUA ? "en" : null,
        };

        if (mode != OutputMode.TaggedStream)
        {
            return HtmlToPdf.ConvertToBytes(html, options);
        }

        using var output = new MemoryStream();
        HtmlToPdf.ConvertToStream(html, output, options);
        return output.ToArray();
    }

    /// <summary>
    /// A formula of conditional formatting: every other input (by length) as the text of an .xlsx workbook's formula
    /// (an expression rule, a cellIs rule's value and a data bar's minimum), parsed and computed over a few cells; the
    /// others as the parsed tokens of an .xls workbook's CF rule (written back as text) and the payload of a CF12 rule.
    /// </summary>
    public static void ConditionalFormula(byte[] input)
    {
        var options = new XlsxConverterOptions
        {
            PrintDate = new DateTime(2026, 3, 4),
            Sheet = new SheetOptions { Limits = Limits, Diagnostics = _ => { } },
        };
        if (input.Length % 2 == 0)
        {
            XlsxToPdf.ConvertToBytes(ConditionalInputs.Xlsx(Encoding.UTF8.GetString(input)), options);
        }
        else
        {
            XlsToPdf.ConvertToBytes(ConditionalInputs.Xls(input), options);
        }
    }

    /// <summary>The workbooks the conditional formatting target wraps its inputs in.</summary>
    internal static class ConditionalInputs
    {
        private const string Spreadsheet = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

        /// <summary>A workbook of one worksheet (numbers, a text, an error) whose rules have the formula.</summary>
        public static byte[] Xlsx(string formula)
        {
            var text = System.Security.SecurityElement.Escape(new string([.. formula.Where(System.Xml.XmlConvert.IsXmlChar)]));
            var rules = $"<conditionalFormatting sqref=\"A1:C4\"><cfRule type=\"expression\" dxfId=\"0\" priority=\"1\"><formula>{text}</formula></cfRule>"
                + $"<cfRule type=\"cellIs\" dxfId=\"0\" priority=\"2\" operator=\"between\"><formula>{text}</formula><formula>A1</formula></cfRule>"
                + $"<cfRule type=\"dataBar\" priority=\"3\"><dataBar><cfvo type=\"formula\" val=\"{text}\"/><cfvo type=\"max\"/><color rgb=\"FF00FF00\"/></dataBar></cfRule></conditionalFormatting>";
            using var stream = new MemoryStream();
            using (var zip = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
            {
                void Text(string path, string content)
                {
                    using var entry = zip.CreateEntry(path).Open();
                    entry.Write(Encoding.UTF8.GetBytes(content));
                }

                const string package = "http://schemas.openxmlformats.org/package/2006/relationships";
                const string relationships = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
                Text("_rels/.rels", $"<Relationships xmlns=\"{package}\"><Relationship Id=\"rId1\" Type=\"{relationships}/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
                Text("xl/workbook.xml", $"<workbook xmlns=\"{Spreadsheet}\" xmlns:r=\"{relationships}\"><sheets><sheet name=\"Sheet1\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
                Text("xl/_rels/workbook.xml.rels", $"<Relationships xmlns=\"{package}\"><Relationship Id=\"rId1\" Type=\"{relationships}/worksheet\" Target=\"worksheets/sheet1.xml\"/>"
                    + $"<Relationship Id=\"rId2\" Type=\"{relationships}/styles\" Target=\"styles.xml\"/></Relationships>");
                Text("xl/styles.xml", $"<styleSheet xmlns=\"{Spreadsheet}\"><cellXfs count=\"1\"><xf/></cellXfs><dxfs count=\"1\"><dxf><font><b/></font></dxf></dxfs></styleSheet>");
                Text("xl/worksheets/sheet1.xml", $"<worksheet xmlns=\"{Spreadsheet}\"><sheetData><row r=\"1\"><c r=\"A1\"><v>1</v></c><c r=\"B1\" t=\"inlineStr\"><is><t>x</t></is></c></row>"
                    + $"<row r=\"3\"><c r=\"C3\" t=\"e\"><v>#N/A</v></c><c r=\"A3\"><v>-2.5</v></c></row></sheetData>{rules}</worksheet>");
            }

            return stream.ToArray();
        }

        /// <summary>
        /// A bare BIFF8 workbook stream of one worksheet (a few numbers) whose CF rule (for A1:C4) has the tokens as its
        /// formula, followed by a CF12 rule whose payload (after its header) is the input.
        /// </summary>
        public static byte[] Xls(byte[] tokens)
        {
            tokens = tokens.Length > 8000 ? tokens[..8000] : tokens;
            byte[] range = [.. DrawingInputs.U16(0), .. DrawingInputs.U16(3), .. DrawingInputs.U16(0), .. DrawingInputs.U16(2)];
            var sheet = new MemoryStream();
            DrawingInputs.Record(sheet, 0x0809, [.. DrawingInputs.U16(0x0600), .. DrawingInputs.U16(0x0010), .. new byte[12]]);
            for (var r = 0; r < 4; r++)
            {
                DrawingInputs.Record(sheet, 0x0203, [.. DrawingInputs.U16(r), .. DrawingInputs.U16(r % 3), .. DrawingInputs.U16(0), .. BitConverter.GetBytes(r - 1.5)]);
            }

            DrawingInputs.Record(sheet, 0x01B0, [.. DrawingInputs.U16(2), .. DrawingInputs.U16(2), .. range, .. DrawingInputs.U16(1), .. range]);
            DrawingInputs.Record(sheet, 0x01B1, [2, 0, .. DrawingInputs.U16(tokens.Length), .. DrawingInputs.U16(0), .. BitConverter.GetBytes(0x20000000u), .. DrawingInputs.U16(0),
                .. DrawingInputs.U16(0), .. DrawingInputs.U16(0x1240), .. tokens]);
            DrawingInputs.Record(sheet, 0x087A, [.. DrawingInputs.U16(0x087A), .. new byte[10], .. tokens]);
            DrawingInputs.Record(sheet, 0x087B, [.. DrawingInputs.U16(0x087B), .. new byte[10], .. tokens[..Math.Min(tokens.Length, 64)]]);
            DrawingInputs.Record(sheet, 0x000A, []);
            var globals = new MemoryStream();
            DrawingInputs.Record(globals, 0x0809, [.. DrawingInputs.U16(0x0600), .. DrawingInputs.U16(0x0005), .. new byte[12]]);
            var boundSheet = globals.Length;
            DrawingInputs.Record(globals, 0x0085, [.. new byte[4], 0, 0, 6, 0, .. "Sheet1"u8]);
            DrawingInputs.Record(globals, 0x000A, []);
            var bytes = globals.ToArray();
            BitConverter.TryWriteBytes(bytes.AsSpan((int)boundSheet + 4), (int)bytes.Length);
            return [.. bytes, .. sheet.ToArray()];
        }
    }

    /// <summary>The workbooks the drawing targets wrap their inputs in (also used by the tests).</summary>
    internal static class DrawingInputs
    {
        /// <summary>A PNG of one pixel.</summary>
        public static readonly byte[] Png = System.Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

        private const string Spreadsheet = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        private const string Relationships = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        private const string Package = "http://schemas.openxmlformats.org/package/2006/relationships";

        /// <summary>
        /// A workbook of one worksheet (a few cells) whose drawing part is <paramref name="drawing"/>, with a picture
        /// (<c>rId1</c>, a PNG) and a chart (<c>rId2</c>) it may refer to.
        /// </summary>
        public static byte[] Xlsx(byte[] drawing, string sheetData = "<row r=\"1\"><c r=\"A1\"><v>1</v></c><c r=\"B1\"><v>2</v></c></row><row r=\"3\" ht=\"30\" customHeight=\"1\"><c r=\"C3\"><v>3</v></c></row>")
        {
            using var stream = new MemoryStream();
            using (var zip = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
            {
                void Add(string path, byte[] content)
                {
                    using var entry = zip.CreateEntry(path).Open();
                    entry.Write(content);
                }

                void Text(string path, string content) => Add(path, Encoding.UTF8.GetBytes(content));
                Text("_rels/.rels", $"<Relationships xmlns=\"{Package}\"><Relationship Id=\"rId1\" Type=\"{Relationships}/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
                Text("xl/workbook.xml", $"<workbook xmlns=\"{Spreadsheet}\" xmlns:r=\"{Relationships}\"><sheets><sheet name=\"Sheet1\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
                Text("xl/_rels/workbook.xml.rels", $"<Relationships xmlns=\"{Package}\"><Relationship Id=\"rId1\" Type=\"{Relationships}/worksheet\" Target=\"worksheets/sheet1.xml\"/></Relationships>");
                Text("xl/worksheets/sheet1.xml", $"<worksheet xmlns=\"{Spreadsheet}\" xmlns:r=\"{Relationships}\"><sheetData>{sheetData}</sheetData><drawing r:id=\"rId1\"/></worksheet>");
                Text("xl/worksheets/_rels/sheet1.xml.rels", $"<Relationships xmlns=\"{Package}\"><Relationship Id=\"rId1\" Type=\"{Relationships}/drawing\" Target=\"../drawings/drawing1.xml\"/></Relationships>");
                Text("xl/drawings/_rels/drawing1.xml.rels", $"<Relationships xmlns=\"{Package}\"><Relationship Id=\"rId1\" Type=\"{Relationships}/image\" Target=\"../media/image1.png\"/>"
                    + $"<Relationship Id=\"rId2\" Type=\"{Relationships}/chart\" Target=\"../charts/chart1.xml\"/></Relationships>");
                Add("xl/drawings/drawing1.xml", drawing);
                Add("xl/media/image1.png", Png);
                Text("xl/charts/chart1.xml", "<c:chartSpace xmlns:c=\"http://schemas.openxmlformats.org/drawingml/2006/chart\"><c:chart><c:plotArea><c:barChart><c:barDir val=\"col\"/>"
                    + "<c:ser><c:idx val=\"0\"/><c:val><c:numRef><c:numCache><c:ptCount val=\"2\"/><c:pt idx=\"0\"><c:v>1</c:v></c:pt><c:pt idx=\"1\"><c:v>3</c:v></c:pt></c:numCache></c:numRef></c:val></c:ser>"
                    + "<c:axId val=\"1\"/><c:axId val=\"2\"/></c:barChart><c:catAx><c:axId val=\"1\"/><c:crossAx val=\"2\"/></c:catAx><c:valAx><c:axId val=\"2\"/><c:crossAx val=\"1\"/></c:valAx></c:plotArea></c:chart></c:chartSpace>");
            }

            return stream.ToArray();
        }

        /// <summary>
        /// A bare BIFF8 workbook stream of one worksheet whose drawing data is <paramref name="drawing"/> (in MSODRAWING
        /// records), with a BLIP store of a PNG and a DIB, and OBJ records after it: by default as many as the
        /// input's length picks (a picture, a rectangle, a text box with its TXO, an oval and a group in turn).
        /// </summary>
        /// <param name="drawing">The drawing data.</param>
        /// <param name="objects">The OBJ types after it, or <c>null</c> for those the length picks.</param>
        /// <param name="leading">OBJ types (pictures) before the drawing data, whose shapes are not in it.</param>
        public static byte[] Xls(byte[] drawing, IReadOnlyList<int>? objects = null, IReadOnlyList<int>? leading = null)
        {
            objects ??= [.. Enumerable.Range(0, 1 + (drawing.Length % 6)).Select(i => new[] { 0x08, 0x02, 0x06, 0x03, 0x00 }[i % 5])];
            var sheet = new MemoryStream();
            Record(sheet, 0x0809, [.. U16(0x0600), .. U16(0x0010), .. new byte[12]]);
            Record(sheet, 0x0203, [.. U16(0), .. U16(0), .. U16(0), .. BitConverter.GetBytes(1.0)]);
            foreach (var type in leading ?? [])
            {
                Record(sheet, 0x005D, [.. U16(0x15), .. U16(18), .. U16(type), .. U16(99), .. U16(0x6011), .. new byte[12], .. U16(0), .. U16(0)]);
            }

            for (var at = 0; at < drawing.Length; at += 8224)
            {
                Record(sheet, 0x00EC, drawing.AsSpan(at, Math.Min(8224, drawing.Length - at)).ToArray());
            }

            for (var i = 0; i < objects.Count; i++)
            {
                // FtCmo (type, id, flags: printed), then FtEnd.
                Record(sheet, 0x005D, [.. U16(0x15), .. U16(18), .. U16(objects[i]), .. U16(i + 1), .. U16(0x6011), .. new byte[12], .. U16(0), .. U16(0)]);
                if (objects[i] == 0x06)
                {
                    Record(sheet, 0x01B6, [.. U16(0x0004 | 0x0020), .. new byte[8], .. U16(4), .. U16(16), .. new byte[4]]);
                    Record(sheet, 0x003C, [0, .. "Fuzz"u8]);
                    Record(sheet, 0x003C, [.. U16(0), .. U16(0), .. new byte[4], .. U16(4), .. U16(0), .. new byte[4]]);
                }
            }

            Record(sheet, 0x000A, []);
            var globals = new MemoryStream();
            Record(globals, 0x0809, [.. U16(0x0600), .. U16(0x0005), .. new byte[12]]);
            Record(globals, 0x00EB, DrawingGroup());
            var boundSheet = globals.Length;
            Record(globals, 0x0085, [.. new byte[4], 0, 0, 6, 0, .. "Sheet1"u8]);
            Record(globals, 0x000A, []);
            var bytes = globals.ToArray();
            BitConverter.TryWriteBytes(bytes.AsSpan((int)boundSheet + 4), (int)bytes.Length);
            return [.. bytes, .. sheet.ToArray()];
        }

        /// <summary>An OfficeArt drawing group: its BLIP store with a PNG (entry 1) and a 2 by 2 DIB (entry 2).</summary>
        public static byte[] DrawingGroup()
        {
            byte[] dib = [.. BitConverter.GetBytes(40), .. BitConverter.GetBytes(2), .. BitConverter.GetBytes(2), .. U16(1), .. U16(24), .. new byte[24],
                255, 0, 0, 0, 255, 0, 0, 0, 0, 0, 255, 255, 255, 255, 0, 0];
            byte[] Entry(int blipType, int instance, int recordType, byte[] picture)
            {
                byte[] blip = [.. Header(0, instance, recordType, 17 + picture.Length), .. new byte[16], 0xFF, .. picture];
                return [.. Header(2, blipType, 0xF007, 36 + blip.Length), (byte)blipType, (byte)blipType, .. new byte[16], .. U16(0xFF),
                    .. BitConverter.GetBytes(blip.Length), .. BitConverter.GetBytes(1), .. new byte[4], 0, 0, 0, 0, .. blip];
            }

            byte[] store = [.. Entry(6, 0x6E0, 0xF01E, Png), .. Entry(7, 0x7A8, 0xF01F, dib)];
            byte[] fdgg = [.. Header(0, 0, 0xF006, 16), .. new byte[16]];
            byte[] container = [.. fdgg, .. Header(0xF, 2, 0xF001, store.Length), .. store];
            return [.. Header(0xF, 0, 0xF000, container.Length), .. container];
        }

        /// <summary>An OfficeArt record header.</summary>
        public static byte[] Header(int version, int instance, int type, int length) =>
            [.. U16(version | (instance << 4)), .. U16(type), .. BitConverter.GetBytes(length)];

        internal static byte[] U16(int value) => [(byte)value, (byte)(value >> 8)];

        internal static void Record(MemoryStream stream, int id, byte[] data)
        {
            stream.Write(U16(id));
            stream.Write(U16(data.Length));
            stream.Write(data);
        }
    }
}

/// <summary>How a fuzzed HTML or CSS input is written.</summary>
public enum OutputMode
{
    /// <summary>A plain PDF.</summary>
    Plain,

    /// <summary>A tagged PDF.</summary>
    Tagged,

    /// <summary>PDF/UA-1 (tagged).</summary>
    PdfUA,

    /// <summary>PDF/A-2b.</summary>
    PdfA,

    /// <summary>A tagged PDF written page by page with <see cref="HtmlToPdf.ConvertToStream"/>.</summary>
    TaggedStream,
}

/// <summary>Takes what the interpreter reports and touches it, as text extraction would.</summary>
internal sealed class CountingSink : RaiCore.Pdf.Extraction.Content.ContentSink
{
    public long Characters { get; private set; }

    public override void Glyph(in RaiCore.Pdf.Extraction.Content.TextGlyph glyph) => Characters += glyph.Text.Length;

    public override void Image(in RaiCore.Pdf.Extraction.Content.ImagePlacement image) => Characters++;
}
