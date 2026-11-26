using System.Text;
using System.Text.Json;
using RaiCore.Pdf.Documents;
using RaiCore.Pdf.Extraction;
using static RaiCore.Pdf.Tests.WordDocuments.Odt;

namespace RaiCore.Pdf.Tests.WordDocuments;

/// <summary>
/// Converting OpenDocument text documents to PDF: the fixtures and the Apache Tika samples against an independent reader
/// (odfpy), the layout of page styles, headers and footers, lists, tables, notes, fields and images, options, malformed
/// and hostile input.
/// </summary>
public class OdtToPdfTests
{
    private static string ExpectedPath => Fixtures.Path(Path.Combine("Documents", "Odt", "expected.json"));

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
    /// Every paragraph odfpy finds in a document's body (lists, sections, indexes, table cells, text boxes; deleted and
    /// hidden text, comments and notes left out) is in the PDF's text, word by word in order (ignoring case, since
    /// capitals are drawn as capitals); the cells of a table row may be in any order, as their lines interleave. The
    /// generated fixtures have nothing clipped or wider than its box. The cells of consecutive rows may be in any order
    /// too, since text extraction can read a table column by column.
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
        var rows = position;
        var end = position;
        foreach (var item in expected.RootElement.GetProperty(name).EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                position = FindWords(name, text, item.GetString()!, end);
                rows = end = position;
                continue;
            }

