#pragma warning disable CA1849, CA2007
using RaiCore.Pdf;
using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Html;

namespace Snippets;

internal static class Fonts
{
    public static Task RunAsync()
    {
        #region shared-collection
        // Create once at startup and share: fonts are parsed once and are thread-safe.
        var fonts = BundledFonts.CreateCollection();                    // the same output on every machine
        // var fonts = new FontCollection().AddSystemFonts().AddBundledFonts();   // installed fonts too
        // var fonts = BundledFonts.CreateCollection().AddDirectory("fonts");     // your own font files

        var options = new HtmlConverterOptions
        {
            Fonts = fonts,
            FallbackFontFamilies = ["DejaVu Sans"],                    // tried for characters the chosen fonts lack
        };
        #endregion

        #region font-face
        var html = """
            <style>
              @font-face {
                font-family: Brand;
                src: url(fonts/brand.woff2) format("woff2"), url(fonts/brand.ttf);
                font-weight: 100 900;                                  /* a variable font */
              }
              h1 { font-family: Brand, Arial, sans-serif; }
            </style>
            <h1>Annual report</h1>
            """;
        HtmlToPdf.Convert(html, options).Save("fonts.pdf");
        #endregion

        #region find
        PdfFont heading = fonts.Find("Arial", weight: 700)!;           // Liberation Sans Bold
        PdfFont body = fonts.Find(["Inter", "Helvetica", "sans-serif"])!; // the first family that is there
        #endregion

        _ = (heading, body);
        return Task.CompletedTask;
    }
}
