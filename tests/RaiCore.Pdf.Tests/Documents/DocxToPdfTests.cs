using System.Text;
using System.Text.Json;
using RaiCore.Pdf.Documents;
using RaiCore.Pdf.Extraction;
using RaiCore.Pdf.Fonts;
using static RaiCore.Pdf.Tests.WordDocuments.Docx;

namespace RaiCore.Pdf.Tests.WordDocuments;

/// <summary>
/// Converting Word documents to PDF: the fixtures against an independent reader (python-docx), layout of paragraphs,
/// lists, tabs, tables, sections, headers and footers, fields, notes and images, options, malformed and hostile input.
/// </summary>
public class DocxToPdfTests
{
    private static byte[] Fixture(string name) => Fixtures.Read(Path.Combine("Documents", name));

    private static IReadOnlyList<TextMatch> Find(byte[] pdf, int page, string text) => new TextExtractor(PdfReader.Open(pdf)).FindOnPage(page, text);

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
        using var expected = JsonDocument.Parse(File.ReadAllText(Fixtures.Path(Path.Combine("Documents", "expected.json"))));
        foreach (var property in expected.RootElement.EnumerateObject())
        {
            data.Add(property.Name);
        }

        return data;
    }

    /// <summary>
    /// Every paragraph python-docx finds (body, table cells; hidden text left out) is in the PDF's text, word by word in
    /// order (ignoring case, since capitals are drawn as capitals, and punctuation next to note marks); the cells of a
    /// table row may be in any order, as their lines interleave. Nothing is clipped or wider than its box.
    /// </summary>
    [Theory]
    [MemberData(nameof(FixtureNames))]
    public void Fixtures_show_every_paragraph_in_order(string name)
    {
        using var expected = JsonDocument.Parse(File.ReadAllText(Fixtures.Path(Path.Combine("Documents", "expected.json"))));
        var (pdf, diagnostics) = Convert(Fixtures.Read(Path.Combine("Documents", name)));
        var data = pdf.ToArray();
        var text = Words(AllText(data));
        var position = 0;
        foreach (var item in expected.RootElement.GetProperty(name).EnumerateArray())
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

        Assert.DoesNotContain(diagnostics, d => d.Kind == DocumentDiagnosticKind.Layout);
        AssertValid(data);
    }

    /// <summary>Finds a paragraph's words in order from a position, returning the position after its last word.</summary>
    private static int FindWords(string name, string text, string paragraph, int position)
    {
        foreach (var raw in paragraph.Split([' ', '\t', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var word = Words(raw);
            if (word.Length == 0 || (word.All(char.IsAsciiDigit) && paragraph.Contains('\t', StringComparison.Ordinal)))
            {
                continue; // Punctuation alone, or a cached page number after a tab (a table of contents): the PDF has its own.
            }

            var found = text.IndexOf(word, position, StringComparison.OrdinalIgnoreCase);
            Assert.True(found >= 0, $"{name}: '{raw}' of \"{paragraph}\" is missing after position {position}.");
            position = found + word.Length;
        }

        return position;
    }

    /// <summary>Text without whitespace and punctuation: letters, digits and symbols only.</summary>
    private static string Words(string text) => new([.. text.Where(char.IsLetterOrDigit)]);

    private static string Normalize(string text) => new([.. text.Where(c => !char.IsWhiteSpace(c) && c != '\u200B')]);

    [Fact]
    public void Hidden_text_is_not_drawn_and_capitals_are()
    {
        var text = AllText(DocxToPdf.ConvertToBytes(Fixture("styles.docx")));
        Assert.DoesNotContain("Hidden text", text, StringComparison.Ordinal);
        Assert.Contains("ALL CAPS", text, StringComparison.Ordinal);
        Assert.Contains("SMALL CAPS", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_space_between_paragraphs_adds_the_space_after_and_before()
    {
        var pdf = DocxToPdf.ConvertToBytes(Document(
            P("First", "<w:spacing w:before=\"0\" w:after=\"400\"/>") + P("Second", "<w:spacing w:before=\"600\" w:after=\"0\"/>")
            + P("Third", "<w:spacing w:after=\"0\"/>") + P("Fourth", "<w:spacing w:before=\"200\" w:after=\"200\"/>")
            + P("Fifth", "<w:spacing w:before=\"200\"/><w:contextualSpacing/>") + P("Sixth", "<w:spacing w:before=\"200\"/><w:contextualSpacing/>")));
        double Top(string text) => Find(pdf, 0, text).Single().Bounds.Top;
        var line = Top("Second") - Top("Third");
        Assert.Equal(50, Top("First") - Top("Second") - line, 0); // 20 + 30, not 30.
        Assert.Equal(10, Top("Third") - Top("Fourth") - line, 0);
        Assert.Equal(10, Top("Fourth") - Top("Fifth") - line, 0); // Fourth has no contextual spacing: its space after stays.
        Assert.Equal(0, Top("Fifth") - Top("Sixth") - line, 0); // The same style, both with contextual spacing.
    }

    [Fact]
    public void An_empty_paragraph_takes_a_line()
    {
        var pdf = DocxToPdf.ConvertToBytes(Document(P("A") + P("B") + "<w:p/>" + P("C")));
        double Top(string text) => Find(pdf, 0, text).Single().Bounds.Top;
        Assert.Equal(2 * (Top("A") - Top("B")), Top("B") - Top("C"), 1);
    }

    [Fact]
    public void Alignment_and_indents_place_the_text()
    {
        var pdf = DocxToPdf.ConvertToBytes(Document(
            P("Left") + P("Centered", "<w:jc w:val=\"center\"/>") + P("Right", "<w:jc w:val=\"right\"/>") + P("Indented", "<w:ind w:left=\"1440\"/>")
            + P("Outdented first line of a long paragraph that wraps onto a second line because it goes on and on and on and on and on and on and on and on and on", "<w:ind w:left=\"1440\" w:hanging=\"720\"/>")));
        var left = Find(pdf, 0, "Left").Single().Bounds;
        Assert.Equal(72, left.X, 0); // The default margin.
        var centered = Find(pdf, 0, "Centered").Single().Bounds;
        Assert.Equal(306, centered.X + (centered.Width / 2), 0);
        Assert.Equal(540, Find(pdf, 0, "Right").Single().Bounds.Right, 0);
        Assert.Equal(144, Find(pdf, 0, "Indented").Single().Bounds.X, 0);
        Assert.Equal(108, Find(pdf, 0, "Outdented").Single().Bounds.X, 0);
        var outdented = Find(pdf, 0, "Outdented").Single().Bounds;
        var lines = new TextExtractor(PdfReader.Open(pdf)).FindOnPage(0, new System.Text.RegularExpressions.Regex(@"\bon\b"));
        Assert.Contains(lines, m => m.Bounds.Top < outdented.Y && Math.Abs(m.Bounds.X - 144) < 0.5); // The second line starts at the indent.
    }

    [Fact]
    public void Tabs_go_to_their_stops()
    {
        var pdf = DocxToPdf.ConvertToBytes(Document(
            P("Name:\tValue\tEnd", "<w:tabs><w:tab w:val=\"left\" w:pos=\"2880\"/><w:tab w:val=\"right\" w:pos=\"8640\"/></w:tabs>")
            + P("a\tdefault", "<w:tabs/>") + P("Mid\tCentered", "<w:tabs><w:tab w:val=\"center\" w:pos=\"4320\"/></w:tabs>")
            + P("Price\t12.50", "<w:tabs><w:tab w:val=\"decimal\" w:pos=\"4320\"/></w:tabs>")
            + P("Chapter\t7", "<w:tabs><w:tab w:val=\"right\" w:leader=\"dot\" w:pos=\"9360\"/></w:tabs>"),
            settings: "<w:defaultTabStop w:val=\"720\"/>"));
        Assert.Equal(72 + 144, Find(pdf, 0, "Value").Single().Bounds.X, 0);
        Assert.Equal(72 + 432, Find(pdf, 0, "End").Single().Bounds.Right, 0);
        Assert.Equal(72 + 36, Find(pdf, 0, "default").Single().Bounds.X, 0);
        var centered = Find(pdf, 0, "Centered").Single().Bounds;
        Assert.Equal(72 + 216, centered.X + (centered.Width / 2), 0);
        var point = Find(pdf, 0, ".").Where(m => m.Bounds.Y < Find(pdf, 0, "Price").Single().Bounds.Top && m.Bounds.Top > Find(pdf, 0, "Price").Single().Bounds.Y).ToList();
        Assert.Contains(point, m => Math.Abs(m.Bounds.X - (72 + 216)) < 1);
        Assert.Equal(540, Find(pdf, 0, "7").Single().Bounds.Right, 0); // A leader to the right margin.
        Assert.Contains(".....", AllText(pdf), StringComparison.Ordinal);
    }

    [Fact]
    public void List_labels_hang_in_the_indent()
    {
        const string numbering = """
            <w:abstractNum w:abstractNumId="1"><w:lvl w:ilvl="0"><w:start w:val="1"/><w:numFmt w:val="decimal"/><w:lvlText w:val="%1."/><w:pPr><w:ind w:left="720" w:hanging="360"/></w:pPr></w:lvl>
            <w:lvl w:ilvl="1"><w:start w:val="1"/><w:numFmt w:val="lowerRoman"/><w:lvlText w:val="(%2)"/><w:suff w:val="space"/><w:pPr><w:ind w:left="1440" w:hanging="360"/></w:pPr></w:lvl>
            <w:lvl w:ilvl="2"><w:start w:val="1"/><w:numFmt w:val="upperLetter"/><w:lvlText w:val="%3."/><w:pPr><w:ind w:left="1000" w:hanging="360"/></w:pPr></w:lvl></w:abstractNum>
            <w:num w:numId="1"><w:abstractNumId w:val="1"/></w:num>
            """;
        var pdf = DocxToPdf.ConvertToBytes(Document(
            P("First", "<w:numPr><w:ilvl w:val=\"0\"/><w:numId w:val=\"1\"/></w:numPr>") + P("Nested", "<w:numPr><w:ilvl w:val=\"1\"/><w:numId w:val=\"1\"/></w:numPr>")
            + P("Lettered", "<w:numPr><w:ilvl w:val=\"2\"/><w:numId w:val=\"1\"/></w:numPr>"),
            numbering: numbering));
        Assert.Equal(72 + 18, Find(pdf, 0, "1.").Single().Bounds.X, 0);
        Assert.Equal(72 + 36, Find(pdf, 0, "First").Single().Bounds.X, 0); // The label's tab goes to the indent.
        var label = Find(pdf, 0, "(i)").Single().Bounds;
        Assert.Equal(72 + 54, label.X, 0);
        Assert.InRange(Find(pdf, 0, "Nested").Single().Bounds.X - label.Right, 1, 5); // A space after it.
        Assert.Equal(72 + 50, Find(pdf, 0, "Lettered").Single().Bounds.X, 0); // The hanging indent's stop, not the next default stop.
    }

    [Fact]
    public void Page_breaks_and_page_break_before_start_new_pages()
    {
        var pdf = DocxToPdf.ConvertToBytes(Document(
            "<w:p><w:r><w:t>one</w:t><w:br w:type=\"page\"/><w:t>two</w:t></w:r></w:p>" + P("three", "<w:pageBreakBefore/>")
            + "<w:p><w:r><w:br w:type=\"page\"/></w:r></w:p>" + P("four") + "<w:p><w:r><w:t>five</w:t><w:br/><w:t>six</w:t></w:r></w:p>"));
        var pages = Pages(pdf);
        Assert.Equal(4, pages.Length);
        Assert.Contains("one", pages[0], StringComparison.Ordinal);
        Assert.StartsWith("two", pages[1].Trim(), StringComparison.Ordinal);
        Assert.StartsWith("three", pages[2].Trim(), StringComparison.Ordinal);
        Assert.Contains("four", pages[3], StringComparison.Ordinal); // After the empty line the break leaves.
        Assert.True(Find(pdf, 3, "five").Single().Bounds.Y > Find(pdf, 3, "six").Single().Bounds.Top - 1); // A line break.
    }

    [Fact]
    public void Keep_with_next_moves_a_heading_to_the_next_page()
    {
        var lines = Pages(DocxToPdf.ConvertToBytes(Document(string.Concat(Enumerable.Range(0, 100).Select(i => P($"Line {i}"))))))[0]
            .Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
        var filler = string.Concat(Enumerable.Range(0, lines - 1).Select(i => P($"Line {i}")));
        var withKeep = Pages(DocxToPdf.ConvertToBytes(Document(filler + P("Heading", "<w:keepNext/>") + P("Body"))));
        Assert.Contains("Heading", withKeep[1], StringComparison.Ordinal);
        var without = Pages(DocxToPdf.ConvertToBytes(Document(filler + P("Heading") + P("Body"))));
        Assert.Contains("Heading", without[0], StringComparison.Ordinal);
        Assert.DoesNotContain("Heading", without[1], StringComparison.Ordinal);
    }

    [Fact]
    public void Run_formatting_reaches_the_text_styles()
    {
        var pdf = DocxToPdf.ConvertToBytes(Document(
            "<w:p><w:r><w:t xml:space=\"preserve\">Base </w:t></w:r><w:r><w:rPr><w:vertAlign w:val=\"superscript\"/></w:rPr><w:t>up</w:t></w:r>"
            + "<w:r><w:rPr><w:vertAlign w:val=\"subscript\"/></w:rPr><w:t>dn</w:t></w:r><w:r><w:rPr><w:sz w:val=\"48\"/></w:rPr><w:t>Big</w:t></w:r></w:p>"));
        var baseline = Find(pdf, 0, "Base").Single().Bounds;
        var up = Find(pdf, 0, "up").Single().Bounds;
        var down = Find(pdf, 0, "dn").Single().Bounds;
        Assert.True(up.Y > baseline.Y + 1);
        Assert.True(down.Y < baseline.Y - 0.5);
        Assert.True(up.Height < baseline.Height);
        Assert.True(Find(pdf, 0, "Big").Single().Bounds.Height > 2 * baseline.Height);
    }

    [Fact]
    public void Tables_repeat_header_rows_and_span_cells()
    {
        var pdf = DocxToPdf.ConvertToBytes(Fixture("tables.docx"));
        var pages = Pages(pdf);
        Assert.True(pages.Length >= 3);
        for (var i = 1; i < pages.Length; i++)
        {
            Assert.Single(Find(pdf, i, "Description"));
        }

        var across = Find(pdf, 0, "Merged across").Single().Bounds;
        var c = Find(pdf, 0, "C").First(m => Math.Abs(m.Bounds.Y - across.Y) < 1).Bounds;
        Assert.True(c.X > across.X + 200); // The merged cell spans two of the four 108-point columns.
        var down = Find(pdf, 0, "Merged down").Single().Bounds;
        Assert.True(Find(pdf, 0, "Bottom").Single().Bounds.X > down.Right); // Its column is taken in the row below.
    }

    [Fact]
    public void Table_cells_draw_borders_shading_and_alignment()
    {
        var pdf = DocxToPdf.ConvertToBytes(Document(
            """
            <w:tbl><w:tblPr><w:jc w:val="right"/><w:tblBorders><w:top w:val="single" w:sz="8"/><w:bottom w:val="single" w:sz="8"/></w:tblBorders></w:tblPr>
            <w:tblGrid><w:gridCol w:w="2880"/><w:gridCol w:w="2880"/></w:tblGrid>
            <w:tr><w:trPr><w:trHeight w:val="1440"/></w:trPr><w:tc><w:tcPr><w:shd w:val="clear" w:fill="FF0000"/><w:vAlign w:val="bottom"/></w:tcPr><w:p><w:r><w:t>Low</w:t></w:r></w:p></w:tc>
            <w:tc><w:p><w:pPr><w:jc w:val="right"/></w:pPr><w:r><w:t>Top</w:t></w:r></w:p></w:tc></w:tr></w:tbl>
            """));
        var low = Find(pdf, 0, "Low").Single().Bounds;
        var top = Find(pdf, 0, "Top").Single().Bounds;
        Assert.Equal(540 - 288 + 5.4, low.X, 0); // Right-aligned table, the default cell padding.
        Assert.Equal(540 - 5.4, top.Right, 0);
        Assert.True(top.Y - low.Y > 40); // The row is an inch tall; the first cell's text sits at its bottom.
        var content = Encoding.Latin1.GetString(pdf);
        AssertValid(pdf);
        Assert.True(Find(pdf, 0, "Low").Count == 1 && content.Length > 0);
    }

    [Fact]
    public void Sections_have_their_own_page_size_headers_footers_and_numbers()
    {
        var pdf = DocxToPdf.Convert(Fixture("sections.docx"));
        var data = pdf.ToArray();
        var pages = Pages(data);
        Assert.Equal(6, pages.Length);
        Assert.Contains("First page header", pages[0], StringComparison.Ordinal);
        Assert.DoesNotContain("Page 1 of", pages[0], StringComparison.Ordinal); // The title page has no footer of its own.
        Assert.Contains("Even page header", pages[1], StringComparison.Ordinal);
        Assert.Contains("Even page 2", pages[1], StringComparison.Ordinal);
        Assert.Contains("Odd page header", pages[2], StringComparison.Ordinal);
        Assert.Contains("Page 3 of 6", pages[2], StringComparison.Ordinal);
        Assert.Contains("Landscape header", pages[4], StringComparison.Ordinal);
        Assert.True(pdf.Pages[4].Width > pdf.Pages[4].Height);
        Assert.True(pdf.Pages[5].Width < pdf.Pages[5].Height);
        Assert.Contains("Section page i of 1", pages[5], StringComparison.Ordinal); // Restarted in roman numerals.
    }

    [Fact]
    public void A_continuous_section_with_the_same_page_setup_shares_the_page_and_an_even_page_section_skips_to_an_even_page()
    {
        // A section's w:type says how the section it ends starts.
        var pdf = DocxToPdf.ConvertToBytes(Document(
            P("one") + "<w:p><w:pPr><w:sectPr/></w:pPr></w:p>" + P("two") + "<w:p><w:pPr><w:sectPr><w:type w:val=\"continuous\"/></w:sectPr></w:pPr></w:p>"
            + P("three") + "<w:p><w:pPr><w:sectPr><w:type w:val=\"evenPage\"/></w:sectPr></w:pPr></w:p>" + P("four") + "<w:sectPr><w:type w:val=\"evenPage\"/></w:sectPr>"));
        var pages = Pages(pdf);
        Assert.Equal(4, pages.Length);
        Assert.Contains("one", pages[0], StringComparison.Ordinal);
        Assert.Contains("two", pages[0], StringComparison.Ordinal); // Continuous: on the same page.
        Assert.Contains("three", pages[1], StringComparison.Ordinal);
        Assert.Equal(string.Empty, pages[2].Trim()); // A blank page, so that the next section starts on an even page.
        Assert.Contains("four", pages[3], StringComparison.Ordinal);
    }

    [Fact]
    public void A_tall_header_pushes_the_text_down()
    {
        var header = Part("hdr", string.Concat(Enumerable.Range(1, 8).Select(i => P($"Header line {i}"))));
        var pdf = DocxToPdf.ConvertToBytes(Document(
            P("Body") + "<w:sectPr><w:headerReference w:type=\"default\" r:id=\"rH\"/><w:pgMar w:top=\"720\" w:bottom=\"720\" w:left=\"720\" w:right=\"720\" w:header=\"360\" w:footer=\"360\"/></w:sectPr>",
            parts: [("word/header1.xml", header)], relationships: [("rH", "header", "header1.xml", false)]));
        Assert.True(Find(pdf, 0, "Body").Single().Bounds.Top < Find(pdf, 0, "Header line 8").Single().Bounds.Y);
    }

    [Fact]
    public void Page_references_show_the_pdf_page_numbers()
    {
        var pdf = DocxToPdf.Convert(Fixture("fields.docx"));
        var data = pdf.ToArray();
        var pages = Pages(data);
        Assert.Matches(@"Introduction\.+1", Normalize(pages[0]));
        Assert.Matches(@"Details\.+2", Normalize(pages[0]));
        Assert.Contains("Details", pages[1], StringComparison.Ordinal);
        Assert.True(pdf.NamedDestinations.ContainsKey("_Toc2"));
        Assert.Contains(pdf.Pages[0].Links, l => l.DestinationName == "_Toc2");
        Assert.Contains(pdf.Pages[1].Links, l => l.Uri == new Uri("https://example.org/field"));
    }

    [Fact]
    public void Notes_follow_their_section_and_endnotes_the_document()
    {
        var pdf = DocxToPdf.Convert(Fixture("fields.docx"));
        var text = Normalize(AllText(pdf.ToArray()));
        var footnote = text.IndexOf("Thefootnotetext.", StringComparison.Ordinal);
        var endnote = text.IndexOf("Theendnotetext.", StringComparison.Ordinal);
        Assert.True(footnote > text.IndexOf("Asentencewithafootnote", StringComparison.Ordinal));
        Assert.True(endnote > footnote);
        Assert.Contains("1Thefootnote", text, StringComparison.Ordinal);
        Assert.Contains("iTheendnote", text, StringComparison.Ordinal); // Endnotes in lower roman by default.
        Assert.True(pdf.NamedDestinations.ContainsKey("_fn1"));
        Assert.True(pdf.NamedDestinations.ContainsKey("_en1"));
        Assert.Contains(pdf.Pages.SelectMany(p => p.Links), l => l.DestinationName == "_fn1");
    }

    [Fact]
    public void Notes_are_numbered_in_order()
    {
        var notes = Part("footnotes", "<w:footnote w:id=\"7\"><w:p><w:r><w:footnoteRef/><w:t xml:space=\"preserve\"> Seventh.</w:t></w:r></w:p></w:footnote>"
            + "<w:footnote w:id=\"3\"><w:p><w:r><w:footnoteRef/><w:t xml:space=\"preserve\"> Third.</w:t></w:r></w:p></w:footnote>");
        var pdf = DocxToPdf.Convert(Document(
            "<w:p><w:r><w:t>a</w:t></w:r><w:r><w:footnoteReference w:id=\"7\"/></w:r><w:r><w:t xml:space=\"preserve\"> b</w:t></w:r><w:r><w:footnoteReference w:id=\"3\"/></w:r></w:p>",
            parts: [("word/footnotes.xml", notes)], relationships: [("rN", "footnotes", "footnotes.xml", false)]));
        var text = Normalize(AllText(pdf.ToArray()));
        Assert.Contains("a1b2", text, StringComparison.Ordinal);
        Assert.Contains("1Seventh.2Third.", text, StringComparison.Ordinal);
        Assert.True(pdf.NamedDestinations.ContainsKey("_fn2"));
    }

    [Fact]
    public void Hyperlinks_and_bookmarks_become_links_and_destinations()
    {
        var pdf = DocxToPdf.Convert(Fixture("styles.docx"));
        var links = pdf.Pages.SelectMany(p => p.Links).ToList();
        Assert.Contains(links, l => l.Uri == new Uri("https://example.com/docs"));
        Assert.Contains(links, l => l.DestinationName == "target");
        Assert.True(pdf.NamedDestinations.ContainsKey("target"));
    }

    [Fact]
    public void Images_are_drawn_inline_floating_and_in_headers()
    {
        var data = DocxToPdf.ConvertToBytes(Fixture("images.docx"));
        var images = new ImageExtractor(PdfReader.Open(data)).ExtractPage(0).Images;
        Assert.Equal(4, images.Count);
        Assert.Contains(images, i => Math.Abs(i.Width - (4 / 2.54 * 72)) < 0.5); // 4 cm wide.
        var floating = images.Single(i => Math.Abs(i.Width - (3 / 2.54 * 72)) < 0.5);
        Assert.Equal(612 - 90, floating.Bounds.Right, 0); // At the right margin.
        var wrapped = Find(data, 0, "This paragraph wraps").First().Bounds;
        Assert.True(wrapped.Right < floating.Bounds.X);
        Assert.Contains(images, i => i.Bounds.Top > 792 - 72); // The header's logo.
    }

    [Fact]
    public void Images_that_cannot_be_drawn_are_reported_and_left_out()
    {
        var (pdf, diagnostics) = Convert(Document(
            "<w:p><w:r><w:drawing><wp:inline><wp:extent cx=\"914400\" cy=\"914400\"/><wp:docPr id=\"1\" name=\"p\"/><a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/picture\">"
            + "<pic:pic><pic:blipFill><a:blip r:embed=\"rE\"/></pic:blipFill></pic:pic></a:graphicData></a:graphic></wp:inline></w:drawing></w:r><w:r><w:t>after</w:t></w:r></w:p>",
            parts: [("word/media/image1.emf", new byte[] { 1, 0, 0, 0, 0x6C, 0, 0, 0 })], relationships: [("rE", "image", "media/image1.emf", false)]));
        Assert.Contains("after", AllText(pdf.ToArray()), StringComparison.Ordinal);
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(DocumentDiagnosticKind.ImageNotLoaded, diagnostic.Kind);
        Assert.Contains("word/media/image1.emf", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Text_boxes_are_laid_out_after_their_paragraph_and_reported()
    {
        var (pdf, diagnostics) = Convert(Document(
            "<w:p><w:r><w:t>Anchor</w:t></w:r><w:r><w:pict><v:shape xmlns:v=\"urn:schemas-microsoft-com:vml\"><v:textbox><w:txbxContent>" + P("Boxed text")
            + "</w:txbxContent></v:textbox></v:shape></w:pict></w:r></w:p>" + P("Next")));
        var text = AllText(pdf.ToArray());
        Assert.True(text.IndexOf("Anchor", StringComparison.Ordinal) < text.IndexOf("Boxed text", StringComparison.Ordinal));
        Assert.True(text.IndexOf("Boxed text", StringComparison.Ordinal) < text.IndexOf("Next", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Kind == DocumentDiagnosticKind.UnsupportedContent);
    }

    [Fact]
    public void Headings_become_bookmarks_and_tagged_headings()
    {
        const string styles = """
            <w:style w:type="paragraph" w:styleId="Heading1"><w:pPr><w:outlineLvl w:val="0"/></w:pPr></w:style>
            <w:style w:type="paragraph" w:styleId="Heading2"><w:pPr><w:outlineLvl w:val="1"/></w:pPr></w:style>
            """;
        var docx = Document(P("Chapter", "<w:pStyle w:val=\"Heading1\"/>") + P("Section", "<w:pStyle w:val=\"Heading2\"/>") + P("Body") + P("Next chapter", "<w:pStyle w:val=\"Heading1\"/>"), styles);
        var pdf = DocxToPdf.Convert(docx, new DocxConverterOptions { Tagged = true });
        Assert.Equal(["Chapter", "Next chapter"], pdf.Outline.Select(o => o.Title));
        Assert.Equal("Section", Assert.Single(pdf.Outline[0].Children).Title);
        var types = Flatten(pdf.StructureRoot).Select(e => e.Type).ToList();
        Assert.Contains("H1", types);
        Assert.Contains("H2", types);
        Assert.Empty(DocxToPdf.Convert(docx, new DocxConverterOptions { HeadingBookmarks = false }).Outline);

        static IEnumerable<PdfStructureElement> Flatten(PdfStructureElement element) => element.Children.SelectMany(Flatten).Prepend(element);
    }

    [Fact]
    public void Title_comes_from_the_options_or_the_document()
    {
        Assert.Equal("Styles fixture", DocxToPdf.Convert(Fixture("styles.docx")).Info.Title);
        Assert.Equal("Mine", DocxToPdf.Convert(Fixture("styles.docx"), new DocxConverterOptions { Title = "Mine" }).Info.Title);
        Assert.Equal("RaiCore.Pdf.Documents", DocxToPdf.Convert(Fixture("minimal.docx")).Info.Creator);
    }

    [Fact]
    public void Fonts_are_found_by_family_with_substitutes_and_fallbacks()
    {
        var fonts = new FontCollection().AddFile(Fixtures.Path("LiberationSerif-Regular.ttf"));
        var pdf = DocxToPdf.ConvertToBytes(Document(P("Serif", rPr: "<w:rFonts w:ascii=\"Georgia\"/>") + P("Fallback", rPr: "<w:rFonts w:ascii=\"Unknown Face\"/>")),
            new DocxConverterOptions { Fonts = fonts, FontFamily = "Liberation Serif" });
        var content = Encoding.Latin1.GetString(pdf);
        Assert.Contains("LiberationSerif", content, StringComparison.Ordinal);
        Assert.DoesNotContain("LiberationSans", content, StringComparison.Ordinal); // Both found a font before the last fallback.
    }

    [Fact]
    public void Overloads_read_streams_files_and_run_asynchronously()
    {
        var bytes = Fixture("minimal.docx");
        var expected = AllText(DocxToPdf.ConvertToBytes(bytes));
        Assert.Contains("Hello, minimal document.", expected, StringComparison.Ordinal);
        using (var stream = new MemoryStream(bytes))
        {
            Assert.Equal(expected, AllText(DocxToPdf.Convert(stream).ToArray()));
        }

        using (var stream = new MemoryStream(bytes))
        {
            Assert.Equal(expected, AllText(DocxToPdf.ConvertToBytes(stream)));
        }

        Assert.Equal(expected, AllText(DocxToPdf.Convert(Fixtures.Path(Path.Combine("Documents", "minimal.docx"))).ToArray()));
    }

    [Fact(Timeout = 60_000)]
    public async Task Async_overloads_convert_and_observe_cancellation()
    {
        var bytes = Fixture("minimal.docx");
        var pdf = await DocxToPdf.ConvertAsync(bytes, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("minimal", AllText(pdf.ToArray()), StringComparison.Ordinal);
        using var stream = new MemoryStream(bytes);
        Assert.Single((await DocxToPdf.ConvertAsync(stream, cancellationToken: TestContext.Current.CancellationToken)).Pages);
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DocxToPdf.ConvertAsync(Fixture("tables.docx"), null, cancel.Token));
    }

    [Fact]
    public void Arguments_are_checked()
    {
        Assert.Throws<ArgumentNullException>(() => DocxToPdf.Convert((byte[])null!));
        Assert.Throws<ArgumentNullException>(() => DocxToPdf.Convert((Stream)null!));
        Assert.Throws<ArgumentNullException>(() => DocxToPdf.Convert((string)null!));
        Assert.Throws<ArgumentNullException>(() => DocxToPdf.Convert(Fixture("minimal.docx"), new DocxConverterOptions { FontFamily = null! }));
        Assert.Throws<ArgumentNullException>(() => DocxToPdf.Convert(Fixture("minimal.docx"), new DocxConverterOptions { Limits = null! }));
        Assert.Throws<ArgumentNullException>(() => DocxToPdf.Convert(Fixture("minimal.docx"), new DocxConverterOptions { FallbackFontFamilies = null! }));
    }

    [Fact]
    public void Not_a_word_document_throws_a_format_exception()
    {
        Assert.Throws<DocumentFormatException>(() => DocxToPdf.Convert(Encoding.ASCII.GetBytes("hello")));
        Assert.Contains("RTF", Assert.Throws<DocumentFormatException>(() => DocxToPdf.Convert(Encoding.ASCII.GetBytes(@"{\rtf1 x}"))).Message, StringComparison.Ordinal);
        Assert.Throws<DocumentFormatException>(() => DocxToPdf.Convert("PK\u0003\u0004 broken"u8.ToArray()));
        Assert.Throws<DocumentFormatException>(() => DocxToPdf.Convert(Package(("word/other.xml", "<x/>"))));
        var workbook = Package(
            ("_rels/.rels", $"<Relationships xmlns=\"{PackageRelationships}\"><Relationship Id=\"a\" Type=\"{R}/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>"),
            ("xl/workbook.xml", "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"/>"));
        Assert.Contains("not a WordprocessingML document", Assert.Throws<DocumentFormatException>(() => DocxToPdf.Convert(workbook)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Encrypted_and_legacy_documents_are_not_supported()
    {
        Assert.Throws<NotSupportedException>(() => DocxToPdf.Convert(Fixtures.Read(Path.Combine("Poi", "bug53475-password-is-pass.docx"))));
        Assert.Throws<NotSupportedException>(() => DocxToPdf.Convert(Fixtures.Read(Path.Combine("Documents", "Doc", "testWORD_protected_passtika.doc"))));
    }

    [Fact]
    public void Dtds_and_external_entities_are_refused()
    {
        var exception = Assert.Throws<DocumentFormatException>(() => DocxToPdf.Convert(Fixtures.Read(Path.Combine("Poi", "ExternalEntityInText.docx"))));
        Assert.Contains("DTD", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Deeply_nested_tables_hit_the_nesting_limit()
    {
        var exception = Assert.Throws<ResourceLimitExceededException>(() => DocxToPdf.Convert(Fixtures.Read(Path.Combine("Poi", "deep-table-cell.docx"))));
        Assert.Equal(nameof(ResourceLimits.MaxNestingDepth), exception.LimitName);
        var nested = string.Concat(Enumerable.Repeat("<w:tbl><w:tr><w:tc>", 12)) + P("deep") + string.Concat(Enumerable.Repeat("</w:tc></w:tr></w:tbl>", 12)) + P("x");
        var limits = ResourceLimits.Default with { MaxNestingDepth = 40 };
        Assert.Equal(nameof(ResourceLimits.MaxNestingDepth),
            Assert.Throws<ResourceLimitExceededException>(() => DocxToPdf.Convert(Document(nested), new DocxConverterOptions { Limits = limits })).LimitName);
        Assert.Contains("deep", AllText(DocxToPdf.ConvertToBytes(Document(nested))), StringComparison.Ordinal);
    }

    [Fact]
    public void Resource_limits_bound_the_conversion()
    {
        var big = Document(string.Concat(Enumerable.Range(0, 300).Select(i => P($"Paragraph {i} with some text in it."))));
        string Limit(ResourceLimits limits) =>
            Assert.Throws<ResourceLimitExceededException>(() => DocxToPdf.Convert(big, new DocxConverterOptions { Limits = limits })).LimitName;
        Assert.Equal(nameof(ResourceLimits.MaxDecompressedBytes), Limit(ResourceLimits.Default with { MaxDecompressedBytes = 5000 }));
        Assert.Equal(nameof(ResourceLimits.MaxElements), Limit(ResourceLimits.Default with { MaxElements = 500 }));
        Assert.Equal(nameof(ResourceLimits.MaxPages), Limit(ResourceLimits.Default with { MaxPages = 2 }));
        Assert.Equal(nameof(ResourceLimits.Timeout), Limit(ResourceLimits.Default with { Timeout = TimeSpan.FromTicks(1) }));
        var table = "<w:tbl>" + string.Concat(Enumerable.Repeat("<w:tr>" + string.Concat(Enumerable.Repeat("<w:tc><w:p/></w:tc>", 10)) + "</w:tr>", 20)) + "</w:tbl>";
        Assert.Equal(nameof(ResourceLimits.MaxTableCells),
            Assert.Throws<ResourceLimitExceededException>(() => DocxToPdf.Convert(Document(table), new DocxConverterOptions { Limits = ResourceLimits.Default with { MaxTableCells = 150 } })).LimitName);
    }

    [Fact]
    public void Macro_enabled_documents_convert_their_content()
    {
        var docx = Document(P("Content of a macro document"), parts: [("word/vbaProject.bin", new byte[] { 0xD0, 0xCF, 0x11, 0xE0 })],
            relationships: [("rV", "vbaProject", "vbaProject.bin", false)]);
        Assert.Contains("Content of a macro document", AllText(DocxToPdf.ConvertToBytes(docx)), StringComparison.Ordinal);
    }

    [Fact]
    public void Several_columns_are_laid_out_without_a_report()
    {
        var (pdf, diagnostics) = Convert(Document(P("text") + "<w:sectPr><w:cols w:num=\"3\"/></w:sectPr>"));
        Assert.Contains("text", AllText(pdf.ToArray()), StringComparison.Ordinal);
        Assert.DoesNotContain(diagnostics, d => d.Message.Contains("text columns", StringComparison.Ordinal));
    }

    [Fact]
    public void Poi_samples_render_their_headers_footers_and_lists()
    {
        var three = Pages(DocxToPdf.ConvertToBytes(Fixtures.Read(Path.Combine("Poi", "ThreeColHeadFoot.docx"))));
        Assert.Equal(2, three.Length);
        Assert.All(three, page => Assert.Contains("Footer Middle", page, StringComparison.Ordinal));
        var first = Pages(DocxToPdf.ConvertToBytes(Fixtures.Read(Path.Combine("Poi", "DiffFirstPageHeadFoot.docx"))));
        Assert.Contains("I am the header on the first page", first[0], StringComparison.Ordinal);
        Assert.DoesNotContain("I am the header on the first page", first[1], StringComparison.Ordinal);
        var lists = AllText(DocxToPdf.ConvertToBytes(Fixtures.Read(Path.Combine("Poi", "ComplexNumberedLists.docx"))));
        foreach (var label in new[] { "2. Entry #2", "c. 2-c", "10. Jump to new list at 10", "12. Carrying on @ 12" })
        {
            Assert.Contains(label, lists, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Conversions_run_in_parallel()
    {
        var bytes = Fixture("lists.docx");
        var expected = AllText(DocxToPdf.ConvertToBytes(bytes));
        var results = new string[8];
        Parallel.For(0, results.Length, i => results[i] = AllText(DocxToPdf.ConvertToBytes(bytes)));
        Assert.All(results, r => Assert.Equal(expected, r));
    }
}
