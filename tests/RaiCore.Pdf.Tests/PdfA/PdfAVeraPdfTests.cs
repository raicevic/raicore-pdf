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
    [InlineData(PdfAConformance.PdfA1B, "1b")]
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

    [Fact]
    public void A_tagged_html_document_is_both_PdfA2b_and_PdfUA1()
    {
        var pdf = PdfASamples.Html(PdfAConformance.PdfA2B, pdfUA: true).ToArray();

        ExternalTools.AssertVeraPdfCompliant(pdf, "2b");
        ExternalTools.AssertVeraPdfCompliant(pdf, "ua1");
    }

    [Fact]
    public void A_tagged_canvas_document_with_a_substituted_standard_font_is_both_PdfA2b_and_PdfUA1()
    {
        var pdf = PdfATests.TaggedPdfUA(PdfAConformance.PdfA2B).ToArray();

        ExternalTools.AssertVeraPdfCompliant(pdf, "2b");
        ExternalTools.AssertVeraPdfCompliant(pdf, "ua1");
    }

    [Theory]
    [InlineData(PdfAConformance.PdfA2A, "2a")]
    [InlineData(PdfAConformance.PdfA3A, "3a")]
    public void A_converted_html_document_is_compliant_at_level_A(PdfAConformance level, string flavour)
    {
        ExternalTools.AssertVeraPdfCompliant(PdfASamples.HtmlLevelA(level).ToArray(), flavour);
        ExternalTools.AssertVeraPdfCompliant(PdfASamples.Html(level).ToArray(), flavour);
    }

    [Theory]
    [InlineData(PdfAConformance.PdfA2A, "2a")]
    [InlineData(PdfAConformance.PdfA3A, "3a")]
    [InlineData(PdfAConformance.PdfA1A, "1a")]
    public void A_layout_document_is_compliant_at_level_A(PdfAConformance level, string flavour)
    {
        ExternalTools.AssertVeraPdfCompliant(PdfASamples.Layout(level).ToArray(), flavour);
    }

    [Theory]
    [InlineData(PdfAConformance.PdfA2A, "2a")]
    [InlineData(PdfAConformance.PdfA3A, "3a")]
    public void A_tagged_document_is_both_level_A_and_PdfUA1(PdfAConformance level, string flavour)
    {
        var canvas = PdfATests.TaggedPdfUA(level).ToArray();
        ExternalTools.AssertVeraPdfCompliant(canvas, flavour);
        ExternalTools.AssertVeraPdfCompliant(canvas, "ua1");

        var html = PdfASamples.Html(level, pdfUA: true).ToArray();
        ExternalTools.AssertVeraPdfCompliant(html, flavour);
        ExternalTools.AssertVeraPdfCompliant(html, "ua1");
    }

    [Fact]
    public void Level_A_is_really_checked()
    {
        // An untagged PDF/A-2b file validated as PDF/A-2a has no structure tree (ISO 19005-2, 6.7.3.3) and says B (6.6.4).
        var report = ExternalTools.ValidateWithVeraPdf(PdfASamples.Canvas(PdfAConformance.PdfA2B).ToArray(), "2a");

        Assert.False(report.IsCompliant);
        Assert.Contains(report.FailedRules, rule => rule.Contains("6.7.3.3-1", StringComparison.Ordinal));
        Assert.Contains(report.FailedRules, rule => rule.Contains("6.6.4-3", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(PdfAConformance.PdfA1B, "1b")]
    [InlineData(PdfAConformance.PdfA1A, "1a")]
    public void A_canvas_document_without_transparency_is_PdfA1_compliant(PdfAConformance level, string flavour)
    {
        ExternalTools.AssertVeraPdfCompliant(PdfASamples.Opaque(level).ToArray(), flavour);
    }

    [Theory]
    [InlineData(PdfAConformance.PdfA1B, "1b")]
    [InlineData(PdfAConformance.PdfA1A, "1a")]
    public void A_converted_html_document_without_transparency_is_PdfA1_compliant(PdfAConformance level, string flavour)
    {
        ExternalTools.AssertVeraPdfCompliant(PdfASamples.HtmlOpaque(level).ToArray(), flavour);
    }

    [Fact]
    public void A_tagged_document_is_both_PdfA1a_and_PdfUA1()
    {
        var canvas = PdfATests.TaggedPdfUA(PdfAConformance.PdfA1A).ToArray();
        ExternalTools.AssertVeraPdfCompliant(canvas, "1a");
        ExternalTools.AssertVeraPdfCompliant(canvas, "ua1");

        var html = PdfASamples.HtmlOpaque(PdfAConformance.PdfA1A, pdfUA: true).ToArray();
        ExternalTools.AssertVeraPdfCompliant(html, "1a");
        ExternalTools.AssertVeraPdfCompliant(html, "ua1");
    }

    [Fact]
    public void What_PdfA1_writes_differently_is_really_checked()
    {
        // The PDF/A-2b version of the sample has a soft mask (6.4-2), 16-bit samples (6.2.4-4), font subsets without a
        // CIDSet (6.3.5-3), and says part 2 (6.7.11-2).
        var report = ExternalTools.ValidateWithVeraPdf(PdfASamples.Opaque(PdfAConformance.PdfA2B).ToArray(), "1b");

        Assert.False(report.IsCompliant);
        foreach (var rule in new[] { "6.4-2", "6.2.4-4", "6.3.5-3", "6.7.11-2" })
        {
            Assert.Contains(report.FailedRules, failed => failed.Contains($" {rule}:", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Transparency_is_really_checked_for_PdfA1()
    {
        // Transparency groups (6.4-3) and constant alpha (6.4-5, 6.4-6), which saving as PDF/A-1 rejects.
        var report = ExternalTools.ValidateWithVeraPdf(PdfASamples.Canvas(PdfAConformance.PdfA2B).ToArray(), "1b");

        Assert.False(report.IsCompliant);
        foreach (var rule in new[] { "6.4-3", "6.4-5", "6.4-6" })
        {
            Assert.Contains(report.FailedRules, failed => failed.Contains($" {rule}:", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Level_A_and_the_structure_types_of_PDF_1_4_are_really_checked_for_PdfA1()
    {
        // An untagged PDF/A-1b file has no structure tree (6.8.3.3-1) and says B (6.7.11-3).
        var levelB = ExternalTools.ValidateWithVeraPdf(PdfASamples.Opaque(PdfAConformance.PdfA1B).ToArray(), "1a");
        Assert.Contains(levelB.FailedRules, rule => rule.Contains(" 6.8.3.3-1:", StringComparison.Ordinal));
        Assert.Contains(levelB.FailedRules, rule => rule.Contains(" 6.7.11-3:", StringComparison.Ordinal));

        // The table row groups of PDF/A-2a (THead, TBody) are not standard in PDF 1.4 (6.8.3.4-1), so PDF/A-1a leaves them out.
        var rowGroups = ExternalTools.ValidateWithVeraPdf(PdfASamples.Layout(PdfAConformance.PdfA2A).ToArray(), "1a");
        Assert.Contains(rowGroups.FailedRules, rule => rule.Contains(" 6.8.3.4-1:", StringComparison.Ordinal));
    }

    [Fact]
    public void A_PdfA3a_invoice_is_compliant()
    {
        var doc = PdfASamples.HtmlLevelA(PdfAConformance.PdfA3A).AttachFacturXInvoice(PdfAttachmentTests.InvoiceXml, FacturXProfile.EN16931);
        ExternalTools.AssertVeraPdfCompliant(doc.ToArray(), "3a");
    }

    [Fact]
    public void A_FacturX_invoice_is_PdfA3b_compliant()
    {
        ExternalTools.AssertVeraPdfCompliant(PdfAttachmentTests.Invoice().ToArray(), "3b");
    }

    [Fact]
    public void A_PdfA3b_document_with_several_attachments_is_compliant()
    {
        var doc = PdfASamples.Canvas(PdfAConformance.PdfA3B);
        doc.Attachments.Add(new PdfAttachment("notes.txt", "Notes"u8, "text/plain") { Description = "Notes", Relationship = PdfAttachmentRelationship.Supplement });
        doc.Attachments.Add(new PdfAttachment("data.bin", new byte[] { 0, 1, 2, 255 }));
        ExternalTools.AssertVeraPdfCompliant(doc.ToArray(), "3b");
    }

    [Fact]
    public void A_converted_html_invoice_is_compliant()
    {
        var doc = PdfASamples.Html(PdfAConformance.PdfA3B).AttachFacturXInvoice(PdfAttachmentTests.InvoiceXml, FacturXProfile.EN16931);
        ExternalTools.AssertVeraPdfCompliant(doc.ToArray(), "3b");
    }
}
