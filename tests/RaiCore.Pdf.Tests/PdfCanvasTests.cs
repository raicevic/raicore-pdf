using System.Text;

namespace RaiCore.Pdf.Tests;

public class PdfCanvasTests
{
    [Fact]
    public void Writes_stroke_style_transform_and_clip_operators()
    {
        var canvas = new PdfDocument().AddPage(PdfPageSize.A4).Canvas
            .SetDash([3, 1.5], 0.5)
            .SetDash([])
            .SetLineCap(LineCap.Round)
            .SetLineJoin(LineJoin.Bevel)
            .SetMiterLimit(4)
            .Transform(1, 0, 0, -1, 10, 20)
            .Rectangle(0, 0, 10, 10).Clip()
            .Rectangle(0, 0, 5, 5).Clip(evenOdd: true)
            .Rectangle(0, 0, 1, 1).FillEvenOdd()
            .Rectangle(0, 0, 1, 1).FillAndStrokeEvenOdd()
            .MoveTo(0, 0).EndPath();

        var ops = Encoding.Latin1.GetString(canvas.ToBytes()).Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(
            ["[3 1.5] 0.5 d", "[] 0 d", "1 J", "2 j", "4 M", "1 0 0 -1 10 20 cm", "0 0 10 10 re", "W n", "0 0 5 5 re", "W* n",
             "0 0 1 1 re", "f*", "0 0 1 1 re", "B*", "0 0 m", "n"],
            ops);
    }

    [Theory]
    [InlineData(new double[] { -1, 2 })]
    [InlineData(new double[] { 0, 0 })]
    public void Rejects_invalid_dash_patterns(double[] pattern) =>
        Assert.Throws<ArgumentException>(() => new PdfDocument().AddPage().Canvas.SetDash(pattern));

    [Fact]
    public void Rejects_a_miter_limit_below_one() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new PdfDocument().AddPage().Canvas.SetMiterLimit(0.5));

    [Fact]
    public void Produces_a_valid_pdf() => ExternalTools.AssertQpdfClean(DashedPdf());

    private static byte[] DashedPdf()
    {
        var document = new PdfDocument();
        document.AddPage(PdfPageSize.A4).Canvas
            .SaveState().SetDash([6, 3]).SetLineCap(LineCap.Round).SetLineJoin(LineJoin.Round).SetLineWidth(2)
            .MoveTo(72, 700).LineTo(300, 700).LineTo(300, 600).Stroke().RestoreState()
            .SaveState().Transform(1, 0, 0, 1, 50, 50).Rectangle(72, 400, 100, 100).Clip().Rectangle(0, 0, 600, 800).Fill().RestoreState();
        return document.ToArray();
    }
}
