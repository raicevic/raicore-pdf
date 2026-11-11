using RaiCore.Pdf.Fonts;

namespace RaiCore.Pdf.Tests.PdfA;

/// <summary>
/// PDF/A output validated by veraPDF, the reference PDF/A validator. veraPDF runs in CI (Linux); these tests skip
/// where it is not installed. <see cref="PdfATests"/> checks the structure locally.
/// </summary>
public class PdfAVeraPdfTests
{
    [Theory]
    [InlineData(PdfAConformance.PdfA2B, "2b")]
    [InlineData(PdfAConformance.PdfA3B, "3b")]
    public void A_minimal_document_is_compliant(PdfAConformance level, string flavour)
    {
        var doc = new PdfDocument { PdfAConformance = level }.EmbedStandardFonts();
        doc.AddPage().Canvas.DrawText("Archived", StandardFont.Helvetica, 12, 72, 720);

        ExternalTools.AssertVeraPdfCompliant(doc.ToArray(), flavour);
    }

    [Theory]
    [InlineData(PdfAConformance.PdfA2B, "2b")]
    [InlineData(PdfAConformance.PdfA3B, "3b")]
    public void A_canvas_document_with_fonts_images_svg_transparency_links_and_outline_is_compliant(PdfAConformance level, string flavour)
    {
        ExternalTools.AssertVeraPdfCompliant(PdfASamples.Canvas(level).ToArray(), flavour);
    }

    [Theory]
    [InlineData(PdfAConformance.PdfA2B, "2b")]
    [InlineData(PdfAConformance.PdfA3B, "3b")]
    public void A_converted_html_document_is_compliant(PdfAConformance level, string flavour)
    {
        ExternalTools.AssertVeraPdfCompliant(PdfASamples.Html(level).ToArray(), flavour);
    }

    [Fact]
    public void The_part_must_match_the_identification()
    {
        // A PDF/A-2b file validated as PDF/A-3b fails on pdfaid:part (ISO 19005-3, 6.6.4): veraPDF really checks it.
        var report = ExternalTools.ValidateWithVeraPdf(PdfASamples.Canvas(PdfAConformance.PdfA2B).ToArray(), "3b");

        Assert.False(report.IsCompliant);
        Assert.Contains(report.FailedRules, rule => rule.Contains("6.6.4-2", StringComparison.Ordinal));
    }
}
