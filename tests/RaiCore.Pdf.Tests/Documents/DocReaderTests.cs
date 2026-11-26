using RaiCore.Pdf.Documents;
using RaiCore.Pdf.Documents.Doc;
using RaiCore.Pdf.Documents.Model;
using static RaiCore.Pdf.Tests.WordDocuments.Doc;

namespace RaiCore.Pdf.Tests.WordDocuments;

/// <summary>
/// Reading Word 97-2003 documents into the model: formatting, styles, lists, tables, sections, headers and footers,
/// notes, fields, pictures and text boxes of the samples, the property modifiers, limits, and damaged input.
/// </summary>
public class DocReaderTests
{
    private static List<WordParagraph> All(IEnumerable<WordBlock> blocks) => [.. AllParagraphs(blocks)];

    private static List<WordInline> AllInlines(WordDocument document) =>
        [.. document.Sections.SelectMany(s => AllParagraphs(s.Blocks)).SelectMany(p => Inlines(p.Inlines))];

    [Fact]
    public void Runs_have_their_character_formatting()
    {
        var document = ReadSample("test2.doc");
        Assert.True(Run(document, "bold").Format.Bold);
        Assert.False(Run(document, "bold").Format.Italic);
        Assert.True(Run(document, "italic").Format.Italic);
        Assert.NotEqual(UnderlineStyle.None, Run(document, "underlined").Format.Underline);
        Assert.Equal(UnderlineStyle.None, Run(document, "This is a simple").Format.Underline);
        var red = Run(document, "red").Format.Color;
        Assert.NotNull(red);
        Assert.True(red.Value.Red > 0.9 && red.Value.Green < 0.1 && red.Value.Blue < 0.1);
        var plain = Run(document, "This is a simple").Format;
        var different = Run(document, "different font").Format;
        Assert.NotEqual(plain.FontFamily, different.FontFamily);
        Assert.NotEqual(plain.FontSize, different.FontSize);
        Assert.Equal("Times New Roman", plain.FontFamily);
        Assert.Equal(12, plain.FontSize);
    }

    [Fact]
    public void Nested_runs_keep_each_property()
    {
        // Tika WordParserTest.java lines 239-243: "ita<s>li</s>c", "<sup>superscript</sup>", "<sub>subscript".
        var document = ReadSample("testWORD_various.doc");
        Assert.Equal(VerticalPosition.Superscript, Run(document, "superscript").Format.Position);
        Assert.Equal(VerticalPosition.Subscript, Run(document, "subscript").Format.Position);
        var struck = Paragraphs(document).SelectMany(Runs).Where(r => r.Text == "li").ToList();
        Assert.NotEmpty(struck);
        Assert.All(struck, r => Assert.True(r.Format.Strike && r.Format.Italic));
        Assert.True(Run(document, "Bold").Format.Bold);
    }

    [Fact]
    public void Styles_give_headings_and_their_formatting()
    {
        var document = ReadSample("testWORD.doc");
        Assert.Equal(0, Paragraph(document, "Heading Level 1").Format.OutlineLevel);
        Assert.Equal(1, Paragraph(document, "Heading Level 2").Format.OutlineLevel);
        Assert.Equal(2, Paragraph(document, "Heading Level 3").Format.OutlineLevel);
        Assert.Null(Paragraph(document, "This is a sample Microsoft Word Document").Format.OutlineLevel);
        Assert.True(Run(document, "Heading Level 1").Format.Bold);
        Assert.True(Run(document, "Heading Level 2").Format.Italic);
        Assert.True(Run(document, "BOLD").Format.Bold);
        Assert.True(Run(document, "ITALIC").Format.Italic);
        Assert.Equal(WordAlignment.Center, Paragraph(document, "Sample Word Document Title").Format.Alignment);
        Assert.Equal(WordAlignment.Start, Paragraph(document, "This is a sample Microsoft").Format.Alignment);
    }

