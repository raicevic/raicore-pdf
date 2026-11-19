using System.Globalization;
using System.Text;
using RaiCore.Pdf.Extraction;
using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Layout;
using RaiCore.Pdf.Sheets;
using RaiCore.Pdf.Sheets.Csv;

namespace RaiCore.Pdf.Tests.Sheets;

/// <summary>Converting CSV data to PDF: pagination with repeated headers, fitted columns, alignment, limits.</summary>
public class CsvToPdfTests
{
    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    private static string[] Pages(byte[] pdf) => [.. new TextExtractor(PdfReader.Open(pdf)).ExtractPages().Select(p => p.Text)];

    private static IReadOnlyList<TextMatch> Find(byte[] pdf, int page, string text) => new TextExtractor(PdfReader.Open(pdf)).FindOnPage(page, text);

    private static void AssertValid(byte[] pdf)
    {
        if (ExternalTools.Find("qpdf") is not null)
        {
            ExternalTools.AssertQpdfClean(pdf);
        }
    }

    /// <summary>Lays out CSV data as the converter does, and returns what the layout reports (overflow, clipping).</summary>
    private static (PdfDocument Document, List<LayoutDiagnostic> Diagnostics) Render(byte[] csv, CsvConverterOptions? options = null)
    {
        options ??= new CsvConverterOptions();
        var flow = SheetRenderer.Layout(CsvConverter.Read(csv, options), options.Sheet);
        var diagnostics = new List<LayoutDiagnostic>();
        flow.Diagnostics = diagnostics.Add;
        return (flow.Render(), diagnostics);
    }

    /// <summary>The characters of the text that are not white space, counted.</summary>
    private static Dictionary<char, int> Characters(IEnumerable<string> texts) => texts.SelectMany(t => t)
        .Where(c => !char.IsWhiteSpace(c)).GroupBy(c => c).ToDictionary(g => g.Key, g => g.Count());

    /// <summary>Asserts that every character of the cells is on the pages (none clipped or lost).</summary>
    private static void AssertAllTextShown(IEnumerable<string> cells, byte[] pdf)
    {
        var shown = Characters(Pages(pdf));
        foreach (var (c, count) in Characters(cells))
        {
            Assert.True(shown.GetValueOrDefault(c) >= count, $"'{c}' is shown {shown.GetValueOrDefault(c)} times, not {count}.");
        }
    }

    private static string LongCsv(int rows)
    {
        var builder = new StringBuilder("Number,HeaderMarker,Amount,Text\n");
        for (var i = 1; i <= rows; i++)
        {
            builder.Append(CultureInfo.InvariantCulture, $"{i},row{i},{i * 1.25:F2},\"Some text, with a comma, in row {i}\"\n");
        }

        return builder.ToString();
    }

    [Fact]
    public void Long_data_paginates_with_the_header_row_on_every_page()
    {
        var pdf = CsvToPdf.ConvertToBytes(Utf8(LongCsv(400)));
        var pages = Pages(pdf);
        Assert.True(pages.Length >= 5, $"{pages.Length} pages");

        // The header is at the same height at the top of every page, above the first row of the page.
        var headerTop = Find(pdf, 0, "HeaderMarker").Single().Bounds.Top;
        for (var i = 0; i < pages.Length; i++)
        {
            var header = Find(pdf, i, "HeaderMarker").Single().Bounds;
            Assert.Equal(headerTop, header.Top, 0.01);
            Assert.All(new TextExtractor(PdfReader.Open(pdf)).FindOnPage(i, new System.Text.RegularExpressions.Regex(@"row\d+")),
                m => Assert.True(m.Bounds.Top < header.Y));
        }

        // Every row once, in order.
        var rows = string.Join("\n", pages).Split('\n').Select(l => System.Text.RegularExpressions.Regex.Match(l, @"\brow(\d+)\b"))
            .Where(m => m.Success).Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToList();
        Assert.Equal(Enumerable.Range(1, 400), rows);
        AssertValid(pdf);
    }

