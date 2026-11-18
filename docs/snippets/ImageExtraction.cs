#pragma warning disable CA1849, CA2007
using RaiCore.Pdf;
using RaiCore.Pdf.Extraction;

namespace Snippets;

internal static class ImageExtraction
{
    private const string Pixel = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==";

    public static Task RunAsync()
    {
        // A file to read: a page with an image on it.
        var source = new PdfDocument();
        var picture = PdfImage.Load(Convert.FromBase64String(Pixel));
        source.AddPage(PdfPageSize.A4).Canvas.DrawImage(picture, 72, 500, 240, 160);
        source.Save("brochure.pdf");

        #region list
        var extractor = new ImageExtractor(PdfReader.Open("brochure.pdf"));
        foreach (var page in extractor.ExtractPages())
        {
            foreach (var image in page.Images)
            {
                // Bounds is in PDF points from the page's bottom-left corner; Width and Height are the displayed size.
                Console.WriteLine($"Page {page.PageIndex + 1}: {image.PixelWidth} x {image.PixelHeight} pixels "
                    + $"({image.ColorSpace}), shown {image.Width:0} x {image.Height:0} pt at ({image.Bounds.X:0}, {image.Bounds.Y:0})");
            }
        }
        #endregion

        #region export
        var saved = new HashSet<int>();
        foreach (var page in extractor.ExtractPages())
        {
            foreach (var image in page.Images)
            {
                // An image drawn several times has one object number: save it once.
                if (image.ObjectNumber is { } number && !saved.Add(number))
                {
                    continue;
                }

                var name = $"page{page.PageIndex + 1}-{image.ObjectNumber?.ToString() ?? "inline"}{image.FileExtension}";
                File.WriteAllBytes(name, image.GetData());   // .png, .jpg, .jp2, .tif, ...
            }
        }
        #endregion

        return Task.CompletedTask;
    }
}
