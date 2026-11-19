using System.Text;
using RaiCore.Pdf.Editing;
using RaiCore.Pdf.Email;
using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Fonts.OpenType;
using RaiCore.Pdf.Html;
using RaiCore.Pdf.Images;
using RaiCore.Pdf.Layout.Text;
using RaiCore.Pdf.Sheets;

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
        or SpreadsheetFormatException;

    /// <summary>HTML conversion, with no resources loaded (data: URIs only).</summary>
    public static void Html(byte[] input) => Html(input, ModeOf(input));

    /// <summary>HTML conversion in the given output mode.</summary>
    public static void Html(byte[] input, OutputMode mode) => Convert(Encoding.UTF8.GetString(input), mode);

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
        var file = RaiCore.Pdf.Email.Cfb.CompoundFile.Open(input, Limits);
        var storages = new Stack<RaiCore.Pdf.Email.Cfb.CfbEntry>([file.Root]);
        var read = 0;
        while (storages.TryPop(out var storage) && read < 64)
        {
            foreach (var entry in storage.Children)
            {
                if (entry.Kind != RaiCore.Pdf.Email.Cfb.CfbEntryKind.Stream)
                {
                    storages.Push(entry);
                }
                else if (read++ < 64)
                {
                    var data = file.Read(entry);
                    if (entry.Name.StartsWith('\u0005'))
                    {
                        _ = RaiCore.Pdf.Email.Cfb.PropertySetStream.Read(data);
                    }
                }
            }
        }
    }

    /// <summary>
    /// CSV data: decoded (encoding detected), split into records (delimiter detected), its cells classified, and laid
    /// out as tables with fitted columns; every other input (by length) on landscape pages fitted to the width, every
    /// third one tagged.
    /// </summary>
    public static void Csv(byte[] input) => CsvToPdf.ConvertToBytes(input, new CsvConverterOptions
    {
        Sheet = new SheetOptions
        {
            Limits = Limits,
            Landscape = input.Length % 2 == 0,
            FitToWidth = input.Length % 2 == 0,
            Tagged = input.Length % 3 == 0,
        },
    });

    /// <summary>
    /// An XLSX workbook: its package and parts read (shared strings, styles, theme, defined names), every visible
    /// worksheet with its values formatted, and laid out with its styles, merged cells, sizes and page setup;
    /// every other input (by length) with sheet names as headings and fitted to the width, every third one tagged, every
    /// fifth one fitted to the content.
    /// </summary>
    public static void Xlsx(byte[] input) => XlsxToPdf.ConvertToBytes(input, new XlsxConverterOptions
    {
        Sheet = new SheetOptions
        {
            Limits = Limits,
            FitToWidth = input.Length % 2 == 0,
            Tagged = input.Length % 3 == 0,
        },
        ShowSheetNames = input.Length % 2 == 0,
        AutoFit = input.Length % 5 == 0,
    });

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
