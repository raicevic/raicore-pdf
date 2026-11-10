using PDFtoImage;
using RaiCore.Pdf.Html;
using RaiCore.Pdf.Layout;
using RaiCore.Pdf.Layout.Blocks;
using SkiaSharp;

namespace RaiCore.Pdf.Tests.Html;

/// <summary>SVG in HTML: <c>img</c> elements with SVG files or data URIs, and inline <c>svg</c> elements.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
public class HtmlSvgTests
{
    private const double Px = 0.75; // Points per CSS pixel.

    private static readonly Uri SvgFolder = new(Fixtures.Path("Svg") + Path.DirectorySeparatorChar);

    [Fact]
    public void Img_elements_load_svg_files_at_their_natural_size()
    {
        var blocks = Blocks("""
            <img src="logo.svg" style="display: block">
            <p><img src="icons.svg"> <img src="icons.svg" height="64"></p>
            """);

        var block = Flatten(blocks).OfType<ImageBlock>().Single();
        Assert.Equal(ImageFormat.Svg, block.Image.Format);
        Assert.Equal((300 * Px, 100 * Px), (block.Image.NaturalWidth, block.Image.NaturalHeight));
        Assert.Equal([(120 * Px, 32 * Px), (240 * Px, 64 * Px)], InlineImages(blocks).Select(i => (i.Width, i.Height)));
        Assert.Same(InlineImages(blocks)[0].Image, InlineImages(blocks)[1].Image); // Loaded once.
    }

    [Fact]
    public void Svg_data_uris_load_url_encoded_or_base64()
    {
        const string svg = "<svg xmlns='http://www.w3.org/2000/svg' width='20' height='10'><rect width='20' height='10' fill='#090'/></svg>";
        var encoded = "data:image/svg+xml," + Uri.EscapeDataString(svg);
        var base64 = "data:image/svg+xml;base64," + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(svg));
        var raw = "data:image/svg+xml;utf8," + svg.Replace('\'', '"').Replace("#", "%23", StringComparison.Ordinal); // Unescaped markup.
        var blocks = Blocks($"""<p><img src="{encoded}"><img src="{base64}"><img src='{raw}'></p>""");

