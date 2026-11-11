using PDFtoImage;
using RaiCore.Pdf.Html;
using RaiCore.Pdf.Layout.Blocks;
using SkiaSharp;

namespace RaiCore.Pdf.Tests.Html;

/// <summary>CSS <c>overflow</c> clipping and <c>text-overflow: ellipsis</c>.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
public class HtmlOverflowTests
{
    [Theory]
    [InlineData("overflow: hidden", true, true)]
    [InlineData("overflow: clip", true, false)] // Clips without a new formatting context.
    [InlineData("overflow: auto", true, true)]
    [InlineData("overflow-y: scroll", true, true)]
    [InlineData("overflow: visible", false, false)]
    [InlineData("", false, false)]
    public void Overflow_clips_and_may_contain_floats(string css, bool clips, bool containsFloats)
    {
        var style = Box($"<div id=\"b\" style=\"{css}\">x</div>").Style;

        Assert.Equal((clips, containsFloats), (style.ClipOverflow, style.ContainsFloats));
    }

    [Theory]
    [InlineData("overflow: hidden; text-overflow: ellipsis", true)]
    [InlineData("text-overflow: ellipsis", false)] // Only in a box that clips.
    [InlineData("overflow: hidden", false)]
    public void Text_overflow_ellipsis_applies_in_a_clipping_box(string css, bool ellipsis)
    {
        var paragraph = Assert.IsType<ParagraphBlock>(Box($"<div id=\"b\" style=\"{css}\">text</div>"));

        Assert.Equal(ellipsis, paragraph.Paragraph.Ellipsis);
    }

    [Fact]
    public void Hidden_overflow_is_not_drawn()
    {
        var pdf = HtmlToPdf.Convert(
            "<body style=\"margin: 0\"><div style=\"height: 50px; overflow: hidden\"><div style=\"height: 200px; background: red\"></div></div></body>").ToArray();
        using var bitmap = Conversion.ToImage(pdf, page: 0, options: new RenderOptions(Dpi: 72));

        Assert.Equal(new SKColor(255, 0, 0), bitmap.GetPixel(100, 70)); // The page area starts at 56.7; the box is 37.5 pt tall.
        Assert.Equal(new SKColor(255, 255, 255), bitmap.GetPixel(100, 110));
    }

    private static Block Box(string html) => Flatten(HtmlToPdf.CreateFlowDocument(html).Blocks).Single(b => b.Id == "b");

    private static IEnumerable<Block> Flatten(IEnumerable<Block> blocks) =>
        blocks.SelectMany(b => b is ContainerBlock c ? Flatten(c.Children).Prepend(b) : [b]);
}
