using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Html;
using RaiCore.Pdf.Layout;

namespace RaiCore.Pdf.Tests.Layout;

/// <summary>Text that does not wrap (#142): no soft wrap opportunity inside it, at spaces, hyphens or hyphenation points.</summary>
public class NoWrapTests
{
    private static readonly TextStyle Wrapping = new(PdfFont.Load(Fixtures.Path("LiberationSans-Regular.ttf")), 10) { Language = "en-US" };
    private static readonly TextStyle NoWrap = Wrapping with { NoWrap = true };

    private static List<string> Lines(Paragraph paragraph, double width)
    {
        var text = string.Concat(paragraph.Spans.Select(s => s.Text));
        return [.. ParagraphLayouter.Layout(paragraph, width).Lines.Select(line => text[line.Start..line.End].TrimEnd())];
    }

    [Fact]
    public void No_wrap_text_does_not_break_at_spaces_hyphens_slashes_or_dashes()
    {
        var paragraph = new Paragraph(NoWrap).Add("alpha-beta gamma/delta \u2014 epsilon-zeta", NoWrap);

        Assert.Single(Lines(paragraph, 40));
        Assert.Equal(ParagraphLayouter.MeasureIntrinsic(paragraph).Max, ParagraphLayouter.MeasureIntrinsic(paragraph).Min, 6); // Min-content is the whole run.
        Assert.Equal(2, Lines(new Paragraph(Wrapping).Add("alpha-beta gamma/delta \u2014 epsilon-zeta"), 120).Count); // Without it, it wraps.
    }

    [Fact]
    public void Breaks_around_no_wrap_text_stay_but_none_falls_inside_it()
    {
        var paragraph = new Paragraph(Wrapping).Add("aa bb ", Wrapping).Add("cc-dd ee", NoWrap).Add(" ff gg", Wrapping);

        var lines = Lines(paragraph, 1);

        Assert.Equal(["aa", "bb", "cc-dd ee", "ff", "gg"], lines);
    }

    [Fact]
    public void No_wrap_text_is_not_hyphenated_not_even_at_soft_hyphens()
    {
        var auto = NoWrap with { Hyphens = Hyphens.Auto };
        var paragraph = new Paragraph(auto).Add("internationalization hyphen\u00adated", auto);

        Assert.Single(Lines(paragraph, 30));
    }

    [Fact]
    public void Hard_breaks_still_break_no_wrap_text()
    {
        var paragraph = new Paragraph(NoWrap).Add("first-line\nsecond-line", NoWrap);

        Assert.Equal(["first-line", "second-line"], Lines(paragraph, 10));
    }

    [Theory]
    [InlineData("nowrap")]
    [InlineData("pre")]
    public void Html_nowrap_and_pre_keep_their_spaces_and_do_not_wrap(string whiteSpace)
    {
        var found = new List<HtmlDiagnostic>();
        var flow = HtmlToPdf.CreateFlowDocument(
            $"<div style='width: 100px'><p style='white-space: {whiteSpace}; margin: 0'>alpha-beta gamma-delta epsilon</p></div>",
            new HtmlConverterOptions { Diagnostics = found.Add });
        var paragraph = Flatten(flow.Blocks).OfType<ParagraphBlock>().Single().Paragraph;

        Assert.Equal("alpha-beta gamma-delta epsilon", string.Concat(paragraph.Spans.Select(s => s.Text)).Trim()); // Regular spaces.
        Assert.Single(ParagraphLayouter.Layout(paragraph, 75).Lines);
        flow.Render();
        Assert.Equal(HtmlDiagnosticKind.Overflow, Assert.Single(found).Kind); // It runs past its 75 pt box, as in browsers.
    }

    [Fact]
    public void Pre_keeps_leading_spaces_after_hard_breaks()
    {
        var flow = HtmlToPdf.CreateFlowDocument("<pre style='margin: 0'>a\n    indented</pre>");
        var paragraph = Flatten(flow.Blocks).OfType<ParagraphBlock>().Single().Paragraph;
        var layout = ParagraphLayouter.Layout(paragraph, 400);

        Assert.Equal(2, layout.Lines.Count);
        Assert.True(layout.Lines[1].Width > ParagraphLayouter.Layout(new Paragraph(paragraph.Spans[0].Style).Add("indented"), 400).Lines[0].Width,
            "The four leading spaces take room");
    }

    private static IEnumerable<Block> Flatten(IEnumerable<Block> blocks) =>
        blocks.SelectMany(b => b switch
        {
            ContainerBlock c => Flatten(c.Children).Prepend(b),
            _ => [b],
        });
}
