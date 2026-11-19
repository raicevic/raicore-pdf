#pragma warning disable CA1849, CA2007
using System.IO.Compression;
using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Slides;

namespace Snippets;

internal static class Slides
{
    public static async Task RunAsync()
    {
        // A small presentation to convert: one slide with a title and a bulleted text box.
        WritePresentation("deck.pptx");

        #region convert
        var pdf = PptxToPdf.Convert("deck.pptx");
        pdf.Save("deck.pdf");
        #endregion

        #region options
        var options = new PptxConverterOptions
        {
            IncludeHiddenSlides = true,  // Hidden slides are left out by default.
            SlideNumbers = [1],          // Only these slides, numbered from 1; all by default.
            Title = "Quarterly review",  // The presentation's own title by default.
            Tagged = true,               // Headings, paragraphs, tables and figures in the structure tree.
        };
        await using (var pptx = File.OpenRead("deck.pptx"))
        {
            var document = await PptxToPdf.ConvertAsync(pptx, options);
            document.Save("deck-tagged.pdf");
        }
        #endregion

        #region fonts
        var fonts = BundledFonts.CreateCollection().AddSystemFonts();
        var withFonts = PptxToPdf.Convert("deck.pptx", new PptxConverterOptions
        {
            Fonts = fonts,
            FallbackFontFamilies = ["Noto Sans CJK JP", "Noto Sans Arabic"],
        });
        withFonts.Save("deck-fonts.pdf");
        #endregion

        #region diagnostics
        var report = PptxToPdf.Convert("deck.pptx", new PptxConverterOptions
        {
            Diagnostics = d => Console.WriteLine($"Slide {d.SlideNumber}: {d.Subject}: {d.Message}"),
        });
        report.Save("deck-report.pdf");
        #endregion
    }

    /// <summary>Writes a minimal presentation (a presentation program would write more parts, with the same structure).</summary>
    private static void WritePresentation(string path)
    {
        const string a = "http://schemas.openxmlformats.org/drawingml/2006/main";
        const string p = "http://schemas.openxmlformats.org/presentationml/2006/main";
        const string r = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        const string package = "http://schemas.openxmlformats.org/package/2006/relationships";
        const string ns = $"xmlns:a=\"{a}\" xmlns:p=\"{p}\" xmlns:r=\"{r}\"";
        static string Shape(int y, string text, int size) =>
            $"<p:sp><p:nvSpPr><p:cNvPr id=\"2\" name=\"Text\"/><p:cNvSpPr txBox=\"1\"/><p:nvPr/></p:nvSpPr><p:spPr><a:xfrm><a:off x=\"457200\" y=\"{y}\"/>"
            + "<a:ext cx=\"8229600\" cy=\"1143000\"/></a:xfrm><a:prstGeom prst=\"rect\"/></p:spPr><p:txBody><a:bodyPr/>"
            + $"{text.Split('|').Aggregate(string.Empty, (all, line) => all + $"<a:p><a:r><a:rPr sz=\"{size}\"/><a:t>{line}</a:t></a:r></a:p>")}</p:txBody></p:sp>";
        var parts = new Dictionary<string, string>
        {
            ["_rels/.rels"] = $"<Relationships xmlns=\"{package}\"><Relationship Id=\"r1\" Type=\"{r}/officeDocument\" Target=\"ppt/presentation.xml\"/></Relationships>",
            ["ppt/presentation.xml"] = $"<p:presentation {ns}><p:sldIdLst><p:sldId id=\"256\" r:id=\"r1\"/></p:sldIdLst><p:sldSz cx=\"9144000\" cy=\"6858000\"/></p:presentation>",
            ["ppt/_rels/presentation.xml.rels"] = $"<Relationships xmlns=\"{package}\"><Relationship Id=\"r1\" Type=\"{r}/slide\" Target=\"slides/slide1.xml\"/></Relationships>",
            ["ppt/slides/slide1.xml"] = $"<p:sld {ns}><p:cSld><p:spTree>{Shape(457200, "Quarterly review", 4400)}{Shape(1828800, "Revenue up|Costs down", 2400)}</p:spTree></p:cSld></p:sld>",
        };
        File.Delete(path);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, xml) in parts)
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open());
            writer.Write(xml);
        }
    }
}