    [Fact]
    public void Tables_nest_and_merge()
    {
        var document = ReadSample("testWORD.doc");
        var table = Assert.Single(document.Sections.SelectMany(s => s.Blocks).OfType<WordTable>());
        Assert.Equal(3, table.Rows.Count);
        Assert.Equal("This is a table", Text(table.Rows[0].Cells[0].Blocks));
        var nested = table.Rows.SelectMany(r => r.Cells).SelectMany(c => c.Blocks).OfType<WordTable>().Single();
        Assert.Equal("Nested table", Text(nested.Rows[0].Cells[0].Blocks));
        Assert.Contains(nested.Rows.SelectMany(r => r.Cells), c => Text(c.Blocks) == "More of our nested table");
        Assert.All(table.Rows, r => Assert.Equal(2, r.Cells.Count));
        Assert.True(table.Grid.Sum() > 300);
        Assert.True(table.Rows[0].Cells[0].Borders.Top is { IsVisible: true } || table.Borders.Top is { IsVisible: true });

        // POI TestWordToHtmlConverter.java line 105: cells spanning 3 and 2 columns.
        var merged = ReadSample("table-merges.doc").Sections.SelectMany(s => s.Blocks).OfType<WordTable>().SelectMany(t => t.Rows).SelectMany(r => r.Cells).ToList();
        Assert.Contains(merged, c => c.ColumnSpan == 3);
        Assert.Contains(merged, c => c.ColumnSpan == 2);
    }

    [Fact]
    public void Table_rows_have_their_cells_in_order()
    {
        // Tika WordParserTest.java line 287: the cells of the first table in reading order.
        var document = ReadSample("testWORD_various.doc");
        var table = document.Sections.SelectMany(s => s.Blocks).OfType<WordTable>().First();
        Assert.Equal(2, table.Rows.Count);
        Assert.Equal(["Row 1 Col 1", "Row 1 Col 2", "Row 1 Col 3"], table.Rows[0].Cells.Select(c => Text(c.Blocks)));
        Assert.Equal(3, table.Grid.Count);
        Assert.Equal([0, 1, 2], table.Rows[1].Cells.Select(c => c.Column));
    }

    [Fact]
    public void Lists_are_counted_and_indented_by_level()
    {
        var document = ReadSample("Lists.doc");
        Assert.Equal("\u2022", Paragraph(document, "Unordered list 1").Label?.Text);
        Assert.NotNull(Paragraph(document, "Tick 1").Label);
        var first = Paragraph(document, "ML 1:1");
        var second = Paragraph(document, "ML 2:1");
        var fifth = Paragraph(document, "ML 5:1");
        Assert.True(second.Format.IndentStart > first.Format.IndentStart);
        Assert.True(fifth.Format.IndentStart > second.Format.IndentStart);
        var ordered = new[] { "Ordered list 1", "OL 2", "OL 3" }.Select(t => Paragraph(document, t).Label?.Text).ToList();
        Assert.Equal(3, ordered.Distinct().Count());
        Assert.StartsWith("1", ordered[0]);
        Assert.StartsWith("2", ordered[1]);
        Assert.Null(Paragraph(document, "This document has different lists").Label);

        // Tika WordParserTest.java lines 481-484: levels a) and i) under 1).
        var numbered = ReadSample("testWORD_numbered_list.doc");
        Assert.Equal("1)", Paragraph(numbered, "This").Label?.Text);
        Assert.Equal("a)", Paragraph(numbered, "Is").Label?.Text);
        Assert.Equal("i)", Paragraph(numbered, "A multi").Label?.Text);
        Assert.Equal(LabelSuffix.Tab, Paragraph(numbered, "A multi").Label?.Suffix);
    }

    [Fact]
    public void Sections_have_their_page_setup_headers_and_footers()
    {
        var document = ReadSample("DiffFirstPageHeadFoot.doc");
        var section = Assert.Single(document.Sections);
        Assert.True(section.TitlePage);
        Assert.Contains("I am the header on the first page", Text(section.Headers[HeaderFooterKind.First]), StringComparison.Ordinal);
        Assert.Contains("First header column!", Text(section.Headers[HeaderFooterKind.Default]), StringComparison.Ordinal);
        Assert.Contains("The footer of the first page", Text(section.Footers[HeaderFooterKind.First]), StringComparison.Ordinal);
        Assert.Equal("Footer Left\tFooter Middle Footer Right", Text(section.Footers[HeaderFooterKind.Default]));
        Assert.False(section.Headers.ContainsKey(HeaderFooterKind.Even));
        Assert.InRange(section.PageWidth, 590, 615);
        Assert.InRange(section.PageHeight, 790, 845);
        Assert.InRange(section.MarginLeft, 36, 100);

        // Even pages with their own headers (tables in them), and the extra paragraph mark of each story left out.
        var oddEven = ReadSample("PageSpecificHeadFoot.doc");
        Assert.True(oddEven.EvenAndOddHeaders);
        var even = oddEven.Sections[0].Headers[HeaderFooterKind.Even];
        Assert.Contains("[This is an Even Page, with a Header]", Text(even.OfType<WordTable>().Single().Rows[0].Cells[0].Blocks), StringComparison.Ordinal);
        Assert.Contains("[ODD Page Header text]", string.Join(" ", Docx.Paragraphs(oddEven.Sections[0].Headers[HeaderFooterKind.Default]).Select(Text)), StringComparison.Ordinal);
        Assert.False(ReadSample("ThreeColHeadFoot.doc").EvenAndOddHeaders);
        Assert.Single(ReadSample("ThreeColHeadFoot.doc").Sections[0].Headers[HeaderFooterKind.Default]);
    }

