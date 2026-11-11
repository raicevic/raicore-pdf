using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Html;

namespace RaiCore.Pdf.Tests.PdfA;

/// <summary>
/// Documents that exercise what PDF/A constrains: text in standard (substituted), TrueType and CFF fonts, PNG with
/// alpha, JPEG (RGB and gray), GIF with a transparent color, SVG with text, gradients and opacity, transparency groups,
/// translucent colors, links of every kind, an outline, custom metadata and the language.
/// </summary>
internal static class PdfASamples
{
    public static readonly DateTimeOffset Created = new(2026, 11, 11, 20, 15, 0, TimeSpan.FromHours(1));

    // A gradient with translucent stops (a soft mask), a radial gradient, group opacity and text in a standard font.
    public const string Svg = """
        <svg xmlns="http://www.w3.org/2000/svg" width="200" height="100" viewBox="0 0 200 100">
          <defs>
            <linearGradient id="fade"><stop offset="0" stop-color="#d33"/><stop offset="1" stop-color="#33d" stop-opacity="0.3"/></linearGradient>
            <radialGradient id="glow"><stop offset="0" stop-color="#ff0"/><stop offset="1" stop-color="#f80"/></radialGradient>
          </defs>
          <rect width="200" height="60" fill="url(#fade)"/>
          <circle cx="170" cy="75" r="20" fill="url(#glow)"/>
          <g opacity="0.6"><rect x="10" y="50" width="60" height="40" fill="#0a0"/><rect x="40" y="60" width="60" height="30" fill="#00a"/></g>
          <text x="110" y="95" font-family="serif" font-size="14" font-weight="bold">SVG text</text>
        </svg>
        """;

    /// <summary>A canvas-drawn document with <paramref name="level"/> and the bundled fonts in place of the standard fonts.</summary>
    public static PdfDocument Canvas(PdfAConformance level)
    {
        var doc = new PdfDocument { PdfAConformance = level, Language = "en-GB" }.EmbedStandardFonts();
        doc.Info.CreationDate = Created;
        doc.Info.Title = "PDF/A sample";
        doc.Info.Author = "RaiCore.Pdf tests";
        doc.Info.Subject = "Everything PDF/A constrains";
        doc.Info.Keywords = "archive, PDF/A";
        doc.Info.Creator = "PdfASamples";
        doc.Info.CustomProperties["Department"] = "Archive & Records";

        var first = doc.AddPage();
        var canvas = first.Canvas;
        var top = first.Height - 72;

        // Former standard fonts (embedded as Liberation), including WinAnsi characters outside ASCII, a no-break space
        // and a soft hyphen (codes A0 and AD, which WinAnsi names "space" and "hyphen").
        canvas.DrawText("Helvetica: caf\u00E9 \u20AC 12 \u2013 \u201Cquoted\u201D \u00A0no-break\u00ADsoft", StandardFont.Helvetica, 12, 72, top);
        canvas.DrawText("Times Bold: \u00C6bler og p\u00E6rer", StandardFont.TimesBold, 12, 72, top - 18);
        canvas.DrawText("Courier Oblique: 0123456789", StandardFont.CourierOblique, 12, 72, top - 36);

        // Embedded fonts: TrueType (Identity-H CIDFontType2) and CFF (CIDFontType0C subsets).
        canvas.DrawText("Liberation Sans Bold (TrueType)", PdfFont.Load(Fixtures.Path("LiberationSans-Bold.ttf")), 12, 72, top - 54);
        canvas.DrawText("RaiCore Sans 3 (CFF)", PdfFont.Load(Fixtures.Path("RaiCoreSans3-Subset.otf")), 12, 72, top - 72);
        canvas.DrawText("Noto Sans JP (CID-keyed CFF)", PdfFont.Load(Fixtures.Path("NotoSansJP-Subset.otf")), 12, 72, top - 90);

        // Images: PNG with an alpha channel, JPEG in RGB and gray, GIF with a transparent color (a color key mask).
        canvas.DrawImage(PdfImage.Load(Fixtures.Path("PngSuite/basn6a08.png")), 72, top - 180, 64, 64);
        canvas.DrawImage(PdfImage.Load(Fixtures.Path("Images/rgb.jpg")), 150, top - 180, 64, 64);
        canvas.DrawImage(PdfImage.Load(Fixtures.Path("Images/gray.jpg")), 228, top - 180, 64, 64);
        canvas.DrawImage(PdfImage.Load(Fixtures.Path("Images/transparent.gif")), 306, top - 180, 64, 64);

        // SVG: gradients (one with a soft mask), group opacity and text in a standard font.
        canvas.DrawImage(PdfImage.LoadSvg(Svg), 72, top - 300, 200, 100);

        // A transparency group and a translucent color.
        canvas.BeginGroup(0.5)
            .SetFillColor(new PdfColor(0.9, 0.2, 0.2)).Rectangle(300, top - 300, 80, 60).Fill()
            .SetFillColor(new PdfColor(0.2, 0.2, 0.9)).Rectangle(330, top - 280, 80, 60).Fill()
            .EndGroup();
        canvas.SetFillColor(new PdfColor(0, 0.6, 0, 0.4)).Rectangle(420, top - 300, 80, 60).Fill().SetFillColor(PdfColor.Black);

        var second = doc.AddPage();
        second.Canvas.DrawText("Second page", StandardFont.Helvetica, 18, 72, second.Height - 72);
        doc.NamedDestinations["second"] = new PdfDestination(second, second.Height - 72);

        // Links: a URI, a page and a named destination.
        first.AddLink(72, top - 340, 100, 14, new Uri("https://example.com/archive?q=pdf%2Fa"));
        canvas.DrawText("Visit example.com", StandardFont.Helvetica, 12, 72, top - 337);
        first.AddLink(72, top - 360, 100, 14, new PdfDestination(second));
        first.AddLink(72, top - 380, 100, 14, "second");

        doc.Outline.Add(new PdfOutlineItem("First page", first) { Children = { new PdfOutlineItem("Images", first, top - 120) } });
        doc.Outline.Add(new PdfOutlineItem("Second page", second));
        return doc;
    }

