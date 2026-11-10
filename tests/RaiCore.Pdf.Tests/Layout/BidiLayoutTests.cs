using PDFtoImage;
using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Layout;
using RaiCore.Pdf.Layout.Blocks;
using RaiCore.Pdf.Layout.Text;
using SkiaSharp;

namespace RaiCore.Pdf.Tests.Layout;

/// <summary>Mixed-direction text in paragraphs (the Unicode bidi algorithm applied to lines).</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
public class BidiLayoutTests
{
    private const string Alef = "\u05D0";
    private const string Bet = "\u05D1";
    private const string Gimel = "\u05D2";

    private static readonly PdfFont Sans = PdfFont.Load(Fixtures.Path("LiberationSans-Regular.ttf"));
    private static readonly PdfFont Arabic = PdfFont.Load(Fixtures.Path("NotoSansArabic-Subset.ttf"));
    private static readonly TextStyle Text = new(Sans, 10) { LineHeight = LineHeight.Points(12) };

    [Fact]
    public void Right_to_left_words_in_left_to_right_text_are_reversed_in_place()
    {
        var line = Single(new Paragraph(Text).Add($"abc {Alef}{Bet}{Gimel} def"));

        Assert.Equal(["abc ", Gimel + Bet + Alef, " def"], VisualTexts(line));
        Assert.Equal(0, line.X); // Left-aligned (start of a left-to-right paragraph).
    }

    [Fact]
    public void A_right_to_left_paragraph_runs_from_the_right_and_keeps_numbers_and_latin_left_to_right()
    {
        var line = Single(new Paragraph(Text) { Direction = ParagraphDirection.RightToLeft }.Add($"{Alef}{Bet} abc 123 {Gimel}"));

        // Digits after Latin letters count as Latin (rule W7): "abc 123" is one left-to-right run.
        Assert.Equal([Gimel + " ", "abc 123", " " + Bet + Alef], VisualTexts(line));
        Assert.Equal(300 - line.Width, line.X, 6); // Start-aligned: on the right.
    }

    [Theory]
    [InlineData(TextAlignment.End, ParagraphDirection.RightToLeft, false)]
    [InlineData(TextAlignment.End, ParagraphDirection.LeftToRight, true)]
    [InlineData(TextAlignment.Left, ParagraphDirection.RightToLeft, false)]
    [InlineData(TextAlignment.Right, ParagraphDirection.LeftToRight, true)]
    [InlineData(TextAlignment.Start, ParagraphDirection.Auto, true)] // Auto: the first strong character is Hebrew.
    public void Start_and_end_follow_the_direction(TextAlignment alignment, ParagraphDirection direction, bool right)
    {
        var line = Single(new Paragraph(Text) { Alignment = alignment, Direction = direction }.Add($"{Alef}{Bet} ab"));

        Assert.Equal(right ? 300 - line.Width : 0, line.X, 6);
    }

    [Fact]
    public void The_last_line_of_justified_right_to_left_text_is_on_the_right()
    {
        var words = string.Join(' ', Enumerable.Repeat($"{Alef}{Bet}{Gimel}", 30));
        var layout = new Paragraph(Text) { Alignment = TextAlignment.Justify, Direction = ParagraphDirection.RightToLeft }.Add(words).Layout(200);

        Assert.True(layout.Lines.Count > 1);
        Assert.Equal(200, layout.Lines[0].X + layout.Lines[0].Width, 3); // Justified to both edges.
        var last = layout.Lines[^1];
        Assert.True(last.X > 1, "the last line is not justified");
        Assert.Equal(200, last.X + last.Width, 3); // But aligned to the start: the right.
    }

    [Fact]
    public void The_first_line_indent_is_on_the_right_of_right_to_left_text()
    {
        var layout = new Paragraph(Text) { Direction = ParagraphDirection.RightToLeft, FirstLineIndent = 20 }.Add($"{Alef}{Bet}").Layout(300);

        Assert.Equal(280, layout.Lines[0].X + layout.Lines[0].Width, 6);
    }

    [Fact]
    public void Lines_are_broken_in_logical_order_and_each_reordered()
    {
        // Each line holds the next words in reading order: the first line has the first Hebrew words, on its right.
        var hebrew = string.Join(' ', Enumerable.Range(0, 12).Select(i => i % 2 == 0 ? $"{Alef}{Bet}" : $"{Gimel}{Gimel}"));
        var layout = new Paragraph(Text) { Direction = ParagraphDirection.RightToLeft }.Add(hebrew).Layout(60);

        Assert.True(layout.Lines.Count > 1);
        var first = layout.Lines[0];
        var rightmost = first.Fragments.MaxBy(f => f.X)!;
        Assert.EndsWith(Bet + Alef, GlyphText(rightmost), StringComparison.Ordinal); // The paragraph's first word.
        Assert.Equal(0, first.Start);
    }