    [Fact]
    public void Without_a_header_row_nothing_repeats()
    {
        var pdf = CsvToPdf.ConvertToBytes(Utf8(LongCsv(200)), new CsvConverterOptions { HasHeaderRow = false });
        var pages = Pages(pdf);
        Assert.Contains("HeaderMarker", pages[0], StringComparison.Ordinal);
        Assert.All(pages.Skip(1), p => Assert.DoesNotContain("HeaderMarker", p, StringComparison.Ordinal));
    }

    [Fact]
    public void Numbers_and_dates_are_right_aligned_and_text_left_aligned()
    {
        var pdf = CsvToPdf.ConvertToBytes(Utf8("Item,Price,Date\nA long item name,1.5,2026-11-19\nB,1234567.25,1/2/26\nC,-3,8:30\n"));
        double Right(string text) => Find(pdf, 0, text).Single().Bounds.Right;
        double Left(string text) => Find(pdf, 0, text).Single().Bounds.X;

        Assert.Equal(Right("1.5"), Right("1234567.25"), 0.5);
        Assert.Equal(Right("1.5"), Right("-3"), 0.5);
        Assert.Equal(Right("2026-11-19"), Right("8:30"), 0.5);
        Assert.Equal(Left("A long item name"), Left("B"), 0.5);
        Assert.True(Left("-3") > Left("1234567.25") + 10);

        // Headers of numeric columns are right-aligned too; text headers are not.
        Assert.Equal(Right("1.5"), Right("Price"), 0.5);
        Assert.Equal(Left("Item"), Left("B"), 0.5);
    }

    [Fact]
    public void European_data_with_semicolons_and_decimal_commas()
    {
        var csv = Encoding.Latin1.GetBytes("Artikel;Menge;Preis\nM\u00FCsli;7;1.234,56\nK\u00E4se;10;0,5\n");
        var pdf = CsvToPdf.ConvertToBytes(csv);
        Assert.Contains("M\u00FCsli", Pages(pdf)[0], StringComparison.Ordinal);
        double Right(string text) => Find(pdf, 0, text).Single().Bounds.Right;
        Assert.Equal(Right("1.234,56"), Right("0,5"), 0.5);
        Assert.Equal(Right("7"), Right("10"), 0.5);

        // With a decimal point, "1.234,56" and "0,5" are no numbers: they are left-aligned like text.
        var point = CsvToPdf.ConvertToBytes(csv, new CsvConverterOptions { DecimalSeparator = '.' });
        Assert.Equal(Find(point, 0, "1.234,56").Single().Bounds.X, Find(point, 0, "0,5").Single().Bounds.X, 0.5);
        Assert.True(Find(point, 0, "1.234,56").Single().Bounds.Right > Find(point, 0, "0,5").Single().Bounds.Right + 5);
    }

    [Fact]
    public void Options_name_the_delimiter_and_the_encoding()
    {
        var csv = Encoding.Latin1.GetBytes("a;b|c\n\u00E91;2|3\n");
        var pages = Pages(CsvToPdf.ConvertToBytes(csv, new CsvConverterOptions { Delimiter = '|', Encoding = Encoding.Latin1 }));
        Assert.Contains("a;b", pages[0], StringComparison.Ordinal);
        Assert.Contains("\u00E91;2", pages[0], StringComparison.Ordinal);

        var sep = Pages(CsvToPdf.ConvertToBytes(Utf8("sep=|\nx,y|z\n")))[0];
        Assert.DoesNotContain("sep", sep, StringComparison.Ordinal);
        Assert.Contains("x,y", sep, StringComparison.Ordinal);

        var utf16 = Pages(CsvToPdf.ConvertToBytes([.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes("name\tvalue\n\u00C5se\t1\n")]))[0];
        Assert.Contains("\u00C5se", utf16, StringComparison.Ordinal);

        Assert.Throws<ArgumentException>(() => CsvToPdf.Convert(Utf8("a"), new CsvConverterOptions { Delimiter = '"' }));
        Assert.Throws<ArgumentException>(() => CsvToPdf.Convert(Utf8("a"), new CsvConverterOptions { Delimiter = '\n' }));
    }

