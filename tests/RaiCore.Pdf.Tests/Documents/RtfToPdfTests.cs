using System.Text;
using System.Text.Json;
using RaiCore.Pdf.Documents;
using RaiCore.Pdf.Extraction;
using static RaiCore.Pdf.Tests.WordDocuments.Rtf;

namespace RaiCore.Pdf.Tests.WordDocuments;

/// <summary>
/// Converting RTF to PDF: the hand-made fixtures and Apache Tika's samples against an independent reader (striprtf),
/// layout of paragraphs, lists, tabs, tables, sections, headers and footers, fields, notes and pictures, options,
/// malformed and hostile input.
/// </summary>
public class RtfToPdfTests
{
    private static string ExpectedPath => Fixtures.Path(Path.Combine("Documents", "Rtf", "expected.json"));

    private static byte[] Fixture(string name) => Fixtures.Read(Path.Combine("Documents", "Rtf", name));

    private static byte[] Tika(string name) => Fixtures.Read(Path.Combine("Tika", name));

    private static IReadOnlyList<TextMatch> Find(byte[] pdf, int page, string text) => new TextExtractor(PdfReader.Open(pdf)).FindOnPage(page, text);

    private static string[] Pages(byte[] pdf) => Docx.Pages(pdf);

    private static string AllText(byte[] pdf) => Docx.AllText(pdf);

    private static void AssertValid(byte[] pdf)
    {
        if (ExternalTools.Find("qpdf") is not null)
        {
            ExternalTools.AssertQpdfClean(pdf);
        }
    }

    public static TheoryData<string> FixtureNames()
    {
        var data = new TheoryData<string>();
        using var expected = JsonDocument.Parse(File.ReadAllText(ExpectedPath));
        foreach (var property in expected.RootElement.EnumerateObject())
        {
            data.Add(property.Name);
        }

        return data;
    }

    /// <summary>
    /// Every paragraph striprtf finds in the body is in the PDF's text, word by word in order (ignoring case and
    /// punctuation, and characters the bundled fonts do not have, such as CJK); the cells of a table row may be in any
    /// order. Headers, footers and notes are somewhere in the PDF. Hand-made fixtures have nothing clipped or too wide.
    /// </summary>
    [Theory]
    [MemberData(nameof(FixtureNames))]
    public void Fixtures_show_every_paragraph_in_order(string name)
    {
        using var expected = JsonDocument.Parse(File.ReadAllText(ExpectedPath));
        var (pdf, diagnostics) = Convert(Fixtures.Read(name));
        var data = pdf.ToArray();
        var text = Words(AllText(data));
        var position = 0;
        var entry = expected.RootElement.GetProperty(name);
        foreach (var item in entry.GetProperty("text").EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                position = FindWords(name, text, item.GetString()!, position);
                continue;
            }

            var end = position;
            foreach (var cell in item.EnumerateArray())
            {
                end = Math.Max(end, FindWords(name, text, cell.GetString()!, position));
            }

            position = end;
        }

        foreach (var item in entry.GetProperty("anywhere").EnumerateArray())
        {
            FindWords(name, text, item.GetString()!, 0, skipNumbers: true);
        }

        if (name.StartsWith("Documents/", StringComparison.Ordinal))
        {
            Assert.DoesNotContain(diagnostics, d => d.Kind == DocumentDiagnosticKind.Layout);
        }