    [Fact]
    public void Arabic_numbers_and_letters_join_and_reorder()
    {
        // Arabic letters with Arabic-Indic digits: the digits read left to right inside the right-to-left text.
        var line = Single(new Paragraph(new TextStyle(Arabic, 12)) { Direction = ParagraphDirection.RightToLeft }.Add("\u0633\u0644\u0627\u0645 \u0661\u0662"));

        var visual = line.Fragments.OrderBy(f => f.X).ToList();
        Assert.Equal(("\u0661\u0662", TextDirection.LeftToRight), (GlyphText(visual[0]), visual[0].Run.Direction)); // On the left, in its own order.
        Assert.Equal(TextDirection.RightToLeft, visual[^1].Run.Direction);
        Assert.Equal("Arab", visual[^1].Run.Script);
    }

    [Fact]
    public void Brackets_are_mirrored_in_right_to_left_runs()
    {
        var mirrored = Single(new Paragraph(Text) { Direction = ParagraphDirection.RightToLeft }.Add($"{Alef}({Bet})"));
        var plain = Single(new Paragraph(Text).Add("a(b)"));
        var openGlyph = plain.Fragments.SelectMany(f => f.Glyphs).Single(g => g.Text == "(").GlyphId;
        var closeGlyph = plain.Fragments.SelectMany(f => f.Glyphs).Single(g => g.Text == ")").GlyphId;

        // Shown from the left: ")" drawn as "(" ... i.e. the opening bracket's character uses the closing glyph.
        var glyphs = mirrored.Fragments.OrderBy(f => f.X).SelectMany(f => f.Glyphs).ToList();
        Assert.Equal(closeGlyph, glyphs.Single(g => g.Text == "(").GlyphId);
        Assert.Equal(openGlyph, glyphs.Single(g => g.Text == ")").GlyphId);
    }

    [Fact]
    public void Explicit_controls_override_and_are_not_drawn()
    {
        // RLO ... PDF shows "abc" right to left, inside left-to-right text.
        var line = Single(new Paragraph(Text).Add("x \u202Eabc\u202C y"));

        Assert.Equal(["x ", "cba", " y"], VisualTexts(line));
        Assert.DoesNotContain(line.Fragments.SelectMany(f => f.Glyphs), g => g.Text is "\u202E" or "\u202C");
    }

    [Fact]
    public void Isolates_keep_their_content_together()
    {
        // Without the isolate, "abc" would join the Hebrew around it in the right-to-left paragraph's order.
        var isolated = Single(new Paragraph(Text) { Direction = ParagraphDirection.RightToLeft }.Add($"{Alef} \u2066abc {Bet}\u2069 {Gimel}"));

        Assert.Equal([Gimel + " ", "abc ", Bet, " " + Alef], VisualTexts(isolated));
    }

    [Fact]
    public void An_inline_box_split_by_reordering_gets_one_part_per_piece_with_edges_at_its_ends()
    {
        // The element holds "ab " and Alef; the Bet after it joins the Alef's right-to-left run, landing between them.
        var box = new BoxStyle { Padding = new Thickness(0, 4, 0, 4), Border = Borders.All(1, PdfColor.Black) };
        var paragraph = new Paragraph(Text).BeginInline(box).Add("ab " + Alef).EndInline().Add(Bet + " cd");
        var line = Single(paragraph);

        Assert.Equal(["ab ", Bet, Alef, " cd"], VisualTexts(line));
        var parts = line.Decorations.OrderBy(d => d.X0).ToList();
        Assert.Equal(2, parts.Count);
        Assert.Equal((true, false), (parts[0].StartEdge, parts[0].EndEdge)); // Left-to-right element: starts on the left.
        Assert.Equal((false, true), (parts[1].StartEdge, parts[1].EndEdge));
        var bet = line.Fragments.Single(f => f.Run.Text == Bet);
        Assert.True(parts[0].X1 <= bet.X + 1e-6 && bet.X + bet.Width <= parts[1].X0 + 1e-6, "Bet lies between the parts");
    }

    [Fact]
    public void A_right_to_left_inline_element_has_its_start_edge_on_the_right()
    {
        var box = new BoxStyle { Padding = new Thickness(0, 6, 0, 2), Border = Borders.All(1, PdfColor.Black) };
        var paragraph = new Paragraph(Text) { Direction = ParagraphDirection.RightToLeft }
            .BeginInline(box, direction: TextDirection.RightToLeft).Add(Alef + Bet).EndInline();
        var line = Single(paragraph);

        var part = Assert.Single(line.Decorations);
        Assert.Equal((true, true), (part.StartEdge, part.EndEdge)); // Both sides on a line holding the whole element.
        var text = Assert.Single(line.Fragments);
        Assert.Equal(1 + 2, text.X - part.X0, 6); // Left: the end edge's padding and border.
        Assert.Equal(6 + 1, part.X1 - (text.X + text.Width), 6); // Right: the start edge's.
    }