    /// <summary>A wide table: many columns, long text, long unbreakable words, a line break in a cell.</summary>
    private static string WideCsv(int columns, int rows)
    {
        var builder = new StringBuilder();
        builder.AppendJoin(',', Enumerable.Range(1, columns).Select(c => $"Column{c:D2}")).Append('\n');
        for (var r = 0; r < rows; r++)
        {
            builder.AppendJoin(',', Enumerable.Range(1, columns).Select(c => (c % 4) switch
            {
                0 => (r * 1000.5 + c).ToString("N2", CultureInfo.InvariantCulture).Replace(",", string.Empty, StringComparison.Ordinal),
                1 => $"\"Words that wrap in a narrow column {r} {c} and go on for a while\"",
                2 => $"https://example.com/a/very/long/path/without/spaces/{r}/{c}/index.html",
                _ => $"\"Line one\nline two {r}\"",
            })).Append('\n');
        }

        return builder.ToString();
    }

    [Theory]
    [InlineData(6, false, false)]
    [InlineData(12, false, false)]
    [InlineData(12, true, false)]
    [InlineData(30, false, false)]
    [InlineData(30, true, true)]
    public void Wide_data_is_never_clipped(int columns, bool landscape, bool fit)
    {
        var csv = Utf8(WideCsv(columns, 25));
        var options = new CsvConverterOptions { Sheet = new SheetOptions { Landscape = landscape, FitToWidth = fit } };
        var (document, diagnostics) = Render(csv, options);
        Assert.Empty(diagnostics);
        var pdf = document.ToArray();
        AssertAllTextShown(CsvParser.Parse(Encoding.UTF8.GetString(csv), ',').SelectMany(r => r), pdf);
        Assert.Equal(Pages(pdf), Pages(CsvToPdf.ConvertToBytes(csv, options)));
        AssertValid(pdf);
    }

    [Fact]
    public void Columns_that_cannot_all_fit_are_printed_in_bands()
    {
        var csv = Utf8(WideCsv(30, 3));
        var pdf = CsvToPdf.ConvertToBytes(csv);
        var pages = Pages(pdf);
        Assert.True(pages.Length >= 3, $"{pages.Length} pages");

        // Each page holds a band of consecutive columns, all rows, with the band's header; together they hold all columns once.
        var bands = pages.Select(p => System.Text.RegularExpressions.Regex.Matches(p, @"Column(\d\d)").Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToList()).ToList();
        Assert.Equal(Enumerable.Range(1, 30), bands.SelectMany(b => b));
        Assert.All(bands, b => Assert.Equal(Enumerable.Range(b[0], b.Count), b));
        Assert.All(pages, p => Assert.Contains("2/", p, StringComparison.Ordinal));
    }

