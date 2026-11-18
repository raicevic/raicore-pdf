#pragma warning disable CA1849, CA2007
using System.Text.RegularExpressions;
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

        #region search
        var search = new TextExtractor(reader);
        foreach (var match in search.Find("revenue", ignoreCase: true))
        {
            // Bounds and Rectangles are in PDF points from the page's bottom-left corner (its user space).
            Console.WriteLine($"Page {match.PageIndex + 1}: \"{match.Text}\" at {match.Bounds}");
        }

        // Regular expressions work too: here, every percentage on the first page.
        var amounts = search.FindOnPage(0, new Regex(@"\d+ percent"));
        #endregion

        #region highlight
        // Copy the pages and draw a translucent box over every match.
        var highlighted = new PdfDocument();
        var pages = highlighted.ImportPages(reader);
        foreach (var match in search.Find("percent"))
        {
            var media = reader.Pages[match.PageIndex].MediaBox;   // an imported page's canvas starts at its corner
            var canvas = pages[match.PageIndex].Canvas.SetFillColor(PdfColor.FromRgba(255, 230, 0, 0.4));
            foreach (var box in match.Rectangles)
            {
                canvas.Rectangle(box.X - media.X, box.Y - media.Y, box.Width, box.Height).Fill();
            }
        }

        highlighted.Save("report-highlighted.pdf");
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
