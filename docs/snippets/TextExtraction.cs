#pragma warning disable CA1849, CA2007
using RaiCore.Pdf;
using RaiCore.Pdf.Extraction;
using RaiCore.Pdf.Html;

namespace Snippets;

internal static class TextExtraction
{
    public static Task RunAsync()
    {
        // A file to read: the samples below read "report.pdf".
        HtmlToPdf.Convert("<h1>Quarterly report</h1><p>Revenue grew by 12 percent.</p>").Save("report.pdf");

        #region pages
        var reader = PdfReader.Open("report.pdf");
        var extractor = new TextExtractor(reader);
        foreach (var page in extractor.ExtractPages())
        {
            if (page.HasText)
            {
                Console.WriteLine($"Page {page.PageIndex + 1}:\n{page.Text}");
            }
            else
            {
                Console.WriteLine($"Page {page.PageIndex + 1} has no text: a scan, to OCR if you need its words.");
            }
        }
        #endregion

        #region modes
        var layout = new TextExtractor(reader, new TextExtractionOptions
        {
            Mode = TextExtractionMode.Layout,   // keep columns and table cells where they are on each line
            IncludeInvisibleText = false,       // leave out the OCR layer of scanned pages
        });
        string all = layout.ExtractText();      // every page, a form feed (\f) between pages
        #endregion

        #region untrusted
        using (ResourceLimits.Default.Enforce())   // or limits of your own: memory, time, pages
        {
            var text = new TextExtractor(PdfReader.Open("report.pdf")).ExtractPage(0).Text;
        }
        #endregion

        return Task.CompletedTask;
    }
}