        Assert.Equal(
            [(20 * Px, 10 * Px), (20 * Px, 10 * Px), (20 * Px, 10 * Px)],
            InlineImages(blocks).Select(i => (i.Width, i.Height)));
        Assert.All(InlineImages(blocks), i => Assert.Equal(ImageFormat.Svg, i.Image.Format));
    }

    [Fact]
    public void Inline_svg_elements_are_images_sized_by_css_or_attributes()
    {
        var blocks = Blocks("""
            <p>
              <svg width="24" height="12" viewBox="0 0 24 12"><rect width="24" height="12"/></svg>
              <svg viewBox="0 0 24 24" style="width: 48px"><circle cx="12" cy="12" r="10"/></svg>
              <svg viewBox="0 0 10 5"><rect width="10" height="5"/></svg>
            </p>
            <svg style="display: block; height: 30px" viewBox="0 0 20 10"><rect width="20" height="10"/></svg>
            """);

        // Attributes; a CSS width with the view box's aspect ratio; the view box alone.
        Assert.Equal([(24 * Px, 12 * Px), (48 * Px, 48 * Px), (10 * Px, 5 * Px)], InlineImages(blocks).Select(i => (i.Width, i.Height)));
        var block = Flatten(blocks).OfType<ImageBlock>().Single();
        Assert.Equal((null, 30 * Px), (block.Width, block.Height));
    }

    [Fact]
    public void Inline_svg_text_is_not_html_text()
    {
        var blocks = Blocks("<p>before <svg width='10' height='10'><text>secret</text><title>tooltip</title></svg> after</p>");

        var text = string.Concat(Flatten(blocks).OfType<ParagraphBlock>().SelectMany(p => p.Paragraph.Spans).Select(s => s.Text));
        Assert.DoesNotContain("secret", text, StringComparison.Ordinal);
        Assert.DoesNotContain("tooltip", text, StringComparison.Ordinal);
        Assert.Single(InlineImages(blocks));
    }

    [Fact]
    public void Inline_svg_uses_the_css_color_xlink_references_and_mixed_case_names()
    {
        // HTML parsing lower-cases names (fixed up for SVG), drops the xmlns:xlink declaration and resolves &nbsp;.
        const string html = """
            <style>.brand { color: #0000ff } body { margin: 0 } @page { size: 200px 200px; margin: 0 }</style>
            <div class="brand">
              <svg width="100" height="100" viewBox="0 0 100 100" style="display: block">
                <defs>
                  <linearGradient id="g" gradientUnits="userSpaceOnUse" x1="0" x2="0" y1="50" y2="100">
                    <stop offset="0" stop-color="#f00"/><stop offset="1" stop-color="#f00"/>
                  </linearGradient>
                  <clipPath id="c"><rect width="100" height="40"/></clipPath>
                </defs>
                <rect width="100" height="100" fill="currentColor" clip-path="url(#c)"/>
                <use xlink:href="#bar"/>
                <rect id="bar" y="60" width="100" height="40" fill="url(#g)"/>
                <text y="55">&nbsp;</text>
              </svg>
            </div>
            """;
        var pdf = HtmlToPdf.ConvertToBytes(html);
        using var bitmap = Conversion.ToImage(pdf, page: 0, options: new RenderOptions(Dpi: 96)); // One pixel per CSS pixel.

        Assert.Equal(new SKColor(0, 0, 255), bitmap.GetPixel(50, 20));   // currentColor from the CSS color, clipped at 40.
        Assert.Equal(SKColors.White, bitmap.GetPixel(50, 50));
        Assert.Equal(new SKColor(255, 0, 0), bitmap.GetPixel(50, 80));   // Gradient through xlink:href.
    }

    [Fact]
    public void Broken_svg_falls_back_to_the_alt_text()
    {
        var blocks = Blocks("""<p><img src="data:image/svg+xml,%3Csvg%3E%3Crect%3E" alt="[logo]"></p>""");

        Assert.Empty(InlineImages(blocks));
        Assert.Contains("[logo]", string.Concat(Flatten(blocks).OfType<ParagraphBlock>().SelectMany(p => p.Paragraph.Spans).Select(s => s.Text)), StringComparison.Ordinal);
    }

    [Fact]
    public void Svg_in_flex_containers_and_pdf_output()
    {
        var pdf = HtmlToPdf.ConvertToBytes("""
            <div style="display: flex; gap: 10px"><img src="icons.svg"><svg width="20" height="20"><circle cx="10" cy="10" r="10" fill="#c00"/></svg></div>
            <img src="chart.svg"><img src="exported.svg" width="200">
            """, new HtmlConverterOptions { BaseUri = SvgFolder });

        using var bitmap = Conversion.ToImage(pdf, page: 0, options: new RenderOptions(Dpi: 72));
        Assert.Contains(Enumerable.Range(0, bitmap.Height).SelectMany(y => Enumerable.Range(0, bitmap.Width).Select(x => bitmap.GetPixel(x, y))), c => c.Red > 180 && c.Green < 40 && c.Blue < 40);
        ExternalTools.AssertQpdfClean(pdf);
    }

    private static IReadOnlyList<Block> Blocks(string html) =>
        HtmlToPdf.CreateFlowDocument(html, new HtmlConverterOptions { BaseUri = SvgFolder }).Blocks;

    private static IEnumerable<Block> Flatten(IEnumerable<Block> blocks) =>
        blocks.SelectMany(b => b is ContainerBlock c ? Flatten(c.Children).Prepend(b) : [b]);

    private static List<InlineImage> InlineImages(IEnumerable<Block> blocks) =>
        [.. Flatten(blocks).OfType<ParagraphBlock>().SelectMany(p => p.Paragraph.Spans).Where(s => s.Image is not null).Select(s => s.Image!)];
}