    [Fact]
    public void Section_breaks_start_new_sections()
    {
        // Three sections in the main text (PlcfSed's fourth starts after it), the second in two columns; the last two
        // start on the same page.
        var diagnostics = new List<DocumentDiagnostic>();
        var document = ReadSample("testWORD_various.doc", diagnostics);
        Assert.Equal(3, document.Sections.Count);
        Assert.Equal([1, 2, 1], document.Sections.Select(s => s.Columns.Count));
        Assert.Equal(SectionStart.NextPage, document.Sections[0].Start);
        Assert.All(document.Sections.Skip(1), s => Assert.Equal(SectionStart.Continuous, s.Start));
        Assert.DoesNotContain(diagnostics, d => d.Message.Contains("text columns", StringComparison.Ordinal)); // Laid out in columns.
        Assert.Contains("Row 1 column 1", Text(document.Sections[1].Blocks), StringComparison.Ordinal);

        // A page narrower than its margins (sprmSXaPage 3207 twips, A4 height) is as wide as A4.
        var narrow = ReadSample("Bug48075.doc").Sections[0];
        Assert.Equal(595.3, narrow.PageWidth, 1);
        Assert.Equal(841.9, narrow.PageHeight, 1);
        Assert.Equal(70.9, narrow.MarginLeft, 1);
        Assert.Equal(28.35, narrow.MarginRight, 1);
    }

