#pragma warning disable CA1849, CA2007
using RaiCore.Pdf;
using RaiCore.Pdf.Html;

namespace Snippets;

internal static class Standards
{
    public static Task RunAsync()
    {
        var html = """
            <html lang="en"><head><title>Invoice 2026-0042</title></head>
            <body><h1>Invoice 2026-0042</h1><p>Total: 7,365.00</p></body></html>
            """;

        #region pdfua
        // A tagged PDF that also meets PDF/UA-1: it needs a title and a language, and alt text on images.
        var accessible = HtmlToPdf.Convert(html, new HtmlConverterOptions { PdfUA = true });
        accessible.Save("accessible.pdf");
        #endregion

        #region pdfa
        var archived = HtmlToPdf.Convert(html, new HtmlConverterOptions { PdfAConformance = PdfAConformance.PdfA3B });
        archived.Save("archived.pdf");
        #endregion

        #region pdfa-level-a
        // PDF/A-3a: PDF/A-3b plus the logical structure (tagged) and Unicode text. Needs a language and alt text on images.
        var archivedAccessible = HtmlToPdf.Convert(html, new HtmlConverterOptions { PdfAConformance = PdfAConformance.PdfA3A });
        archivedAccessible.Save("archived-accessible.pdf");
        #endregion

        var crossIndustryInvoice = """<?xml version="1.0" encoding="UTF-8"?><rsm:CrossIndustryInvoice xmlns:rsm="urn:un:unece:uncefact:data:standard:CrossIndustryInvoice:100"/>"""u8.ToArray();

        #region facturx
        var invoice = HtmlToPdf.Convert(html, new HtmlConverterOptions { PdfAConformance = PdfAConformance.PdfA3B });
        invoice.AttachFacturXInvoice(crossIndustryInvoice, FacturXProfile.EN16931);   // the CII XML of the invoice
        invoice.Save("factur-x.pdf");
        #endregion

        #region encryption
        var protectedPdf = HtmlToPdf.Convert(html, new HtmlConverterOptions
        {
            Encryption = new PdfEncryption { OwnerPassword = "admin", Permissions = PdfPermissions.Print },
        });
        protectedPdf.Save("protected.pdf");
        #endregion

        return Task.CompletedTask;
    }
}