        AssertValid(data);
    }

    /// <summary>Finds a paragraph's words in order from a position, returning the position after its last word.</summary>
    private static int FindWords(string name, string text, string paragraph, int position, bool skipNumbers = false)
    {
        foreach (var raw in paragraph.Split([' ', '\t', '\n', '\u2009', '\u3000'], StringSplitOptions.RemoveEmptyEntries))
        {
            var word = Words(raw);
            if (word.Length == 0 || (word.All(char.IsAsciiDigit) && (skipNumbers || paragraph.Contains('\t', StringComparison.Ordinal))))
            {
                continue; // Punctuation alone, or a cached page number (the PDF has its own).
            }

            var found = text.IndexOf(word, position, StringComparison.OrdinalIgnoreCase);
            Assert.True(found >= 0, $"{name}: '{raw}' of \"{paragraph}\" is missing after position {position}.");
            position = found + word.Length;
        }

        return position;
    }

    /// <summary>Letters and digits only, of the scripts the bundled fonts have (Latin, Greek, Cyrillic).</summary>
    private static string Words(string text) => new([.. text.Where(c => char.IsLetterOrDigit(c) && c < '\u0590')]);

    [Fact]
    public void Formatting_fixture_draws_its_text_as_formatted()
    {
        var (pdf, diagnostics) = Convert(Fixture("formatting.rtf"));
        var data = pdf.ToArray();
        var text = AllText(data);
        Assert.DoesNotContain("invisible", text, StringComparison.Ordinal);
        Assert.DoesNotContain("deleted words", text, StringComparison.Ordinal);
        Assert.Contains("SMALL CAPS", text, StringComparison.Ordinal);
        Assert.Contains("ALL CAPS", text, StringComparison.Ordinal);
        Assert.Contains("caf\u00E9 \u041F\u0440\u0438\u0432\u0435\u0442 \u017De\u0161", text, StringComparison.Ordinal);
        Assert.Contains("caf\u00E9 na\u00EFve \u20AC euro", text, StringComparison.Ordinal);
        Assert.Contains("{braces} back\\slash", text, StringComparison.Ordinal);
        Assert.Contains("New page before this paragraph.", Pages(data)[1], StringComparison.Ordinal);
        Assert.Contains("Last paragraph without a mark.", Pages(data)[1], StringComparison.Ordinal);
        Assert.Equal("Formatting fixture", pdf.Info.Title);
        Assert.Equal("Test", pdf.Info.Author);
        Assert.Equal(306, Center(Find(data, 0, "Centered paragraph.").Single().Bounds), 0);
        Assert.Equal(612 - 90, Find(data, 0, "Right-aligned paragraph.").Single().Bounds.Right, 0);
        Assert.Equal(90 + 36, Find(data, 0, "Indented paragraph").Single().Bounds.X, 0); // The hanging indent: 72 - 36 points.
        Assert.Equal(90 + 144, Find(data, 0, "Value").Single().Bounds.X, 0);
        Assert.Equal(90 + 432, Find(data, 0, "Right").Last().Bounds.Right, 0); // A right tab at 6 inches.
        Assert.Contains("....", text, StringComparison.Ordinal);
        Assert.Empty(diagnostics);
        AssertValid(data);

        static double Center(PdfRectangle r) => r.X + (r.Width / 2);
    }

    [Fact]
    public void The_space_between_paragraphs_and_line_spacing()
    {
        var pdf = RtfToPdf.ConvertToBytes(Document(@"\pard\sa400 First\par\pard\sb600 Second\par\pard Third\par\pard\sb200\sa200 Fourth\par\pard Fifth\par"
            + @"\pard\sl-720 Exact\line Again\par"));
        double Top(string text) => Find(pdf, 0, text).Single().Bounds.Top;
        var line = Top("Second") - Top("Third");
        Assert.Equal(50, Top("First") - Top("Second") - line, 0); // 20 + 30: word processors add them.
        Assert.Equal(10, Top("Third") - Top("Fourth") - line, 0);
        Assert.Equal(10, Top("Fourth") - Top("Fifth") - line, 0);
        Assert.Equal(36, Top("Exact") - Top("Again"), 0);
    }

    [Fact]
    public void List_labels_hang_in_the_indent()
    {
        var data = RtfToPdf.ConvertToBytes(Fixture("lists.rtf"));
        var text = AllText(data);
        foreach (var label in new[] { "1. First item", "2. Second item", "(i) Nested item", "A. Lettered item", "3. Back to the top level", "e) Letter from five",
            "4. Same list continues", "1. Old first", "III) Computed roman three", "IV) Computed roman four", "9) Label from the list text", "\u2022 Bullet one" })
        {
            Assert.Contains(label, text, StringComparison.Ordinal);
        }

        Assert.Equal(90 + 18, Find(data, 0, "1.").First().Bounds.X, 0);
        Assert.Equal(90 + 36, Find(data, 0, "First item").Single().Bounds.X, 0); // The label's tab goes to the indent.
        var nested = Find(data, 0, "(i)").First().Bounds;
        Assert.InRange(Find(data, 0, "Nested item").Single().Bounds.X - nested.Right, 1, 5); // A space after it.
    }

    [Fact]
    public void Tables_repeat_header_rows_span_cells_and_nest()
    {
        var data = RtfToPdf.ConvertToBytes(Fixture("tables.rtf"));
        var pages = Pages(data);
        Assert.True(pages.Length >= 2);
        Assert.Single(Find(data, 1, "Description")); // The header row repeats.
        var page = pages.Length - 1;
        var across = Find(data, page, "Merged across").Single().Bounds;
        var c = Find(data, page, "C").First(m => Math.Abs(m.Bounds.Y - across.Y) < 1).Bounds;
        Assert.True(c.X > across.X + 150); // The merged cell spans two of the 100-point columns.
        var down = Find(data, page, "Merged down").Single().Bounds;
        Assert.True(Find(data, page, "Bottom").Single().Bounds.X > down.Right); // Its column is taken in the row below.
        var outer = Find(data, page, "Outer A").Single().Bounds;
        var inner = Find(data, page, "Inner 2").Single().Bounds;
        Assert.True(inner.Y < outer.Y && inner.Right < Find(data, page, "Outer B").Single().Bounds.X); // Inside the first cell.
        Assert.True(Find(data, page, "After inner").Single().Bounds.Y < inner.Y);
        AssertValid(data);
    }

    [Fact]
    public void Sections_have_their_own_page_size_headers_footers_and_numbers()
    {
        var (pdf, diagnostics) = Convert(Fixture("sections.rtf"));
        var data = pdf.ToArray();
        var pages = Pages(data);
        Assert.Equal(5, pages.Length);
        Assert.Contains("First page header", pages[0], StringComparison.Ordinal);
        Assert.DoesNotContain("Page 1 of", pages[0], StringComparison.Ordinal); // The title page has no footer of its own.
        Assert.Contains("Even page header", pages[1], StringComparison.Ordinal);
        Assert.Contains("Even page 2", pages[1], StringComparison.Ordinal);
        Assert.Contains("Odd page header", pages[2], StringComparison.Ordinal);
        Assert.Contains("Page 3 of 5", pages[2], StringComparison.Ordinal);
        Assert.Contains("Landscape header", pages[3], StringComparison.Ordinal); // \header is for even pages too.
        Assert.Contains("Landscape page 4", pages[3], StringComparison.Ordinal); // \chpgn.
        Assert.True(pdf.Pages[3].Width > pdf.Pages[3].Height);
        Assert.True(pdf.Pages[4].Width < pdf.Pages[4].Height);
        Assert.Contains("Section page i of 1", pages[4], StringComparison.Ordinal); // Restarted in roman numerals.
        Assert.Contains("A continuous section on the same page.", pages[4], StringComparison.Ordinal);
        Assert.DoesNotContain(diagnostics, d => d.Message.Contains("text columns", StringComparison.Ordinal)); // Laid out in columns.
    }

    [Fact]
    public void Page_references_links_notes_and_pictures()
    {
        var (pdf, diagnostics) = Convert(Fixture("fields.rtf"));
        var data = pdf.ToArray();
        var pages = Pages(data);
        var first = new string([.. pages[0].Where(c => !char.IsWhiteSpace(c))]);
        Assert.Matches(@"Introduction\.+1", first);
        Assert.Matches(@"Details\.+2", first);
        Assert.True(pdf.NamedDestinations.ContainsKey("intro"));
        Assert.True(pdf.NamedDestinations.ContainsKey("details"));
        Assert.Contains(pdf.Pages[0].Links, l => l.DestinationName == "details");
        Assert.Contains(pdf.Pages[0].Links, l => l.Uri == new Uri("https://example.com/docs"));
        var all = new string([.. AllText(data).Where(c => !char.IsWhiteSpace(c))]);
        Assert.Contains("footnote1andanendnotei.", all, StringComparison.Ordinal);
        Assert.True(all.IndexOf("1Thefootnotetext.", StringComparison.Ordinal) < all.IndexOf("iTheendnotetext.", StringComparison.Ordinal));
        Assert.Contains("2026-01-02", all, StringComparison.Ordinal);
        Assert.Contains("Objectresulttext", all, StringComparison.Ordinal);
        Assert.Contains("Textinatextbox", all, StringComparison.Ordinal);
        Assert.DoesNotContain("Fallbacktext", all, StringComparison.Ordinal);
        Assert.DoesNotContain("Commenttext", all, StringComparison.Ordinal);
        var images = new ImageExtractor(PdfReader.Open(data)).ExtractPage(0).Images;
        Assert.Equal(4, images.Count); // PNG, JPEG, \bin PNG and the shape picture (the damaged metafile is left out).
        Assert.Contains(images, i => Math.Abs(i.Width - 80) < 0.5 && Math.Abs(i.Height - 40) < 0.5); // The goal size.
        Assert.Contains(images, i => Math.Abs(i.Width - 30) < 0.5); // 60 points scaled to 50%.
        Assert.Contains(diagnostics, d => d.Kind == DocumentDiagnosticKind.ImageNotLoaded && d.Message.Contains("damaged", StringComparison.Ordinal));
        Assert.Contains(pdf.Outline, o => o.Title == "Details");
        AssertValid(data);
    }

    [Fact]
    public void Headings_become_bookmarks_and_tagged_headings()
    {
        var rtf = Document(@"\pard\s1 Chapter\par\pard\s2 Section\par\pard Body\par\pard\s1 Next chapter\par", @"{\stylesheet{Normal;}{\s1 heading 1;}{\s2 heading 2;}}");
        var pdf = RtfToPdf.Convert(rtf, new RtfConverterOptions { Tagged = true, Title = "Mine" });
        Assert.Equal(["Chapter", "Next chapter"], pdf.Outline.Select(o => o.Title));
        Assert.Equal("Section", Assert.Single(pdf.Outline[0].Children).Title);
        Assert.Equal("Mine", pdf.Info.Title);
        Assert.Equal("RaiCore.Pdf.Documents", pdf.Info.Creator);
        Assert.Contains(Flatten(pdf.StructureRoot), e => e.Type == "H2");
        Assert.Empty(RtfToPdf.Convert(rtf, new RtfConverterOptions { HeadingBookmarks = false }).Outline);

        static IEnumerable<PdfStructureElement> Flatten(PdfStructureElement element) => element.Children.SelectMany(Flatten).Prepend(element);
    }

    [Fact]
    public void Tika_samples_render_their_headers_footers_notes_tables_and_pictures()
    {
        var various = Pages(RtfToPdf.ConvertToBytes(Tika("testRTFVarious.rtf")));
        Assert.Equal(2, various.Length);
        Assert.All(various, p => Assert.Contains("This is the header text.", p, StringComparison.Ordinal));
        Assert.All(various, p => Assert.Contains("This is the footer text.", p, StringComparison.Ordinal));
        Assert.Contains("1 This is a footnote.", string.Join("\n", various), StringComparison.Ordinal);
        Assert.Contains("1) Number bullet 1", various[0], StringComparison.Ordinal);
        Assert.Contains("\u2022 Bullet 1", various[0], StringComparison.Ordinal);
        var images = new ImageExtractor(PdfReader.Open(RtfToPdf.ConvertToBytes(Tika("testRTFRegularImages.rtf")))).ExtractPage(0).Images;
        Assert.Equal(2, images.Count);
        var cells = RtfToPdf.ConvertToBytes(Tika("testRTFTableCellSeparation.rtf"));
        Assert.True(Find(cells, 0, "b").Single().Bounds.X > Find(cells, 0, "a").Single().Bounds.Right + 50);
        Assert.Contains("\u00E4", AllText(cells), StringComparison.Ordinal);
        var czech = AllText(RtfToPdf.ConvertToBytes(Tika("testRTFWord2010CzechCharacters.rtf")));
        Assert.Contains("\u010Cl\u00E1nek t\u00FDdne", czech, StringComparison.Ordinal);
        var lists = AllText(RtfToPdf.ConvertToBytes(Tika("testRTFListMicrosoftWord.rtf")));
        Assert.Contains("1. a", lists, StringComparison.Ordinal);
        Assert.Contains("3. c", lists, StringComparison.Ordinal);
    }

    [Fact]
    public void Double_byte_code_pages_and_unicode_are_decoded()
    {
        var document = Read(Tika("testRTF-ms932.rtf"));
        Assert.Equal(["Hello", "\u3053\u3093\u306B\u3061\u306F", "Test", "\u30C6\u30B9\u30C8"], Paragraphs(document).Select(Text).Where(t => t.Length > 0));
        var japanese = Paragraphs(Read(Tika("testRTFJapanese.rtf"))).Select(Text).ToList();
        Assert.Contains("\u30BE\u30EB\u30B2\u3068\u5C3E\u5D0E\u3001\u6DE1\u3005\u3068\u6700\u671F", japanese.Select(t => t.Trim()));
        var fonts = new global::RaiCore.Pdf.Fonts.FontCollection().AddFile(Fixtures.Path("NotoSansJP-Subset.otf"));
        var pdf = RtfToPdf.ConvertToBytes(Encoding.Latin1.GetBytes(@"{\rtf1\ansi{\fonttbl{\f0 Arial;}{\f1\fcharset128 MS Gothic;}}{\f1 \'93\'fa\'96\'7b}}"),
            new RtfConverterOptions { Fonts = fonts, FallbackFontFamilies = ["Noto Sans JP"] });
        Assert.Contains("\u65E5\u672C", AllText(pdf), StringComparison.Ordinal);
    }

    [Fact]
    public void Unsupported_content_is_reported_once()
    {
        var (_, diagnostics) = Convert(Document(@"{\*\do x}{\*\do y}{\pict\macpict 01}{\pict\pmmetafile 01}{\*\annotation c}\sectd\cols2 text\par"));
        Assert.Equal(diagnostics.Count, diagnostics.Distinct().Count());
        Assert.All(diagnostics, d => Assert.Equal(DocumentDiagnosticKind.UnsupportedContent, d.Kind));
        Assert.Equal(4, diagnostics.Count); // Not the two columns: they are laid out.
    }

    [Fact]
    public void Damaged_pictures_are_reported_and_left_out()
    {
        var (pdf, diagnostics) = Convert(Document(@"{\pict\pngblip\picwgoal400 89504e470d0a1a0a0000} after\par"));
        Assert.Contains("after", AllText(pdf.ToArray()), StringComparison.Ordinal);
        Assert.Equal(DocumentDiagnosticKind.ImageNotLoaded, Assert.Single(diagnostics).Kind);
    }

    [Fact]
    public void Overloads_read_streams_files_and_run_asynchronously()
    {
        var bytes = Fixture("minimal.rtf");
        var expected = AllText(RtfToPdf.ConvertToBytes(bytes));
        Assert.Contains("Hello, minimal RTF document.", expected, StringComparison.Ordinal);
        using (var stream = new MemoryStream(bytes))
        {
            Assert.Equal(expected, AllText(RtfToPdf.Convert(stream).ToArray()));
        }

        using (var stream = new MemoryStream(bytes))
        {
            Assert.Equal(expected, AllText(RtfToPdf.ConvertToBytes(stream)));
        }

        Assert.Equal(expected, AllText(RtfToPdf.Convert(Fixtures.Path(Path.Combine("Documents", "Rtf", "minimal.rtf"))).ToArray()));
    }

    [Fact(Timeout = 60_000)]
    public async Task Async_overloads_convert_and_observe_cancellation()
    {
        var bytes = Fixture("minimal.rtf");
        var pdf = await RtfToPdf.ConvertAsync(bytes, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("minimal", AllText(pdf.ToArray()), StringComparison.Ordinal);
        using var stream = new MemoryStream(bytes);
        Assert.Single((await RtfToPdf.ConvertAsync(stream, cancellationToken: TestContext.Current.CancellationToken)).Pages);
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RtfToPdf.ConvertAsync(Fixture("tables.rtf"), null, cancel.Token));
    }

    [Fact]
    public void Arguments_are_checked()
    {
        Assert.Throws<ArgumentNullException>(() => RtfToPdf.Convert((byte[])null!));
        Assert.Throws<ArgumentNullException>(() => RtfToPdf.Convert((Stream)null!));
        Assert.Throws<ArgumentNullException>(() => RtfToPdf.Convert((string)null!));
        Assert.Throws<ArgumentNullException>(() => { _ = RtfToPdf.ConvertAsync((byte[])null!, cancellationToken: TestContext.Current.CancellationToken); });
        Assert.Throws<ArgumentNullException>(() => RtfToPdf.Convert(Fixture("minimal.rtf"), new RtfConverterOptions { FontFamily = null! }));
        Assert.Throws<ArgumentNullException>(() => RtfToPdf.Convert(Fixture("minimal.rtf"), new RtfConverterOptions { Limits = null! }));
    }

    [Fact]
    public void Not_an_rtf_document_throws_a_format_exception()
    {
        Assert.Throws<DocumentFormatException>(() => RtfToPdf.Convert("hello"u8.ToArray()));
        Assert.Contains("ZIP package", Assert.Throws<DocumentFormatException>(() => RtfToPdf.Convert(Fixtures.Read(Path.Combine("Documents", "minimal.docx")))).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resource_limits_bound_the_conversion()
    {
        var big = Document(string.Concat(Enumerable.Range(0, 300).Select(i => $@"\pard Paragraph {i} with some text in it.\par")));
        string Limit(ResourceLimits limits) =>
            Assert.Throws<ResourceLimitExceededException>(() => RtfToPdf.Convert(big, new RtfConverterOptions { Limits = limits })).LimitName;
        Assert.Equal(nameof(ResourceLimits.MaxElements), Limit(ResourceLimits.Default with { MaxElements = 500 }));
        Assert.Equal(nameof(ResourceLimits.MaxPages), Limit(ResourceLimits.Default with { MaxPages = 2 }));
        Assert.Equal(nameof(ResourceLimits.Timeout), Limit(ResourceLimits.Default with { Timeout = TimeSpan.FromTicks(1) }));
        var nested = @"{\rtf1 " + string.Concat(Enumerable.Range(1, 30).Select(l => $@"\pard\intbl\itap{l} level {l}\par")) + "}";
        Assert.Equal(nameof(ResourceLimits.MaxNestingDepth), Assert.Throws<ResourceLimitExceededException>(() =>
            RtfToPdf.Convert(Encoding.Latin1.GetBytes(nested), new RtfConverterOptions { Limits = ResourceLimits.Default with { MaxNestingDepth = 20 } })).LimitName);
        Assert.Contains("level 30", AllText(RtfToPdf.ConvertToBytes(Encoding.Latin1.GetBytes(nested))), StringComparison.Ordinal);
    }

    [Fact]
    public void Deep_groups_do_not_overflow_the_stack()
    {
        var deep = Encoding.Latin1.GetBytes(@"{\rtf1 " + new string('{', 250) + "deep" + new string('}', 250) + "}");
        string? text = null;
        Exception? error = null;
        var thread = new Thread(() => error = Record.Exception(() => text = AllText(RtfToPdf.ConvertToBytes(deep))), 256 * 1024);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)));
        Assert.Null(error);
        Assert.Contains("deep", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Conversions_run_in_parallel()
    {
        var bytes = Fixture("lists.rtf");
        var expected = AllText(RtfToPdf.ConvertToBytes(bytes));
        var results = new string[8];
        Parallel.For(0, results.Length, i => results[i] = AllText(RtfToPdf.ConvertToBytes(bytes)));
        Assert.All(results, r => Assert.Equal(expected, r));
    }
}