    [Fact]
    public void Notes_are_linked_to_their_references()
    {
        var diagnostics = new List<DocumentDiagnostic>();
        var document = ReadSample("footnote.doc", diagnostics);
        var references = AllInlines(document).OfType<NoteReference>().ToList();
        Assert.Contains(references, r => r.Kind == NoteKind.Footnote && Text(document.Footnotes[r.Id]).Contains("TestFootnote", StringComparison.Ordinal));
        Assert.Contains(references, r => r.Kind == NoteKind.Endnote && Text(document.Endnotes[r.Id]).Contains("TestEndnote", StringComparison.Ordinal));
        Assert.All(document.Footnotes.Values.Concat(document.Endnotes.Values), n => Assert.IsType<NoteMark>(Docx.Paragraphs(n).First().Inlines[0]));
        Assert.DoesNotContain(document.Sections.SelectMany(s => Docx.Paragraphs(s.Blocks)), p => Text(p).Contains("TestComment", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Message.Contains("Comments", StringComparison.Ordinal));

        var various = ReadSample("testWORD_various.doc");
        Assert.Equal("This is a footnote.", Text(various.Footnotes["f0"]).Trim());
    }

    [Fact]
    public void Fields_become_links_page_numbers_and_their_results()
    {
        var document = ReadSample("testWORD.doc");
        var links = AllInlines(document).OfType<HyperlinkInline>().ToList();
        Assert.Contains(links, l => l.Uri == new Uri("http://tika.apache.org/") && Docx.Text(new WordParagraph { Inlines = { l } }) == "Tika");
        Assert.DoesNotContain(Paragraphs(document), p => Text(p).Contains("HYPERLINK", StringComparison.Ordinal));

        // Links in a header (Tika WordParserTest.java lines 427-430).
        var header = ReadSample("testWORD_header_hyperlink.doc").Sections[0].Headers.Values.SelectMany(All).SelectMany(p => Inlines(p.Inlines)).OfType<HyperlinkInline>().ToList();
        Assert.Contains(header, l => l.Uri == new Uri("mailto:ab@example.com"));
        Assert.Contains(header, l => l.Uri == new Uri("http://tw-systemhaus.de"));

        // Page numbers in a footer.
        var footers = ReadSample("testWORD_numbered_list.doc").Sections.SelectMany(s => s.Footers.Values.Concat(s.Headers.Values)).SelectMany(All).SelectMany(p => p.Inlines);
        var various = ReadSample("testWORD.doc").Sections.SelectMany(s => s.Footers.Values).SelectMany(All).SelectMany(p => p.Inlines);
        Assert.DoesNotContain(footers.Concat(various).OfType<TextRun>(), r => r.Text.Contains("PAGE", StringComparison.Ordinal));
    }

    [Fact]
    public void Pictures_are_inline_or_floating()
    {
        // POI TestPictures.java line 346: one PNG picture.
        var png = AllInlines(ReadSample("PngPicture.doc")).OfType<ImageInline>().Single();
        Assert.True(png.Data.AsSpan().StartsWith((ReadOnlySpan<byte>)[0x89, (byte)'P', (byte)'N', (byte)'G']));
        Assert.True(png.Width > 10 && png.Height > 10);

        // Tika WordParserTest.java lines 136-139: a PNG, a JPEG and a PNG.
        var images = AllInlines(ReadSample("testWORD_3imgs.doc")).OfType<ImageInline>().ToList();
        Assert.Equal(3, images.Count);
        Assert.Equal(2, images.Count(i => i.Data.AsSpan().StartsWith((ReadOnlySpan<byte>)[0x89, (byte)'P'])));
        Assert.Single(images, i => i.Data.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8]));
        Assert.Contains(images, i => i.Placement == ImagePlacement.Float);
        Assert.All(images, i => Assert.True(i.Width is > 10 and < 600 && i.Height is > 10 and < 800));
    }

    [Fact]
    public void Text_boxes_follow_their_paragraph()
    {
        var diagnostics = new List<DocumentDiagnostic>();
        var document = ReadSample("testWORD_various.doc", diagnostics);
        var boxes = Paragraphs(document).SelectMany(p => p.Inlines).OfType<AnchoredBlocks>().ToList();
        Assert.Contains(boxes, b => Text(b.Blocks).Contains("Here is a text box", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Message.Contains("text box", StringComparison.Ordinal));
    }

    [Fact]
    public void Text_is_read_from_unicode_and_fast_saved_pieces()
    {
        // POI TestWordExtractor.java lines 200, 223, 323 and 335.
        var unicode = ReadSample("HeaderFooterUnicode.doc").Sections[0];
        Assert.Contains("\u20AC euro symbol", Text(unicode.Headers[HeaderFooterKind.Default]), StringComparison.Ordinal);
        Assert.Contains("Moli\u00E8re", Text(unicode.Footers[HeaderFooterKind.Default]), StringComparison.Ordinal);
        Assert.Contains(Paragraphs(ReadSample("rasp.doc")), p => Text(p).Contains("\u0425\u0425\u0425\u0425\u0425", StringComparison.Ordinal));
        Assert.StartsWith("\u041F\u0440\u0438\u043B\u043E\u0436\u0435\u043D\u0438\u0435", Paragraphs(ReadSample("Bug48075.doc")).Select(Text).First(t => t.Trim().Length > 0).Trim(), StringComparison.Ordinal);
        Assert.Contains(Paragraphs(ReadSample("testWORD_various.doc")), p => Text(p).Contains("\u30BE\u30EB\u30B2\u3068\u5C3E\u5D0E", StringComparison.Ordinal));
    }

    [Fact]
    public void Capitals_are_formatting()
    {
        // POI TestWordExtractor.java line 421: "CAPITALIZED" is stored in other case and shown in capitals.
        var run = Run(ReadSample("capitalized.doc"), "apitalized");
        Assert.True(run.Format.Caps);
    }

    [Fact]
    public void Summary_information_gives_title_and_author()
    {
        var document = ReadSample("testWORD.doc");
        Assert.Equal("Sample Word Document", document.Title);
        Assert.Equal("Keith Bennett", document.Author);
        Assert.Equal("\u041A\u0440\u0435\u0434\u0438\u0442\u043E\u0432\u0430\u043D\u0438\u0435", ReadSample("Bug48075.doc").Title?[..12]);
    }
    [Fact]
    public void Paragraph_properties_come_from_their_pages_and_styles()
    {
        // A PAPX of cb words holds 2cb - 1 bytes after its count ([MS-DOC] 2.9.179): odd lengths.
        var file = DocFile.Open(Sample("testWORD.doc"), new ConversionContext(ResourceLimits.Default, null, CancellationToken.None));
        Assert.Contains(file.ParagraphRuns, r => r.Length % 2 == 1);
        Assert.All(file.ParagraphRuns, r => Assert.True(r.FcEnd > r.FcStart));

        // A style with a base other than Normal inherits its base's properties (an OpenOffice "List" style on "Text
        // body", whose own UPX holds only its style number).
        var styles = DocStyles.Read(DocFile.Open(Sample("PngPicture.doc"), new ConversionContext(ResourceLimits.Default, null, CancellationToken.None)));
        Assert.Equal("List", styles.Name(18));
        Assert.True(styles.Paragraph(17).Pap.After > 0);
        Assert.Equal(styles.Paragraph(17).Pap.After, styles.Paragraph(18).Pap.After);

        // Word 2007's Normal style: 1.15 lines, 10 points after; Heading 1 (a UPX of odd size before its character
        // properties) bold and larger.
        var document = ReadSample("Lists.doc");
        var body = Paragraph(document, "This document has different lists");
        Assert.Equal((1.15, LineSpacingRule.Multiple, 10.0), (body.Format.LineSpacing, body.Format.LineRule, body.Format.SpaceAfter));
        var heading = Run(document, "Heading Level 1");
        Assert.True(heading.Format.Bold);
        Assert.True(heading.Format.FontSize > 12);
        Assert.Equal(1.5, Paragraph(ReadSample("rasp.doc"), "\u041B\u0438\u0446\u0435\u0432\u043E\u0439").Format.LineSpacing);
    }

    [Fact]
    public void Page_numbers_in_footers_are_computed()
    {
        var footer = ReadSample("PageSpecificHeadFoot.doc").Sections[0].Footers.Values.SelectMany(All).SelectMany(p => Inlines(p.Inlines));
        var field = footer.OfType<FieldInline>().First();
        Assert.Equal(FieldKind.Page, field.Kind);
        Assert.Equal("2", field.CachedResult);
    }

    [Fact]
    public void Pictures_of_several_formats_are_read()
    {
        // POI TestBugs.java line 566: a PNG and two bitmaps (the other pictures cannot be drawn).
        var diagnostics = new List<DocumentDiagnostic>();
        var images = AllInlines(ReadSample("Bug51890.doc", diagnostics)).OfType<ImageInline>().ToList();
        Assert.Single(images, i => i.Data.AsSpan().StartsWith((ReadOnlySpan<byte>)[0x89, (byte)'P', (byte)'N', (byte)'G']));
        Assert.Equal(2, images.Count(i => i.Data.AsSpan().StartsWith("BM"u8)));
        Assert.Contains(diagnostics, d => d.Kind == DocumentDiagnosticKind.ImageNotLoaded);
    }

    [Fact]
    public void Horizontally_merged_cells_join_the_cell_before()
    {
        // Word 97's merged cells (fFirstMerged, fMerged): the merged cell's edge extends the first one.
        var definition = new DocRowDefinition { Left = 0 };
        definition.Cells.Add(new DocCellDefinition { Right = 1000, HorizontalMerge = 1 });
        definition.Cells.Add(new DocCellDefinition { Right = 2000, HorizontalMerge = 2 });
        definition.Cells.Add(new DocCellDefinition { Right = 3000 });
        var builder = new DocReader.TableBuilder();
        builder.Rows.Add((definition, [[new WordParagraph()], [new WordParagraph()], [new WordParagraph()]]));
        var row = Assert.Single(DocReader.BuildTable(builder).Rows);
        Assert.Equal(2, row.Cells.Count);
        Assert.Equal((0, 2), (row.Cells[0].Column, row.Cells[0].ColumnSpan));
        Assert.Equal(2, row.Cells[1].Column);
    }

    [Fact]
    public void Surrogates_without_their_other_half_are_replacement_characters()
    {
        // "subscript" in UTF-16 in testWORD_various.doc, its first letter made a lone high surrogate.
        var data = Sample("testWORD_various.doc");
        var at = data.AsSpan().IndexOf(System.Text.Encoding.Unicode.GetBytes("subscript"));
        data[at] = 0x00;
        data[at + 1] = 0xD8;
        Assert.Contains(Paragraphs(Read(data)).SelectMany(Runs), r => r.Text.Contains("\uFFFDubscript", StringComparison.Ordinal));

        // List label texts and the summary information are cleaned the same way.
        Assert.Equal("a\uFFFDb\uD83D\uDE00\uFFFD", DocFile.WithoutLoneSurrogates("a\uDC00b\uD83D\uDE00\uD800"));
    }
}
