#pragma warning disable CA1849, CA2007 // Samples read like application code.
using RaiCore.Pdf;
using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Html;

namespace Snippets;

internal static class GettingStarted
{
    public static async Task RunAsync()
    {
        #region html-to-pdf
        var pdf = HtmlToPdf.Convert("""
            <style>
              @page { size: A4; margin: 20mm; }
              html { font-family: Arial, sans-serif; }
            </style>
            <h1>Invoice 2026-0042</h1>
            <p>Thank you for your <b>order</b>.</p>
            """);
        pdf.Save("invoice.pdf");
        #endregion

        var html = "<h1>Report</h1><p>Text.</p>";
        var cancellationToken = CancellationToken.None;

        #region options
        var options = new HtmlConverterOptions
        {
            // Where relative URLs (images, stylesheets) point.
            BaseUri = new Uri(Path.GetFullPath("templates") + Path.DirectorySeparatorChar),
            Title = "Report 2026",
            Language = "en-GB",
            Limits = ResourceLimits.Default with { Timeout = TimeSpan.FromSeconds(30) },
        };
        byte[] bytes = await HtmlToPdf.ConvertToBytesAsync(html, options, cancellationToken);
        #endregion

        #region stream
        await using (var file = File.Create("report.pdf"))
        {
            // Each page is written as soon as it is laid out, with asynchronous writes only.
            await HtmlToPdf.ConvertToStreamAsync(html, file, options, cancellationToken);
        }
        #endregion

        #region draw
        var doc = new PdfDocument();
        var page = doc.AddPage(PdfPageSize.A4);
        page.Canvas
            .SetFillColor(PdfColor.FromRgb(26, 79, 139))
            .Rectangle(72, page.Height - 140, 200, 60)
            .Fill()
            .SetFillColor(PdfColor.Black)
            .DrawText("Hello from RaiCore.Pdf", StandardFont.Helvetica, 24, 72, page.Height - 180);
        doc.Save("hello.pdf");
        #endregion

        _ = bytes;
    }
}