    [Fact]
    public void Landscape_turns_the_page_and_fit_to_width_shrinks_the_text()
    {
        var csv = Utf8(WideCsv(12, 3));
        var portrait = CsvToPdf.Convert(csv);
        var landscape = CsvToPdf.Convert(csv, new CsvConverterOptions { Sheet = new SheetOptions { Landscape = true } });
        Assert.True(portrait.Pages[0].Width < portrait.Pages[0].Height);
        Assert.Equal(portrait.Pages[0].Height, landscape.Pages[0].Width, 0.01);

        // A landscape page given stays landscape.
        var given = new SheetOptions { Page = new PageSetup(PdfPageSize.A4.Landscape, Thickness.All(20)), Landscape = true };
        Assert.True(CsvToPdf.Convert(csv, new CsvConverterOptions { Sheet = given }).Pages[0].Width > PdfPageSize.A4.Width);

        // Fitted, all twelve columns are on the first page; at the full size they are not.
        var fitted = CsvToPdf.ConvertToBytes(csv, new CsvConverterOptions { Sheet = new SheetOptions { FitToWidth = true, MinimumFontSize = 2 } });
        Assert.Contains("Column12", Pages(fitted)[0], StringComparison.Ordinal);
        Assert.DoesNotContain("Column12", Pages(portrait.ToArray())[0], StringComparison.Ordinal);
        var small = Find(fitted, 0, "Column01").Single().Bounds.Height;
        var full = Find(portrait.ToArray(), 0, "Column01").Single().Bounds.Height;
        Assert.True(small < full * 0.8, $"{small} vs {full}");

        // Data that fits is not enlarged, and the minimum size bounds the shrinking.
        var narrow = Utf8("a,b\n1,2\n");
        Assert.Equal(
            Find(CsvToPdf.ConvertToBytes(narrow), 0, "a").Single().Bounds.Height,
            Find(CsvToPdf.ConvertToBytes(narrow, new CsvConverterOptions { Sheet = new SheetOptions { FitToWidth = true } }), 0, "a").Single().Bounds.Height, 0.01);
        var bounded = CsvToPdf.ConvertToBytes(csv, new CsvConverterOptions { Sheet = new SheetOptions { FitToWidth = true, MinimumFontSize = 8 } });
        Assert.True(Find(bounded, 0, "Column01").Single().Bounds.Height > full * 0.85);
    }