    [Fact]
    public void A_split_right_to_left_element_starts_on_the_right_and_ends_on_the_left()
    {
        // In a right-to-left paragraph, the element holds Alef and " box": the Latin joins " then" in a left-to-right
        // run on the left, so the element's start (Alef) is its right part and its end ("box") its left part.
        var box = new BoxStyle { Padding = new Thickness(0, 4, 0, 4), Border = Borders.All(1, PdfColor.Black) };
        var paragraph = new Paragraph(Text) { Direction = ParagraphDirection.RightToLeft }
            .BeginInline(box, direction: TextDirection.RightToLeft).Add(Alef + " box").EndInline().Add(" then");
        var line = Single(paragraph);

        var parts = line.Decorations.OrderBy(d => d.X0).ToList();
        Assert.Equal(2, parts.Count);
        Assert.Equal((true, false), (parts[0].StartEdge, parts[0].EndEdge)); // "box": the end, a left edge.
        Assert.Equal((false, true), (parts[1].StartEdge, parts[1].EndEdge)); // Alef: the start, a right edge.
    }

    [Fact]
    public void Nested_elements_ending_together_close_the_inner_one_first()
    {
        var outer = new BoxStyle { Padding = new Thickness(0, 10, 0, 0) };
        var inner = new BoxStyle { Padding = new Thickness(0, 2, 0, 0) };
        var paragraph = new Paragraph(Text).BeginInline(outer).Add("a").BeginInline(inner).Add("b").EndInline().EndInline();
        var line = Single(paragraph);

        var outerPart = line.Decorations.Single(d => d.Decoration.Box == outer);
        var innerPart = line.Decorations.Single(d => d.Decoration.Box == inner);
        Assert.Equal(10, outerPart.X1 - innerPart.X1, 6); // The inner box ends inside the outer one's padding.
    }

    [Fact]
    public void A_link_split_by_reordering_has_a_rectangle_per_part()
    {
        var paragraph = new Paragraph(Text).BeginLink(LinkTarget.ToUri(new Uri("https://example.com/"))).Add("ab " + Alef).EndLink().Add(Bet + " cd");
        var line = Single(paragraph);

        Assert.Equal(2, line.Links.Count);
    }

    [Fact]
    public void Fragments_stay_in_logical_order_for_extraction()
    {
        var line = Single(new Paragraph(Text).Add($"abc {Alef}{Bet} def"));

        // Drawn (and so extracted) in reading order, while placed in visual order.
        Assert.Equal(["abc ", Bet + Alef, " def"], line.Fragments.Select(GlyphText));
    }

    [Fact]
    public void Mixed_text_renders_with_the_hebrew_reversed_and_extracts()
    {
        var doc = new PdfDocument();
        var page = doc.AddPage(new PdfPageSize(300, 100));
        new Paragraph(Text with { FontSize = 20 }) { Direction = ParagraphDirection.RightToLeft }.Add($"{Alef}{Bet} 12").Layout(280).Draw(page.Canvas, 10, 90);
        var pdf = doc.ToArray();

        using var bitmap = Conversion.ToImage(pdf, page: 0, options: new RenderOptions(Dpi: 72));
        int Ink(int x0, int x1)
        {
            var count = 0;
            for (var x = x0; x < x1; x++)
            {
                for (var y = 10; y < 40; y++)
                {
                    count += bitmap.GetPixel(x, y).Red < 128 ? 1 : 0;
                }
            }

            return count;
        }

        Assert.True(Ink(240, 290) > 50, "the Hebrew is at the right");
        Assert.True(Ink(0, 200) < 5, "nothing on the left half");
    }

    [Fact]
    public void Mixed_text_extracts_with_the_latin_in_reading_order()
    {
        var doc = new PdfDocument();
        new Paragraph(Text).Add($"Total {Alef}{Bet}{Gimel} due").Layout(280).Draw(doc.AddPage(new PdfPageSize(300, 100)).Canvas, 10, 90);

        var text = ExternalTools.ExtractText(doc.ToArray());
        var withoutMarks = new string([.. text.Where(c => c is not ('\u202A' or '\u202B' or '\u202C' or '\u200E' or '\u200F'))]);
        Assert.True(
            withoutMarks.Contains($"Total {Alef}{Bet}{Gimel} due", StringComparison.Ordinal) || withoutMarks.Contains($"Total {Gimel}{Bet}{Alef} due", StringComparison.Ordinal),
            $"Unexpected extraction:\n{text}");
    }

    private static LineLayout Single(Paragraph paragraph) => Assert.Single(paragraph.Layout(300).Lines);

    private static string GlyphText(LineFragment fragment) => string.Concat(fragment.Glyphs.Select(g => g.Text));

    /// <summary>The fragments' glyph texts from left to right (each glyph's characters, so right-to-left runs read reversed).</summary>
    private static List<string> VisualTexts(LineLayout line) => [.. line.Fragments.OrderBy(f => f.X).Select(GlyphText)];
}
