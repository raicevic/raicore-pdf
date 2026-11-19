#pragma warning disable CA1849, CA2007
using RaiCore.Pdf;
using RaiCore.Pdf.Html;

namespace Snippets;

internal static class Images
{
    private const string Pixel = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==";

    /// <summary>A two-page CCITT Group 4 fax TIFF (64 x 32 pixels at 204 x 196 dpi).</summary>
    private const string Fax =
        "SUkqADIAAAAmoHhvzpfI4LP////////yCh/0EUP+P///////4/////////HjwAQAQAAMAAABAwABAAAAQAAAAAEBAwABAAAAIAAA" +
        "AAIBAwABAAAAAQAAAAMBAwABAAAABAAAAAYBAwABAAAAAQAAABEBBAABAAAACAAAABYBAwABAAAAIAAAABcBBAABAAAAKQAAABoB" +
        "BQABAAAAyAAAABsBBQABAAAA0AAAABwBAwABAAAAAQAAACgBAwABAAAAAgAAABQBAADMAAAAAQAAAMQAAAABAAAAAAAAAAAAAABJ" +
        "SSoANAAAACageG/Ol8jgs/////////IKH/QRx/x//9L9L9L9A/x////////+PHgAgAgADAAAAQMAAQAAAEAAAAABAQMAAQAAACAA" +
        "AAACAQMAAQAAAAEAAAADAQMAAQAAAAQAAAAGAQMAAQAAAAEAAAARAQQAAQAAAOgAAAAWAQMAAQAAACAAAAAXAQQAAQAAACsAAAAa" +
        "AQUAAQAAAKoBAAAbAQUAAQAAALIBAAAcAQMAAQAAAAEAAAAoAQMAAQAAAAIAAAAAAAAAzAAAAAEAAADEAAAAAQAAAAAAAAAAAA==";

    public static Task RunAsync()
    {
        #region html-images
        var html = $$"""
            <style>
              .hero { height: 40mm; background: url(data:image/png;base64,{{Pixel}}) center / cover, #1a4f8b; }
              img.logo { width: 40mm; }
            </style>
            <div class="hero"></div>
            <img class="logo" alt="Logo" src="data:image/png;base64,{{Pixel}}">
            <svg width="60" height="60" viewBox="0 0 24 24"><circle cx="12" cy="12" r="10" fill="currentColor"/></svg>
            """;
        HtmlToPdf.Convert(html).Save("images.pdf");
        #endregion

        #region loader
        var http = new HttpClient();
        var options = new HtmlConverterOptions
        {
            BaseUri = new Uri("https://example.com/invoices/"),
            ResourceLoader = request => request.Uri.Host == "example.com"   // only what you allow
                ? http.GetByteArrayAsync(request.Uri, request.CancellationToken).GetAwaiter().GetResult()
                : ResourceLoaders.Default(request),
        };
        #endregion

        #region draw-images
        var doc = new PdfDocument();
        var page = doc.AddPage(PdfPageSize.A4);
        var logo = PdfImage.LoadSvg("""<svg viewBox="0 0 24 24"><circle cx="12" cy="12" r="10" fill="#1a4f8b"/></svg>""");
        page.Canvas.DrawImage(logo, x: 72, y: page.Height - 140, width: 60, height: 60); // drawn as vectors
        doc.Save("drawn-image.pdf");
        #endregion

        File.WriteAllBytes("fax.tif", Convert.FromBase64String(Fax));

        #region tiff-pages
        var fax = File.ReadAllBytes("fax.tif");
        var faxPdf = new PdfDocument();
        for (var i = 0; i < PdfImage.GetPageCount(fax); i++)
        {
            var image = PdfImage.Load(fax, i);  // a full fax page at 204 x 196 dpi comes out at its paper size
            var faxPage = faxPdf.AddPage(new PdfPageSize(image.NaturalWidth, image.NaturalHeight));
            faxPage.Canvas.DrawImage(image, 0, 0, faxPage.Width, faxPage.Height);
        }

        faxPdf.Save("fax.pdf");
        #endregion

        _ = options;
        return Task.CompletedTask;
    }
}
