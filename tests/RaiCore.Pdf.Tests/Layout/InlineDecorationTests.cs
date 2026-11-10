using PDFtoImage;
using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Layout;
using RaiCore.Pdf.Layout.Blocks;
using RaiCore.Pdf.Layout.Text;
using SkiaSharp;

namespace RaiCore.Pdf.Tests.Layout;

/// <summary>Backgrounds, borders, padding and margins of inline elements. Text is 10 pt with 12 pt lines.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
public class InlineDecorationTests
{
    private static readonly PdfFont Sans = PdfFont.Load(Fixtures.Path("LiberationSans-Regular.ttf"));
    private static readonly TextStyle Text = new(Sans, 10) { LineHeight = LineHeight.Points(12) };
    private static readonly BoxStyle Tag = new()
    {
        Margin = new Thickness(0, 3, 0, 3),
        Border = Borders.All(1, PdfColor.Black),
        Padding = new Thickness(2, 5, 2, 5),
        Background = PdfColor.FromRgb(255, 224, 102),
    };

    [Fact]
    public void Horizontal_margin_border_and_padding_take_space_around_the_element()
    {
        var line = new Paragraph(Text).Add("a").AddInline(Tag, p => p.Add("b")).Add("c").Layout(300).Lines[0];

        var piece = Assert.Single(line.Decorations);
        Assert.Equal(W("a") + 3, piece.X0, 6);
        Assert.Equal(W("a") + 3 + 1 + 5 + W("b") + 5 + 1, piece.X1, 6);
        Assert.Equal(W("a") + W("b") + W("c") + (2 * (3 + 1 + 5)), line.Width, 6);
        Assert.Equal(piece.X1 + 3, line.Fragments.Single(f => f.Run.Text == "c").X, 6);
    }

    [Fact]
    public void The_box_covers_the_fonts_content_area_plus_padding_and_border_without_changing_the_line_height()
    {
        var line = new Paragraph(Text).Add("a").AddInline(Tag, p => p.Add("b")).Layout(300).Lines[0];

        var piece = line.Decorations[0];
        Assert.Equal((Sans.Ascent * 10) + 2 + 1, piece.Above, 6);
        Assert.Equal((-Sans.Descent * 10) + 2 + 1, piece.Below, 6);
        Assert.Equal(12, line.Height, 6); // Vertical padding and borders do not make the line taller (CSS).
    }

    [Fact]
    public void An_element_broken_across_lines_is_sliced()
    {
        var layout = new Paragraph(Text).Add("x ").AddInline(Tag, p => p.Add("one two three four")).Add(" y").Layout(W("x one two "));

        var pieces = layout.Lines.SelectMany((line, i) => line.Decorations.Select(d => (Line: i, d.StartEdge, d.EndEdge))).ToList();
        Assert.True(pieces.Count >= 2);
        Assert.Equal((true, false), (pieces[0].StartEdge, pieces[0].EndEdge));
        Assert.Equal((false, true), (pieces[^1].StartEdge, pieces[^1].EndEdge));
        Assert.Equal(0, layout.Lines[pieces[^1].Line].Decorations[0].X0); // Continues from the start of the line.
    }

    [Fact]
    public void Nested_elements_are_listed_outer_first()
    {
        var inner = Tag with { Background = PdfColor.FromRgb(255, 0, 0) };
        var line = new Paragraph(Text).AddInline(Tag, p => p.Add("a").AddInline(inner, q => q.Add("b")).Add("c")).Layout(300).Lines[0];

        Assert.Equal([Tag, inner], line.Decorations.Select(d => d.Decoration.Box));
        Assert.True(line.Decorations[1].X0 > line.Decorations[0].X0);
        Assert.True(line.Decorations[1].X1 < line.Decorations[0].X1);
    }

    [Fact]
    public void Edges_missing_at_a_paragraph_split_take_no_space()
    {
        var paragraph = new Paragraph(Text).BeginInline(Tag, startEdge: false).Add("b").EndInline(endEdge: false);
        var line = paragraph.Layout(300).Lines[0];

        Assert.Equal(W("b"), line.Width, 6);
        Assert.Equal((false, false), (line.Decorations[0].StartEdge, line.Decorations[0].EndEdge));
    }

    [Fact]
    public void Empty_elements_are_dropped_and_unbalanced_ends_are_rejected()
    {
        var paragraph = new Paragraph(Text).Add("a").BeginInline(Tag).EndInline();

        Assert.Empty(paragraph.Decorations);
        Assert.Throws<InvalidOperationException>(() => paragraph.EndInline());
    }

    [Fact]
    public void Intrinsic_widths_include_the_edges()
    {
        var (min, max) = new Paragraph(Text).AddInline(Tag, p => p.Add("word")).MeasureIntrinsicWidths();

        Assert.Equal(W("word") + 18, min, 6);
        Assert.Equal(W("word") + 18, max, 6);
    }

    [Fact]
    public void The_background_is_drawn_behind_the_text()
    {
        var red = new BoxStyle { Background = PdfColor.FromRgb(255, 0, 0), Padding = new Thickness(0, 10, 0, 10) };
        var flow = new FlowDocument(new PageSetup(new PdfPageSize(200, 100), Thickness.All(20)))
            .Add(new ParagraphBlock(new Paragraph(Text).Add("ab ").AddInline(red, p => p.Add("cd"))));

        using var bitmap = Conversion.ToImage(flow.Render().ToArray(), page: 0, options: new RenderOptions(Dpi: 72));
        var left = 20 + W("ab ");
        Assert.Equal(new SKColor(255, 0, 0), bitmap.GetPixel((int)(left + 4), 28));  // In the left padding.
        Assert.NotEqual(new SKColor(255, 0, 0), bitmap.GetPixel((int)(left - 3), 28));
    }

    private static double W(string text) => TextShaper.Shape(text, Text.Fonts).GetWidth(Text.FontSize);
}
