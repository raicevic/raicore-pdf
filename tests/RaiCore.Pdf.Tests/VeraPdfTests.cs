using RaiCore.Pdf.Fonts;

namespace RaiCore.Pdf.Tests;

/// <summary>
/// Checks the veraPDF plumbing that PDF/A (#46, #47) and PDF/UA (#48) tests build on: a plain document is
/// neither PDF/A nor PDF/UA, so veraPDF must report it as non-compliant and name the failed rules.
/// </summary>
public class VeraPdfTests
{
    [Theory]
    [InlineData("2b")]
    [InlineData("3b")]
    [InlineData("ua1")]
    public void VeraPdf_reports_a_plain_document_as_non_compliant(string flavour)
    {
        var doc = new PdfDocument();
        doc.AddPage().Canvas.DrawText("Not PDF/A", StandardFont.Helvetica, 12, 72, 720);

        var report = ExternalTools.ValidateWithVeraPdf(doc.ToArray(), flavour);

        Assert.False(report.IsCompliant);
        Assert.NotEmpty(report.FailedRules);
        Assert.All(report.FailedRules, rule => Assert.Matches(@"^ISO \d+-\d+:\d{4} [\d.]+-\d+: \S", rule));
    }
}
