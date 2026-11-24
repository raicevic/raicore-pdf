using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Html;
using RaiCore.Pdf.Layout;

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
        var bold = PdfFont.Load(Fixtures.Path("LiberationSans-Bold.ttf"));
        canvas.DrawText("Liberation Sans Bold (TrueType)", bold, 12, 72, top - 54);

        // One glyph drawn for two texts: the Latin A's glyph also stands for a Greek capital alpha, which gets a code of
        // its own (a CIDToGIDMap stream maps it to the glyph) so that each extracts as itself.
        var a = ((OpenTypePdfFont)bold).Font.GetGlyphId('A');
        canvas.DrawGlyphs([new PositionedGlyph(a, 0.72, 0, 0, "A"), new PositionedGlyph(a, 0.72, 0, 0, "\u0391")], bold, 12, 300, top - 54);
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
        doc.NamedDestinations["second"] = new PdfDestination(second, top: second.Height - 72);

        // Links: a URI, a page and a named destination.
        first.AddLink(72, top - 340, 100, 14, new Uri("https://example.com/archive?q=pdf%2Fa"));
        first.AddNote(180, top - 340, 12, 12, "A note for archive readers").Author = "Archivist";
        canvas.DrawText("Visit example.com", StandardFont.Helvetica, 12, 72, top - 337);
        first.AddLink(72, top - 360, 100, 14, new PdfDestination(second));
        first.AddLink(72, top - 380, 100, 14, "second");

        doc.Outline.Add(new PdfOutlineItem("First page", first) { Children = { new PdfOutlineItem("Images", first, top - 120) } });
        doc.Outline.Add(new PdfOutlineItem("Second page", second));
        return doc;
    }

    /// <summary>
    /// An HTML document that uses most of what the converter writes, converted with <paramref name="level"/> (and as a
    /// tagged PDF/UA-1 document with <paramref name="pdfUA"/>).
    /// </summary>
    public static PdfDocument Html(PdfAConformance level, bool pdfUA = false)
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
              <p><img src="{{DataUri("image/png", "PngSuite/basn6a08.png")}}" width="48" height="48" alt="A color gradient">
                 <img src="{{DataUri("image/jpeg", "Images/rgb.jpg")}}" width="48" height="48" alt="Four colored squares"></p>
              <svg width="200" height="60" viewBox="0 0 200 60" aria-label="A label reading Inline SVG text">
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

        var doc = HtmlToPdf.Convert(html, new HtmlConverterOptions { PdfAConformance = level, Tagged = pdfUA, PdfUA = pdfUA });
        doc.Info.CreationDate = Created;
        return doc;
    }

    /// <summary>
    /// HTML for PDF/A level A: headings, a paragraph with a link, a list, an image with alt text, a decorative image, a
    /// table with header cells, an SVG figure with a title, and a paragraph in another language.
    /// </summary>
    public static readonly string LevelAHtml = $$"""
        <!DOCTYPE html>
        <html lang="en-GB">
        <head>
          <title>Accessible archive</title>
          <style>
            body { font-family: sans-serif; font-size: 11pt; }
            h1 { color: #234; border-bottom: 2px solid #234; }
            th, td { border: 1px solid #666; padding: 3px 8px; }
            th { background: #dde; }
            ul { color: #333; }
          </style>
        </head>
        <body>
          <h1>Sales report 2026</h1>
          <p>Sales grew in every region. The <a href="https://example.com/sales">full figures</a> are online.</p>
          <h2>Highlights</h2>
          <ul>
            <li>North grew by 12%</li>
            <li>South grew by 8%</li>
          </ul>
          <p><img src="data:image/jpeg;base64,{{Convert.ToBase64String(Fixtures.Read("Images/rgb.jpg"))}}" width="64" height="64" alt="Four colored squares">
             <img src="data:image/png;base64,{{Convert.ToBase64String(Fixtures.Read("PngSuite/basn6a08.png"))}}" width="16" height="16" alt=""></p>
          <table>
            <thead><tr><th>Region</th><th>Sales</th></tr></thead>
            <tbody><tr><td>North</td><td>1,120</td></tr><tr><td>South</td><td>1,080</td></tr></tbody>
          </table>
          <svg width="160" height="60" viewBox="0 0 160 60"><title>A bar chart of sales by region</title>
            <rect x="10" y="10" width="112" height="18" fill="#36c"/><rect x="10" y="34" width="108" height="18" fill="#c63"/>
          </svg>
          <p lang="da">Tak for i dag.</p>
        </body>
        </html>
        """;

    /// <summary><see cref="LevelAHtml"/> converted with a level A <paramref name="level"/>.</summary>
    public static PdfDocument HtmlLevelA(PdfAConformance level)
    {
        var doc = HtmlToPdf.Convert(LevelAHtml, new HtmlConverterOptions { PdfAConformance = level });
        doc.Info.CreationDate = Created;
        return doc;
    }

    // Opaque gradients, a shape with an opaque fill and stroke, and text: nothing that needs transparency (PDF/A-1).
    public const string OpaqueSvg = """
        <svg xmlns="http://www.w3.org/2000/svg" width="200" height="100" viewBox="0 0 200 100">
          <defs>
            <linearGradient id="fade"><stop offset="0" stop-color="#d33"/><stop offset="1" stop-color="#33d"/></linearGradient>
            <radialGradient id="glow"><stop offset="0" stop-color="#ff0"/><stop offset="1" stop-color="#f80"/></radialGradient>
          </defs>
          <rect width="200" height="60" fill="url(#fade)"/>
          <circle cx="170" cy="75" r="20" fill="url(#glow)" stroke="#333" stroke-width="2"/>
          <text x="10" y="95" font-family="serif" font-size="14" font-weight="bold">SVG text</text>
        </svg>
        """;

    /// <summary>
    /// A canvas-drawn document without transparency, for PDF/A-1: the fonts of <see cref="Canvas"/>, a 16-bit PNG, a
    /// PNG and a GIF whose pixels are fully transparent or opaque (stencil masks), a 16-bit PNG with a transparent color,
    /// JPEG, an opaque SVG, links of every kind, an outline, custom metadata and the language.
    /// </summary>
    public static PdfDocument Opaque(PdfAConformance level)
    {
        var doc = new PdfDocument { PdfAConformance = level, Language = "en-GB" }.EmbedStandardFonts();
        doc.Info.CreationDate = Created;
        doc.Info.Title = "PDF/A-1 sample";
        doc.Info.Author = "RaiCore.Pdf tests";
        doc.Info.Keywords = "archive, PDF/A-1";
        doc.Info.CustomProperties["Department"] = "Archive & Records";

        var first = doc.AddPage();
        var canvas = first.Canvas;
        var top = first.Height - 72;
        if (doc.Tagged)
        {
            canvas.BeginTag(doc.StructureRoot.AddChild(PdfStructureTypes.Paragraph));
        }

        canvas.DrawText("Helvetica: caf\u00E9 \u20AC 12 \u2013 \u201Cquoted\u201D", StandardFont.Helvetica, 12, 72, top);
        var bold = PdfFont.Load(Fixtures.Path("LiberationSans-Bold.ttf"));
        canvas.DrawText("Liberation Sans Bold (TrueType)", bold, 12, 72, top - 18);
        var a = ((OpenTypePdfFont)bold).Font.GetGlyphId('A'); // A Greek capital alpha drawn with the A: a code of its own.
        canvas.DrawGlyphs([new PositionedGlyph(a, 0.72, 0, 0, "A"), new PositionedGlyph(a, 0.72, 0, 0, "\u0391")], bold, 12, 300, top - 18);
        canvas.DrawText("RaiCore Sans 3 (CFF)", PdfFont.Load(Fixtures.Path("RaiCoreSans3-Subset.otf")), 12, 72, top - 36);
        canvas.DrawText("Noto Sans JP (CID-keyed CFF)", PdfFont.Load(Fixtures.Path("NotoSansJP-Subset.otf")), 12, 72, top - 54);
        canvas.DrawText("Visit example.com", StandardFont.Helvetica, 12, 72, top - 337);
        if (doc.Tagged)
        {
            canvas.EndTag();
        }

        var images = new (string File, string Description)[]
        {
            ("PngSuite/basn2c16.png", "A 16-bit RGB gradient"),
            ("PngSuite/tbbn3p08.png", "A palette image with transparent pixels"),
            ("PngSuite/tbbn2c16.png", "A 16-bit image with a transparent color"),
            ("Images/rgb.jpg", "Four colored squares"),
            ("Images/transparent.gif", "A GIF with a transparent color"),
            ("svg", "Gradients and text"),
        };
        for (var i = 0; i < images.Length; i++)
        {
            var (file, description) = images[i];
            var image = file == "svg" ? PdfImage.LoadSvg(OpaqueSvg) : PdfImage.Load(Fixtures.Path(file));
            var (x, y, width) = file == "svg" ? (72.0, top - 300, 200.0) : (72 + (i * 78.0), top - 160, 64.0);
            if (doc.Tagged)
            {
                var figure = doc.StructureRoot.AddChild(PdfStructureTypes.Figure);
                figure.AlternateText = description;
                canvas.BeginTag(figure);
            }

            canvas.DrawImage(image, x, y, width, file == "svg" ? 100 : 64);
            if (doc.Tagged)
            {
                canvas.EndTag();
            }
        }

        var second = doc.AddPage();
        if (doc.Tagged)
        {
            second.Canvas.BeginTag(doc.StructureRoot.AddChild(PdfStructureTypes.Heading1));
        }

        second.Canvas.DrawText("Second page", StandardFont.Helvetica, 18, 72, second.Height - 72);
        if (doc.Tagged)
        {
            second.Canvas.EndTag();
        }

        doc.NamedDestinations["second"] = new PdfDestination(second, top: second.Height - 72);
        first.AddLink(72, top - 340, 100, 14, new Uri("https://example.com/archive?q=pdf%2Fa"));
        first.AddLink(72, top - 360, 100, 14, new PdfDestination(second));
        first.AddLink(72, top - 380, 100, 14, "second");

        doc.Outline.Add(new PdfOutlineItem("First page", first) { Children = { new PdfOutlineItem("Images", first, top - 120) } });
        doc.Outline.Add(new PdfOutlineItem("Second page", second));
        return doc;
    }

    /// <summary>
    /// An invoice-like HTML document without transparency, for PDF/A-1 (and level A: alt text, a table with header, body
    /// and footer rows, a list, languages): a web font, an opaque gradient, JPEG, a 16-bit PNG, a GIF with a transparent
    /// color, an SVG figure and links.
    /// </summary>
    public static readonly string OpaqueHtml = $$"""
        <!DOCTYPE html>
        <html lang="en-GB">
        <head>
          <title>Invoice 2026-117</title>
          <meta name="author" content="RaiCore.Pdf tests">
          <style>
            @font-face { font-family: Brand; src: url(data:font/woff2;base64,{{Convert.ToBase64String(Fixtures.Read("LiberationSans-Regular.woff2"))}}) format("woff2"); }
            body { font-family: serif; font-size: 10pt; }
            h1 { font-family: Brand; color: #234; border-bottom: 2px solid #234; }
            .banner { height: 24px; background: linear-gradient(to right, #c33, #33c); border-radius: 6px; }
            table { border-collapse: collapse; width: 100%; }
            th, td { border: 1px solid #666; padding: 3px 6px; }
            th { background: #dde; text-align: left; }
            td.num, th.num { text-align: right; }
            code { font-family: monospace; }
          </style>
        </head>
        <body>
          <h1>Invoice 2026-117</h1>
          <div class="banner"></div>
          <p>Thank you for your order. The <a href="https://example.com/terms">terms</a> apply; see the <a href="#totals">totals</a>
             and reference <code>INV-2026-117</code>.</p>
          <p><img src="data:image/jpeg;base64,{{Convert.ToBase64String(Fixtures.Read("Images/rgb.jpg"))}}" width="48" height="48" alt="Four colored squares">
             <img src="data:image/png;base64,{{Convert.ToBase64String(Fixtures.Read("PngSuite/basn2c16.png"))}}" width="48" height="48" alt="A color gradient">
             <img src="data:image/gif;base64,{{Convert.ToBase64String(Fixtures.Read("Images/transparent.gif"))}}" width="48" height="48" alt="A GIF with a transparent color"></p>
          <table>
            <thead><tr><th>Item</th><th class="num">Qty</th><th class="num">Price</th></tr></thead>
            <tbody>
              <tr><td>Archive boxes</td><td class="num">4</td><td class="num">60.00</td></tr>
              <tr><td>Labels</td><td class="num">2</td><td class="num">40.00</td></tr>
            </tbody>
            <tfoot><tr><td>Net</td><td></td><td class="num">100.00</td></tr></tfoot>
          </table>
          <h2 id="totals">Totals</h2>
          <ul><li>Net: 100.00</li><li>VAT: 25.00</li><li>Total: 125.00</li></ul>
          <svg width="160" height="60" viewBox="0 0 160 60"><title>A bar chart of the items</title>
            <rect x="10" y="10" width="120" height="18" fill="#36c"/><rect x="10" y="34" width="80" height="18" fill="#c63"/>
          </svg>
          <p lang="da">Tak for handlen.</p>
        </body>
        </html>
        """;

    /// <summary><see cref="OpaqueHtml"/> converted with <paramref name="level"/> (tagged as PDF/UA-1 with <paramref name="pdfUA"/>).</summary>
    public static PdfDocument HtmlOpaque(PdfAConformance level, bool pdfUA = false)
    {
        var doc = HtmlToPdf.Convert(OpaqueHtml, new HtmlConverterOptions { PdfAConformance = level, Tagged = pdfUA, PdfUA = pdfUA });
        doc.Info.CreationDate = Created;
        return doc;
    }

    /// <summary>A layout API document (heading, paragraph, figure, table with a header row) rendered with <paramref name="level"/>.</summary>
    public static PdfDocument Layout(PdfAConformance level)
    {
        var style = new TextStyle(PdfFont.Load(Fixtures.Path("LiberationSans-Regular.ttf")), 11);
        var flow = new FlowDocument(PageSetup.A4);
        flow.Add(new ParagraphBlock(new Paragraph(style with { FontSize = 20 }).Add("Archived report")) { Tag = StructureTag.Heading(1) });
        flow.Add(new ParagraphBlock(new Paragraph(style).Add("Laid out with the layout API: caf\u00E9, \u20AC 12, \u00E6\u00F8\u00E5.")));
        flow.Add(new ImageBlock(PdfImage.Load(Fixtures.Path("Images/rgb.jpg")), 64, 64)
        {
            Tag = new StructureTag(PdfStructureTypes.Figure) { AlternateText = "Four colored squares" },
        });
        var table = new TableBlock().Add(new TableRow(new TableCell(new Paragraph(style).Add("Region")), new TableCell(new Paragraph(style).Add("Sales"))) { IsHeader = true });
        table.Add(new TableRow(new TableCell(new Paragraph(style).Add("North")), new TableCell(new Paragraph(style).Add("1,120"))));
        table.Add(new TableRow(new TableCell(new Paragraph(style).Add("South")), new TableCell(new Paragraph(style).Add("1,080"))));
        flow.Add(table);
        flow.PageDecorator = (canvas, info) => canvas.DrawText($"Page {info.Number} of {info.Count}", style.Fonts[0], 8, 500, 20);

        var doc = new PdfDocument { PdfAConformance = level, Language = "en-GB", Info = { Title = "Archived report", CreationDate = Created } };
        flow.RenderTo(doc);
        return doc;
    }
}