    /// <summary>An HTML document that uses most of what the converter writes, converted with <paramref name="level"/>.</summary>
    public static PdfDocument Html(PdfAConformance level)
    {
        static string DataUri(string mime, string fixture) => $"data:{mime};base64,{Convert.ToBase64String(Fixtures.Read(fixture))}";

        var html = $$"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
              <title>PDF/A from HTML</title>
              <meta name="author" content="RaiCore.Pdf tests">
              <style>
                @font-face { font-family: Brand; src: url({{DataUri("font/woff2", "LiberationSans-Regular.woff2")}}) format("woff2"); }
                body { font-family: serif; font-size: 11pt; }
                h1 { font-family: Brand; color: #234; }
                .banner { height: 40px; background: linear-gradient(to right, #c33, rgba(51, 51, 204, 0.3)); border-radius: 8px; }
                .faded { opacity: 0.5; background: #9c9; padding: 6px; }
                .tint { background: rgba(255, 0, 0, 0.25); padding: 6px; }
                table { border-collapse: collapse; } td { border: 1px solid #666; padding: 3px 6px; }
                code { font-family: monospace; }
              </style>
            </head>
            <body>
              <h1 id="top">Archived invoice</h1>
              <div class="banner"></div>
              <p>Plain text in Liberation Serif, <b>bold</b>, <i>italic</i> and <code>monospace</code>, with a
                 <a href="https://example.com/terms">link to the terms</a> and a <a href="#totals">link to the totals</a>.</p>
              <p><img src="{{DataUri("image/png", "PngSuite/basn6a08.png")}}" width="48" height="48">
                 <img src="{{DataUri("image/jpeg", "Images/rgb.jpg")}}" width="48" height="48"></p>
              <svg width="200" height="60" viewBox="0 0 200 60">
                <rect width="200" height="60" rx="6" fill="#eef"/>
                <text x="10" y="35" font-size="16">Inline SVG text</text>
              </svg>
              <div class="faded">A translucent box (opacity 0.5).</div>
              <div class="tint">A box with a translucent background color.</div>
              <h2 id="totals">Totals</h2>
              <table><tr><td>Net</td><td>100.00</td></tr><tr><td>VAT</td><td>25.00</td></tr></table>
            </body>
            </html>
            """;

        var doc = HtmlToPdf.Convert(html, new HtmlConverterOptions { PdfAConformance = level });
        doc.Info.CreationDate = Created;
        return doc;
    }
}