            foreach (var cell in item.EnumerateArray())
            {
                end = Math.Max(end, FindWords(name, text, cell.GetString()!, rows));
            }
        }

        if (name.StartsWith("Documents", StringComparison.Ordinal))
        {
            Assert.DoesNotContain(diagnostics, d => d.Kind == DocumentDiagnosticKind.Layout);
        }

        AssertValid(data);
    }

    /// <summary>
    /// Finds a paragraph's words (split at punctuation too, where lines may break) in order from a position, returning
    /// the position after its last word.
    /// </summary>
    private static int FindWords(string name, string text, string paragraph, int position)
    {
        foreach (var raw in System.Text.RegularExpressions.Regex.Split(paragraph, @"[^\p{L}\p{Nd}]+"))
        {
            var word = Words(raw);
            if (word.Length == 0 || (word.All(char.IsAsciiDigit) && paragraph.Contains('\t', StringComparison.Ordinal)) || word.Any(c => c is >= '\u2E80' and <= '\u9FFF'))
            {
                continue; // Punctuation alone, a page number after a tab (a table of contents), or CJK text, which the bundled fonts lack.
            }

            var found = text.IndexOf(word, position, StringComparison.OrdinalIgnoreCase);
            Assert.True(found >= 0, $"{name}: '{raw}' of \"{paragraph}\" is missing after position {position}.");
            position = found + word.Length;
        }

        return position;
    }

    /// <summary>Text without whitespace and punctuation: letters, digits and symbols only.</summary>
    private static string Words(string text) => new([.. text.Where(char.IsLetterOrDigit)]);

    [Fact]
    public void Hidden_deleted_and_comment_text_is_not_drawn_and_capitals_are()
    {
        var styles = AllText(OdtToPdf.ConvertToBytes(Fixture("styles.odt")));
        Assert.DoesNotContain("Hidden text", styles, StringComparison.Ordinal);
        Assert.Contains("Visible text continues.", styles, StringComparison.Ordinal);
        Assert.Contains("ALL CAPS", styles, StringComparison.Ordinal);
        Assert.Contains("SMALL CAPS", styles, StringComparison.Ordinal);
        Assert.Contains("lower case", styles, StringComparison.Ordinal);
        Assert.Contains("Capitalized Words", styles, StringComparison.Ordinal);
        var fields = AllText(OdtToPdf.ConvertToBytes(Fixture("fields.odt")));
        foreach (var left in new[] { "deleted words", "A comment", "Reviewer", "Hidden section text", "annotation", "hidden by a field", "Template title" })
        {
            Assert.DoesNotContain(left, fields, StringComparison.Ordinal);
        }

        Assert.Contains("Inserted new text stays.", fields, StringComparison.Ordinal);
    }

    [Fact]
    public void Page_styles_give_each_section_its_size_headers_footers_and_numbers()
    {
        var bytes = OdtToPdf.ConvertToBytes(Fixture("pages.odt"));
        var reader = PdfReader.Open(bytes);
        var pages = Pages(bytes);
        Assert.Equal(9, pages.Length);
        double[][] sizes = [[595.3, 841.9], [595.3, 841.9], [841.9, 595.3], [612, 792], [612, 792], [595.3, 841.9], [595.3, 841.9], [595.3, 841.9], [595.3, 841.9]];
        for (var i = 0; i < sizes.Length; i++)
        {
            Assert.Equal(sizes[i][0], reader.Pages[i].Width, 0);
            Assert.Equal(sizes[i][1], reader.Pages[i].Height, 0);
        }

        string[] expected =
        [
            "Default header 1|Page 1 of 9", "Left header 2|Page 2 of 9", "Landscape header|Roman page i", "Title page header", "Following header",
            "ODF 1.3 first header", "ODF 1.3 header", "Left header 6|Page 6 of 9", string.Empty,
        ];
        for (var i = 0; i < expected.Length; i++)
        {
            foreach (var part in expected[i].Split('|', StringSplitOptions.RemoveEmptyEntries))
            {
                Assert.Contains(part, pages[i], StringComparison.Ordinal);
            }
        }

        Assert.DoesNotContain("header", pages[8], StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Hidden footer", string.Join("\n", pages), StringComparison.Ordinal);
        Assert.DoesNotContain("Title page header", pages[4], StringComparison.Ordinal);
        Assert.DoesNotContain("ODF 1.3 first header", pages[6], StringComparison.Ordinal);
        Assert.Contains("ODF 1.3 second page.", pages[6], StringComparison.Ordinal);
        Assert.Contains("Break after this paragraph.", pages[5], StringComparison.Ordinal);
    }

    [Fact]
    public void The_header_sits_in_the_top_margin_and_the_text_below_its_height_and_spacing()
    {
        var bytes = OdtToPdf.ConvertToBytes(Fixture("pages.odt"));
        var header = Find(bytes, 0, "Default header").Single().Bounds;
        var text = Find(bytes, 0, "Page one text.").Single().Bounds;
        var footer = Find(bytes, 0, "Page 1 of 9").Single().Bounds;
        var cm = 72 / 2.54;
        Assert.InRange(841.9 - header.Top, 2 * cm - 1, 2 * cm + 3); // The header starts at the 2 cm page margin.
        Assert.InRange(841.9 - text.Top, 3.5 * cm, (3.5 * cm) + 4); // Below 1 cm of header and 0.5 cm of spacing.
        Assert.InRange(footer.Y, 2 * cm - 4, 2 * cm + 4); // The footer ends at the bottom margin.
        Assert.Equal(56.7, Find(bytes, 0, "Page one text.").Single().Bounds.X, 0);
    }

    [Fact]
    public void List_labels_hang_in_the_indent_with_their_numbers()
    {
        var text = AllText(OdtToPdf.ConvertToBytes(Fixture("lists.odt")));
        foreach (var label in new[]
        {
            "1. First item", "2. Second item", "a) Nested item", "b) Another nested item", "(i) Roman item", "3. Back to the top level",
            "4. Continued item", "1. Level one", "1.1. Level two", "1.1.1. Level three", "\u2022 Bullet one", "C. Letter C item", "D. Letter D item",
            "V. Roman five", "VI. Roman six", "VII. Roman seven", "10. Restarted at ten", "11. Eleven", "1. Item after the header",
            "1. Paragraph style list", "a) Numbered paragraph", "\u2022 Private use bullet",
        })
        {
            Assert.Contains(label, text.Replace('\t', ' ').Replace("  ", " ", StringComparison.Ordinal), StringComparison.Ordinal);
        }

        Assert.DoesNotContain("a)a)", text, StringComparison.Ordinal); // The cached number of the numbered paragraph is not repeated.
        var pdf = OdtToPdf.ConvertToBytes(Fixture("lists.odt"));
        var cm = 72 / 2.54;
        Assert.Equal(56.7 + (0.635 * cm), Find(pdf, 0, "1.").First().Bounds.X, 0);
        Assert.Equal(56.7 + (1.27 * cm), Find(pdf, 0, "First item").Single().Bounds.X, 0);
        Assert.Equal(56.7 + (2.54 * cm), Find(pdf, 0, "Nested item").Single().Bounds.X, 0);
        Assert.Equal(56.7 + (1 * cm), Find(pdf, 0, "Letter C item").Single().Bounds.X, 0); // Old positioning: the minimum label width.
        Assert.Equal(56.7, Find(pdf, 0, "C.").Single().Bounds.X, 0);
        Assert.Equal(56.7 + (1.27 * cm), Find(pdf, 0, "A list header without a label").Single().Bounds.X, 0);
    }

    [Fact]
    public void Tables_repeat_header_rows_span_cells_and_start_pages()
    {
        var bytes = OdtToPdf.ConvertToBytes(Fixture("tables.odt"));
        var pages = Pages(bytes);
        Assert.Equal(3, pages.Length);
        Assert.Contains("Long header", pages[1], StringComparison.Ordinal);
        Assert.Contains("Row 60", pages[1], StringComparison.Ordinal);
        Assert.StartsWith("Table on a new page", pages[2].TrimStart(), StringComparison.Ordinal);
        var cm = 72 / 2.54;
        Assert.Equal(56.7 + (4 * cm) + 2.8, Find(bytes, 0, "B3").Single().Bounds.X, 0);
        Assert.Equal(56.7 + (10 * cm) + 2.8, Find(bytes, 0, "D2").Single().Bounds.X, 0);
        Assert.Equal(56.7 + (7 * cm) + 2.8, Find(bytes, 0, "C2").Single().Bounds.X, 0); // After the cell spanning two columns.
        Assert.Equal(3, Find(bytes, 0, "Repeated row").Count);
        Assert.Equal(3, new TextExtractor(PdfReader.Open(bytes)).FindOnPage(0, new System.Text.RegularExpressions.Regex("(?<![A-Za-z])r(?![A-Za-z])")).Count);
        var right = Find(bytes, 2, "Table on a new page").Single().Bounds;
        Assert.True(right.X > 300, $"{right.X}"); // Aligned right.
        var narrow = Find(bytes, 1, "Narrow").Single().Bounds;
        var wide = Find(bytes, 1, "Wide relative column").Single().Bounds;
        Assert.Equal((595.3 - 56.7 - 56.7) / 2 / 4, wide.X - narrow.X, 0); // Columns 1* and 3* of a table half the width.
    }

    [Fact]
    public void Cells_are_aligned_vertically_and_rows_have_their_heights()
    {
        var bytes = OdtToPdf.ConvertToBytes(Fixture("tables.odt"));
        var middle = Find(bytes, 0, "Middle cell").Single().Bounds;
        var bottom = Find(bytes, 0, "Bottom cell").Single().Bounds;
        var plain = Find(bytes, 0, "Plain cell").Single().Bounds;
        Assert.True(plain.Top > middle.Top + 10, $"{plain.Top} {middle.Top}");
        Assert.True(middle.Top > bottom.Top + 10, $"{middle.Top} {bottom.Top}");
        var exact = Find(bytes, 0, "Exact height row").Single().Bounds;
        var next = Find(bytes, 0, "Between tables.").Single().Bounds;
        Assert.InRange(exact.Top - next.Top, 1.3 * 72 / 2.54, 1.5 * 72 / 2.54); // 1.5 cm, less the cell's top padding.
    }

    [Fact]
    public void Notes_follow_their_section_and_page_references_show_pdf_pages()
    {
        var pages = Pages(OdtToPdf.ConvertToBytes(Fixture("fields.odt")));
        Assert.Equal(2, pages.Length);
        Assert.Contains("See page 2 for the later bookmark.", pages[0], StringComparison.Ordinal);
        Assert.Contains("1 The first footnote text.", pages[1], StringComparison.Ordinal);
        Assert.Contains("2 Custom mark note.", pages[1], StringComparison.Ordinal); // Numbered as the second footnote.
        Assert.Contains("i The endnote text.", pages[1], StringComparison.Ordinal);
        Assert.Contains("custom mark*", pages[0], StringComparison.Ordinal);
        Assert.True(pages[1].IndexOf("Custom mark note.", StringComparison.Ordinal) < pages[1].IndexOf("The endnote text.", StringComparison.Ordinal));
        Assert.Contains("Later section", pages[0], StringComparison.Ordinal);
        Assert.Contains("Page 2 of 2 in the body.", pages[1], StringComparison.Ordinal);
    }

    [Fact]
    public void Note_citations_are_raised_and_link_to_their_notes()
    {
        var pdf = OdtToPdf.Convert(Fixture("fields.odt"));
        var bytes = pdf.ToArray();
        var here = Find(bytes, 0, "here").First().Bounds;
        var mark = Find(bytes, 0, "1").First(m => m.Bounds.X > here.X && Math.Abs(m.Bounds.Y - here.Y) < 10).Bounds;
        Assert.True(mark.Y > here.Y + 1, $"{mark.Y} {here.Y}");
        Assert.True(mark.Height < here.Height, $"{mark.Height} {here.Height}");
        Assert.True(pdf.NamedDestinations.ContainsKey("_fn1"));
        Assert.True(pdf.NamedDestinations.ContainsKey("_en1"));
        Assert.Contains(pdf.Pages.SelectMany(p => p.Links), l => l.DestinationName == "_fn2");
        Assert.True(pdf.NamedDestinations.ContainsKey("later"));
    }

    [Fact]
    public void Images_are_drawn_inline_floating_and_as_blocks_and_others_are_reported()
    {
        var (pdf, diagnostics) = Convert(Fixture("images.odt"));
        var bytes = pdf.ToArray();
        var images = new TextExtractor(PdfReader.Open(bytes)).ExtractPages();
        Assert.Single(images);
        Assert.Contains(diagnostics, d => d.Kind == DocumentDiagnosticKind.ImageNotLoaded && d.Message.Contains("linked image", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Kind == DocumentDiagnosticKind.ImageNotLoaded && d.Message.Contains("Pictures/missing.png", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Kind == DocumentDiagnosticKind.ImageNotLoaded && d.Message.Contains("Pictures/old.wmf", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Message.Contains("embedded object", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Message.Contains("text box", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Message.Contains("drawing shape is laid out", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Message == "A drawing shape is left out.");
        Assert.DoesNotContain(diagnostics, d => d.Message.Contains("vector", StringComparison.OrdinalIgnoreCase));

        var text = AllText(bytes);
        Assert.True(text.IndexOf("Text box anchor.", StringComparison.Ordinal) < text.IndexOf("Text box content.", StringComparison.Ordinal));
        var inline = Find(bytes, 0, "Inline image:").Single().Bounds;
        var after = Find(bytes, 0, "after the image.").Single().Bounds;
        Assert.Equal(2 * 72 / 2.54, after.X - inline.Right - 6, 0); // The 2 cm image between the words (and two spaces).
    }

    [Fact]
    public void Floating_images_wrap_the_text_at_their_margin()
    {
        var bytes = OdtToPdf.ConvertToBytes(Fixture("images.odt"));
        var right = Find(bytes, 0, "Floating right:").Single().Bounds;
        Assert.Equal(56.7, right.X, 0);
        var left = Find(bytes, 0, "Floating left:").Single().Bounds;
        Assert.True(left.X > 56.7 + (2 * 72 / 2.54), $"{left.X}"); // Beside the 2 cm image at the left margin.
        var centered = Find(bytes, 0, "Centered block image paragraph.").Single().Bounds;
        Assert.Equal(56.7, centered.X, 0); // The image stands on a line of its own above it.
    }

    [Fact]
    public void Flat_documents_and_templates_convert()
    {
        var (pdf, diagnostics) = Convert(Fixture("flat.fodt"));
        var bytes = pdf.ToArray();
        var text = AllText(bytes);
        Assert.Contains("Flat header", text, StringComparison.Ordinal);
        Assert.Contains("1. Flat item one", text.Replace('\t', ' ').Replace("  ", " ", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Equal("Flat document", pdf.Info.Title);
        Assert.Equal("Flat Author", pdf.Info.Author);
        Assert.DoesNotContain(diagnostics, d => d.Kind == DocumentDiagnosticKind.ImageNotLoaded);
        Assert.Contains("/Subtype /Image", Encoding.Latin1.GetString(bytes), StringComparison.Ordinal);
        Assert.Contains("Template text.", AllText(OdtToPdf.ConvertToBytes(Fixture("template.ott"))), StringComparison.Ordinal);
    }

    [Fact]
    public void Title_and_author_come_from_the_metadata_unless_the_options_give_a_title()
    {
        var pdf = OdtToPdf.Convert(Fixture("styles.odt"));
        Assert.Equal("Styles fixture", pdf.Info.Title);
        Assert.Equal("Fixture Author", pdf.Info.Author);
        Assert.Equal("Mine", OdtToPdf.Convert(Fixture("styles.odt"), new OdtConverterOptions { Title = "Mine" }).Info.Title);
        Assert.Null(OdtToPdf.Convert(Fixture("minimal.odt")).Info.Title);
    }

    [Fact]
    public void Headings_become_bookmarks_and_tagged_headings()
    {
        var pdf = OdtToPdf.Convert(Fixture("styles.odt"), new OdtConverterOptions { Tagged = true });
        Assert.Equal(["Document styles", "Targets"], pdf.Outline.Select(o => o.Title));
        Assert.Equal(["Paragraph formats", "Tabs and spaces"], pdf.Outline[0].Children.Select(o => o.Title));
        Assert.Empty(OdtToPdf.Convert(Fixture("styles.odt"), new OdtConverterOptions { HeadingBookmarks = false }).Outline);
        var types = Flatten(pdf.StructureRoot).Select(e => e.Type).ToList();
        Assert.Contains("H1", types);
        Assert.Contains("H2", types);
        Assert.Equal("en-GB", pdf.Language);

        static IEnumerable<PdfStructureElement> Flatten(PdfStructureElement element) => element.Children.SelectMany(Flatten).Prepend(element);
    }

    [Fact]
    public void Links_go_to_web_addresses_and_bookmarks()
    {
        var pdf = OdtToPdf.Convert(Fixture("styles.odt"));
        var links = pdf.Pages.SelectMany(p => p.Links).ToList();
        Assert.Equal(2, links.Count);
        Assert.Contains(links, l => l.Uri == new Uri("https://example.com/"));
        Assert.Contains(links, l => l.DestinationName == "target");
        Assert.True(pdf.NamedDestinations.ContainsKey("target"));
        Assert.True(pdf.NamedDestinations.ContainsKey("paragraph"));
    }

    [Fact]
    public void Overloads_read_streams_files_and_run_asynchronously()
    {
        var bytes = Fixture("minimal.odt");
        var expected = AllText(OdtToPdf.ConvertToBytes(bytes));
        Assert.Contains("Hello, OpenDocument.", expected, StringComparison.Ordinal);
        using (var stream = new MemoryStream(bytes))
        {
            Assert.Equal(expected, AllText(OdtToPdf.Convert(stream).ToArray()));
        }

        using (var stream = new MemoryStream(bytes))
        {
            Assert.Equal(expected, AllText(OdtToPdf.ConvertToBytes(stream)));
        }

        var path = Path.Combine(Path.GetTempPath(), $"odt-{Guid.NewGuid():N}.odt");
        File.WriteAllBytes(path, bytes);
        try
        {
            Assert.Equal(expected, AllText(OdtToPdf.Convert(path).ToArray()));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task Async_overloads_convert_and_observe_cancellation()
    {
        var bytes = Fixture("minimal.odt");
        var token = TestContext.Current.CancellationToken;
        Assert.Contains("Hello", AllText((await OdtToPdf.ConvertAsync(bytes, cancellationToken: token)).ToArray()), StringComparison.Ordinal);
        using (var stream = new MemoryStream(bytes))
        {
            Assert.Contains("Hello", AllText((await OdtToPdf.ConvertAsync(stream, cancellationToken: token)).ToArray()), StringComparison.Ordinal);
        }

        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => OdtToPdf.ConvertAsync(bytes, cancellationToken: canceled.Token));
        await Assert.ThrowsAsync<ArgumentNullException>(() => OdtToPdf.ConvertAsync((byte[])null!, cancellationToken: token));
        await Assert.ThrowsAsync<ArgumentNullException>(() => OdtToPdf.ConvertAsync((Stream)null!, cancellationToken: token));
    }

    [Fact]
    public void Arguments_are_checked()
    {
        Assert.Throws<ArgumentNullException>(() => OdtToPdf.Convert((byte[])null!));
        Assert.Throws<ArgumentNullException>(() => OdtToPdf.Convert((Stream)null!));
        Assert.Throws<ArgumentNullException>(() => OdtToPdf.Convert((string)null!));
        Assert.Throws<ArgumentNullException>(() => OdtToPdf.Convert(Fixture("minimal.odt"), new OdtConverterOptions { FontFamily = null! }));
        Assert.Throws<ArgumentNullException>(() => OdtToPdf.Convert(Fixture("minimal.odt"), new OdtConverterOptions { Limits = null! }));
        Assert.Throws<ArgumentNullException>(() => OdtToPdf.Convert(Fixture("minimal.odt"), new OdtConverterOptions { FallbackFontFamilies = null! }));
    }

    [Fact]
    public void Not_an_opendocument_text_throws_a_format_exception()
    {
        string Message(byte[] data) => Assert.Throws<DocumentFormatException>(() => OdtToPdf.Convert(data)).Message;
        Assert.Contains("not an OpenDocument text document", Message(Encoding.ASCII.GetBytes("hello")), StringComparison.Ordinal);
        Assert.Contains("not an OpenDocument text document", Message(Fixtures.Read(Path.Combine("Tika", "testODTnotaZipFile.odt"))), StringComparison.Ordinal);
        Assert.Contains("RTF", Message(Encoding.ASCII.GetBytes(@"{\rtf1 x}")), StringComparison.Ordinal);
        Assert.Contains("Compound File", Message(Fixtures.Read(Path.Combine("Poi", "documentProperties.doc"))), StringComparison.Ordinal);
        Assert.Contains("ZIP", Message("PK\u0003\u0004 broken"u8.ToArray()), StringComparison.Ordinal);
        Assert.Contains("no content.xml", Message(Package(MediaType, ("styles.xml", "<x/>"))), StringComparison.Ordinal);
        Assert.Contains("Office Open XML", Message(Fixtures.Read(Path.Combine("Documents", "minimal.docx"))), StringComparison.Ordinal);
        Assert.Contains("spreadsheet", Message(Package(MediaType.Replace("text", "spreadsheet", StringComparison.Ordinal), ("content.xml", Content(P("x"))))), StringComparison.Ordinal);
        Assert.Contains("media type is 'text/plain'", Message(Package("text/plain", ("content.xml", Content(P("x"))))), StringComparison.Ordinal);
        var spreadsheet = $"<office:document-content {Namespaces}><office:body><office:spreadsheet/></office:body></office:document-content>";
        Assert.Contains("office:spreadsheet", Message(Package(null, ("content.xml", spreadsheet))), StringComparison.Ordinal);
        Assert.Contains("office:document-content", Message(Package(MediaType, ("content.xml", "<other/>"))), StringComparison.Ordinal);
        Assert.Contains("not well-formed", Message(Package(MediaType, ("content.xml", "<office:document-content"))), StringComparison.Ordinal);
        Assert.Contains("flat OpenDocument", Message(Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><html/>")), StringComparison.Ordinal);
        Assert.Contains("no text body", Message(Encoding.UTF8.GetBytes($"<office:document {Namespaces}/>")), StringComparison.Ordinal);
        Assert.Contains("presentation", Message(Encoding.UTF8.GetBytes(
            $"<office:document {Namespaces} office:mimetype=\"application/vnd.oasis.opendocument.presentation\"><office:body><office:text/></office:body></office:document>")), StringComparison.Ordinal);
    }

    [Fact]
    public void A_package_without_a_mimetype_converts_by_its_content()
    {
        Assert.Contains("No media type", AllText(OdtToPdf.ConvertToBytes(Package(null, ("content.xml", Content(P("No media type.")))))), StringComparison.Ordinal);
        var bom = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes($"  \n<office:document {Namespaces}><office:body><office:text>{P("Flat with a byte order mark.")}</office:text></office:body></office:document>")).ToArray();
        Assert.Contains("Flat with a byte order mark.", AllText(OdtToPdf.ConvertToBytes(bom)), StringComparison.Ordinal);
    }

    [Fact]
    public void Encrypted_documents_are_not_supported()
    {
        Assert.Contains("encrypted", Assert.Throws<NotSupportedException>(() => OdtToPdf.Convert(Fixtures.Read(Path.Combine("Tika", "testODTEncrypted.odt")))).Message, StringComparison.Ordinal);
        var manifest = "<manifest:manifest xmlns:manifest=\"urn:oasis:names:tc:opendocument:xmlns:manifest:1.0\"><manifest:file-entry manifest:full-path=\"content.xml\">"
            + "<manifest:encryption-data manifest:checksum-type=\"SHA1/1K\"/></manifest:file-entry></manifest:manifest>";
        Assert.Throws<NotSupportedException>(() => OdtToPdf.Convert(Package(MediaType, ("META-INF/manifest.xml", manifest), ("content.xml", new byte[] { 1, 2, 3 }))));
    }

    [Fact]
    public void Dtds_and_external_entities_are_refused()
    {
        var content = $"<?xml version=\"1.0\"?><!DOCTYPE x [<!ENTITY e SYSTEM \"file:///etc/passwd\">]><office:document-content {Namespaces}><office:body><office:text><text:p>&e;</text:p></office:text></office:body></office:document-content>";
        Assert.Contains("DTD", Assert.Throws<DocumentFormatException>(() => OdtToPdf.Convert(Package(MediaType, ("content.xml", content)))).Message, StringComparison.Ordinal);
        Assert.Contains("DTD", Assert.Throws<DocumentFormatException>(() => OdtToPdf.Convert(Encoding.UTF8.GetBytes(content.Replace("document-content", "document", StringComparison.Ordinal)))).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resource_limits_bound_the_conversion()
    {
        var big = Document(string.Concat(Enumerable.Range(0, 300).Select(i => P($"Paragraph {i} with some text in it."))));
        string Limit(byte[] odt, ResourceLimits limits) =>
            Assert.Throws<ResourceLimitExceededException>(() => OdtToPdf.Convert(odt, new OdtConverterOptions { Limits = limits })).LimitName;
        Assert.Equal(nameof(ResourceLimits.MaxDecompressedBytes), Limit(big, ResourceLimits.Default with { MaxDecompressedBytes = 5000 }));
        Assert.Equal(nameof(ResourceLimits.MaxElements), Limit(big, ResourceLimits.Default with { MaxElements = 200 }));
        Assert.Equal(nameof(ResourceLimits.MaxPages), Limit(big, ResourceLimits.Default with { MaxPages = 2 }));
        Assert.Equal(nameof(ResourceLimits.Timeout), Limit(big, ResourceLimits.Default with { Timeout = TimeSpan.FromTicks(1) }));
        var table = "<table:table>" + string.Concat(Enumerable.Repeat("<table:table-row>" + string.Concat(Enumerable.Repeat("<table:table-cell/>", 10)) + "</table:table-row>", 20)) + "</table:table>";
        Assert.Equal(nameof(ResourceLimits.MaxTableCells), Limit(Document(table), ResourceLimits.Default with { MaxTableCells = 150 }));
        var flat = Encoding.UTF8.GetBytes($"<office:document {Namespaces}><office:body><office:text>{string.Concat(Enumerable.Repeat(P("x"), 300))}</office:text></office:body></office:document>");
        Assert.Equal(nameof(ResourceLimits.MaxElements), Limit(flat, ResourceLimits.Default with { MaxElements = 200 }));
        var embedded = Encoding.UTF8.GetBytes($"<office:document {Namespaces}><office:body><office:text>{P($"<draw:frame text:anchor-type=\"as-char\"><draw:image><office:binary-data>{System.Convert.ToBase64String(new byte[30000])}</office:binary-data></draw:image></draw:frame>")}</office:text></office:body></office:document>");
        Assert.Equal(nameof(ResourceLimits.MaxDecompressedBytes), Limit(embedded, ResourceLimits.Default with { MaxDecompressedBytes = 10000 }));
    }

    [Theory]
    [InlineData("", "<table:table-row table:number-rows-repeated=\"1048576\"><table:table-cell/></table:table-row>", nameof(ResourceLimits.MaxElements))]
    [InlineData("", "<table:table-row table:number-rows-repeated=\"1048576\"><table:table-cell/><table:table-cell/><table:table-cell/></table:table-row>", nameof(ResourceLimits.MaxTableCells))]
    [InlineData("<table:table-column table:number-columns-repeated=\"16384\"/>", "<table:table-row table:number-rows-repeated=\"2000\"><table:table-cell/></table:table-row>", nameof(ResourceLimits.MaxTableCells))]
    [InlineData("", "<table:table-row table:number-rows-repeated=\"2000\"><table:table-cell table:number-columns-repeated=\"16384\"/></table:table-row>", nameof(ResourceLimits.MaxTableCells))]
    [InlineData("", "<table:table-row><table:table-cell table:number-columns-repeated=\"1000\"/></table:table-row>", nameof(ResourceLimits.MaxElements))]
    public void Repeated_rows_and_cells_count_against_the_limits(string columns, string rows, string limit)
    {
        var odt = Document($"<table:table>{columns}{rows}</table:table>");
        var limits = rows.Contains("\"1000\"", StringComparison.Ordinal) ? ResourceLimits.Default with { MaxElements = 500 } : ResourceLimits.Default;
        Assert.Equal(limit, Assert.Throws<ResourceLimitExceededException>(() => OdtToPdf.Convert(odt, new OdtConverterOptions { Limits = limits })).LimitName);
    }

    [Fact]
    public void Deeply_nested_tables_and_elements_hit_the_nesting_limit()
    {
        var nested = string.Concat(Enumerable.Repeat("<table:table><table:table-row><table:table-cell>", 12)) + P("deep") + string.Concat(Enumerable.Repeat("</table:table-cell></table:table-row></table:table>", 12));
        var limits = ResourceLimits.Default with { MaxNestingDepth = 30 };
        Assert.Equal(nameof(ResourceLimits.MaxNestingDepth),
            Assert.Throws<ResourceLimitExceededException>(() => OdtToPdf.Convert(Document(nested), new OdtConverterOptions { Limits = limits })).LimitName);
        Assert.Contains("deep", AllText(OdtToPdf.ConvertToBytes(Document(nested))), StringComparison.Ordinal);
        var tables = ResourceLimits.Default with { MaxNestingDepth = 5 };
        var spans = string.Concat(Enumerable.Repeat("<text:span>", 300)) + "x" + string.Concat(Enumerable.Repeat("</text:span>", 300));
        Assert.Equal(nameof(ResourceLimits.MaxNestingDepth),
            Assert.Throws<ResourceLimitExceededException>(() => OdtToPdf.Convert(Document(P(spans)))).LimitName);
        Assert.Equal(nameof(ResourceLimits.MaxNestingDepth),
            Assert.Throws<ResourceLimitExceededException>(() => OdtToPdf.Convert(Document(nested), new OdtConverterOptions { Limits = tables })).LimitName);
    }

    [Fact]
    public void Scripts_and_forms_are_reported_and_columns_are_laid_out()
    {
        var (_, diagnostics) = Convert(Fixtures.Read(Path.Combine("Tika", "testODTMacro.odt")));
        Assert.Contains(diagnostics, d => d.Message.Contains("scripts", StringComparison.Ordinal));
        var (_, flat) = Convert(Fixtures.Read(Path.Combine("Tika", "testODTMacro.fodt")));
        Assert.Contains(flat, d => d.Message.Contains("scripts", StringComparison.Ordinal));
        var (_, fields) = Convert(Fixture("fields.odt"));
        Assert.DoesNotContain(fields, d => d.Message.Contains("text columns", StringComparison.Ordinal)); // A section in two columns.
        var form = Document("<office:forms><form:form xmlns:form=\"urn:oasis:names:tc:opendocument:xmlns:form:1.0\"/></office:forms>" + P("x<draw:control draw:control=\"c1\"/>"));
        var (_, forms) = Convert(form);
        Assert.Contains(forms, d => d.Message == "Forms are left out.");
        Assert.Contains(forms, d => d.Message == "A form control is left out.");
        var layout = "<style:page-layout style:name=\"pm\"><style:page-layout-properties fo:page-width=\"21cm\" fo:page-height=\"29.7cm\">"
            + "<style:columns fo:column-count=\"3\"/></style:page-layout-properties></style:page-layout>";
        var columns = Document(P("Columns"), pageLayouts: layout, masters: "<style:master-page style:name=\"Standard\" style:page-layout-name=\"pm\"/>");
        var (_, page) = Convert(columns);
        Assert.DoesNotContain(page, d => d.Message.Contains("text columns", StringComparison.Ordinal));
    }

    [Fact]
    public void Tika_samples_render_their_headers_footers_notes_and_lists()
    {
        var footer = Pages(OdtToPdf.ConvertToBytes(Fixtures.Read(Path.Combine("Tika", "testFooter.odt"))));
        Assert.Equal(2, footer.Length);
        Assert.All(footer, page => Assert.Contains("Here is footer text", page, StringComparison.Ordinal));
        Assert.Contains("Here is some text on page 2", footer[1], StringComparison.Ordinal);
        var tika = Pages(OdtToPdf.ConvertToBytes(Fixtures.Read(Path.Combine("Tika", "testODFwithOOo3.odt"))));
        Assert.Contains("1 This is of course a simple footnote", tika[^1], StringComparison.Ordinal);
        foreach (var label in new[] { "1. First item", "2. Second item", "3. Third item" })
        {
            Assert.Contains(label, string.Join("\n", tika).Replace('\t', ' ').Replace("  ", " ", StringComparison.Ordinal), StringComparison.Ordinal);
        }

        Assert.DoesNotContain("complex", string.Join("\n", tika), StringComparison.Ordinal); // Deleted by a tracked change.
        Assert.DoesNotContain("Lucene project", string.Join("\n", tika), StringComparison.Ordinal); // A comment.
        var embedded = Convert(Fixtures.Read(Path.Combine("Tika", "testODTEmbedded.odt")));
        Assert.DoesNotContain(embedded.Diagnostics, d => d.Kind == DocumentDiagnosticKind.ImageNotLoaded);
    }

    [Fact]
    public void Conversions_run_in_parallel()
    {
        var bytes = Fixture("lists.odt");
        var expected = AllText(OdtToPdf.ConvertToBytes(bytes));
        var results = new string[8];
        Parallel.For(0, results.Length, i => results[i] = AllText(OdtToPdf.ConvertToBytes(bytes)));
        Assert.All(results, r => Assert.Equal(expected, r));
    }
}