    [Fact]
    public void Malformed_data_is_read_as_spreadsheets_read_it()
    {
        var csv = Utf8("a,b\n1,\"unclosed\n2,3,4,5\n\"x\"\"y\",\"multi\nline\"\n\"tail");
        var (document, diagnostics) = Render(csv);
        Assert.Empty(diagnostics);
        var text = Pages(document.ToArray())[0];
        foreach (var word in new[] { "\"unclosed", "x\"y", "multi", "line", "\"tail", "5" })
        {
            Assert.Contains(word, text, StringComparison.Ordinal);
        }

        // Control characters are left out, tabs are spaces, and a lone CR in a quoted field breaks the line.
        var controls = Pages(CsvToPdf.ConvertToBytes(Utf8("a\u0001b,c\td,\"e\rf\"\n")))[0];
        Assert.Contains("ab", controls, StringComparison.Ordinal);
        Assert.Contains("c d", controls, StringComparison.Ordinal);
        Assert.DoesNotContain("ef", controls, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n\n")]
    [InlineData(",,,\n")]
    [InlineData("\uFEFF")]
    public void Empty_data_gives_a_page(string csv)
    {
        var pdf = CsvToPdf.ConvertToBytes(Utf8(csv));
        Assert.Single(PdfReader.Open(pdf).Pages);
        AssertValid(pdf);
    }

    [Fact]
    public void Display_text_keeps_clean_text_as_it_is()
    {
        var clean = "plain text\nwith a line feed";
        Assert.Same(clean, SheetRenderer.DisplayText(clean));
        Assert.Equal("a\nb\nc d", SheetRenderer.DisplayText("a\r\nb\rc\td\u0007"));
    }

    [Fact]
    public void Limits_bound_the_cells_the_pages_and_the_time()
    {
        var csv = Utf8(LongCsv(400));
        ResourceLimitExceededException Limited(ResourceLimits limits) => Assert.Throws<ResourceLimitExceededException>(
            () => CsvToPdf.Convert(csv, new CsvConverterOptions { Sheet = new SheetOptions { Limits = limits } }));

        Assert.Equal(nameof(ResourceLimits.MaxTableCells), Limited(ResourceLimits.Default with { MaxTableCells = 1000 }).LimitName);
        Assert.Equal(nameof(ResourceLimits.MaxPages), Limited(ResourceLimits.Default with { MaxPages = 2 }).LimitName);
        Assert.Equal(nameof(ResourceLimits.Timeout), Limited(ResourceLimits.Default with { Timeout = TimeSpan.FromTicks(1) }).LimitName);
        Assert.Throws<ArgumentException>(() => CsvToPdf.Convert(csv, new CsvConverterOptions { Sheet = new SheetOptions { Limits = null! } }));
    }

    [Fact]
    public void The_reading_checkpoint_stops_on_time_and_cancellation()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        CsvConverter.Checkpoint(ResourceLimits.Default with { Timeout = Timeout.InfiniteTimeSpan }, clock, CancellationToken.None)();
        CsvConverter.Checkpoint(ResourceLimits.Default, clock, CancellationToken.None)();
        Assert.Throws<ResourceLimitExceededException>(() =>
            CsvConverter.Checkpoint(ResourceLimits.Default with { Timeout = TimeSpan.FromTicks(1) }, clock, CancellationToken.None)());
        Assert.Throws<OperationCanceledException>(() =>
            CsvConverter.Checkpoint(ResourceLimits.Default, clock, new CancellationToken(canceled: true))());

        // Long data is checked as it is read (and measured), not only when it is laid out.
        var calls = 0;
        CsvConverter.Read(Utf8(LongCsv(3000)), new CsvConverterOptions(), checkpoint: () => calls++);
        Assert.True(calls >= 4, $"{calls} calls");
    }

    [Fact]
    public async Task Cancellation_stops_the_conversion()
    {
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CsvToPdf.ConvertAsync(Utf8(LongCsv(10)), cancellationToken: canceled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CsvToPdf.ConvertAsync(new MemoryStream(Utf8(LongCsv(10))), cancellationToken: canceled.Token));
    }

    [Fact]
    public async Task Paths_streams_and_bytes_give_the_same_document()
    {
        var csv = Utf8(LongCsv(30));
        var path = Path.Combine(Path.GetTempPath(), $"raicore-{Guid.NewGuid():N}.csv");
        await File.WriteAllBytesAsync(path, csv, TestContext.Current.CancellationToken);
        try
        {
            var expected = Pages(CsvToPdf.ConvertToBytes(csv));
            Assert.Equal(expected, Pages(CsvToPdf.Convert(path).ToArray()));
            Assert.Equal(expected, Pages(CsvToPdf.Convert(new MemoryStream(csv)).ToArray()));
            Assert.Equal(expected, Pages(CsvToPdf.ConvertToBytes(new MemoryStream(csv))));
            Assert.Equal(expected, Pages((await CsvToPdf.ConvertAsync(csv, cancellationToken: TestContext.Current.CancellationToken)).ToArray()));
            Assert.Equal(expected, Pages((await CsvToPdf.ConvertAsync(new MemoryStream(csv), cancellationToken: TestContext.Current.CancellationToken)).ToArray()));
        }
        finally
        {
            File.Delete(path);
        }

        Assert.Throws<ArgumentNullException>(() => CsvToPdf.Convert((byte[])null!));
        Assert.Throws<ArgumentNullException>(() => CsvToPdf.Convert((Stream)null!));
        Assert.Throws<ArgumentNullException>(() => CsvToPdf.Convert((string)null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => CsvToPdf.ConvertAsync((byte[])null!, cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentNullException>(() => CsvToPdf.ConvertAsync((Stream)null!, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Metadata_and_tagging()
    {
        var document = CsvToPdf.Convert(Utf8("Name,Value\nx,1\n"), new CsvConverterOptions { Sheet = new SheetOptions { Title = "Values", Tagged = true } });
        Assert.Equal("Values", document.Info.Title);
        Assert.Equal("RaiCore.Pdf.Sheets", document.Info.Creator);
        Assert.True(document.Tagged);
        var text = Encoding.Latin1.GetString(document.ToArray());
        Assert.Contains("/S /Table", text, StringComparison.Ordinal);
        Assert.Contains("/S /TH", text, StringComparison.Ordinal);
        Assert.Contains("/S /TD", text, StringComparison.Ordinal);

        Assert.False(CsvToPdf.Convert(Utf8("a\n")).Tagged);
        Assert.Null(CsvToPdf.Convert(Utf8("a\n")).Info.Title);
    }

    [Fact]
    public void Fonts_and_fallbacks()
    {
        // Hebrew is in the bundled Liberation Sans; Japanese comes from a fallback font, the header from a bold face.
        var fonts = BundledFonts.CreateCollection().AddFile(Fixtures.Path("NotoSansJP-Subset.otf"));
        var options = new CsvConverterOptions
        {
            Sheet = new SheetOptions { Fonts = fonts, FontFamily = "serif", FallbackFontFamilies = ["Noto Sans JP"] },
        };
        var document = CsvToPdf.Convert(Utf8("Name,Text\nx,\u05E9\u05DC\u05D5\u05DD \u65E5\u672C\u8A9E\n"), options);
        var pdf = document.ToArray();
        var text = Pages(pdf)[0];
        Assert.Contains("\u65E5\u672C\u8A9E", text, StringComparison.Ordinal);
        Assert.Contains("\u05E9\u05DC\u05D5\u05DD", text, StringComparison.Ordinal);
        var raw = Encoding.Latin1.GetString(pdf);
        Assert.Contains("NotoSansJP", raw, StringComparison.Ordinal);
        Assert.Contains("LiberationSerif-Bold", raw, StringComparison.Ordinal);
        Assert.Contains("LiberationSerif", raw.Replace("LiberationSerif-Bold", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);

        // A family the collection does not have falls back to Liberation Sans.
        var unknown = Encoding.Latin1.GetString(CsvToPdf.ConvertToBytes(Utf8("a\nb\n"), new CsvConverterOptions { Sheet = new SheetOptions { Fonts = new FontCollection(), FontFamily = "Nothing" } }));
        Assert.Contains("LiberationSans", unknown, StringComparison.Ordinal);
    }

    [Fact]
    public void Gridlines_can_be_turned_off()
    {
        var csv = Utf8("a,b\n1,2\n");
        var with = PdfReader.Open(CsvToPdf.ConvertToBytes(csv)).Pages[0].GetContent();
        var without = PdfReader.Open(CsvToPdf.ConvertToBytes(csv, new CsvConverterOptions { Sheet = new SheetOptions { Gridlines = false } })).Pages[0].GetContent();
        Assert.True(without.Length < with.Length);
    }

    [Theory]
    [InlineData(0, 6)]
    [InlineData(-1, 6)]
    [InlineData(double.NaN, 6)]
    [InlineData(9, 0)]
    [InlineData(9, double.PositiveInfinity)]
    public void Font_sizes_must_be_positive(double size, double minimum) => Assert.Throws<ArgumentException>(() =>
        CsvToPdf.Convert(Utf8("a"), new CsvConverterOptions { Sheet = new SheetOptions { FontSize = size, MinimumFontSize = minimum } }));

    [Fact]
    public void Text_cells_follow_their_direction_and_empty_rows_keep_their_height()
    {
        var pdf = CsvToPdf.ConvertToBytes(Utf8("Text,Other\n\u05E9\u05DC\u05D5\u05DD,a\nabc,x\n,\nend,y\na much longer cell of text,z\n"));
        var hebrew = Find(pdf, 0, "\u05E9\u05DC\u05D5\u05DD").Single().Bounds;
        var latin = Find(pdf, 0, "abc").Single().Bounds;
        var end = Find(pdf, 0, "end").Single().Bounds;
        Assert.True(hebrew.X > latin.X + 1, "Hebrew starts on the right.");
        Assert.True(latin.Y - end.Y > (hebrew.Y - latin.Y) * 1.5, "The empty row has a height.");
    }
}
