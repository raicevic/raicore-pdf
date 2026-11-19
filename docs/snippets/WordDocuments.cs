#pragma warning disable CA1849, CA2007
using System.IO.Compression;
using RaiCore.Pdf;
using RaiCore.Pdf.Documents;
using RaiCore.Pdf.Fonts;

namespace Snippets;

internal static class WordDocuments
{
    public static async Task RunAsync()
    {
        // A small document to convert: a heading, a paragraph and a numbered list (a word processor writes the same parts).
        WriteDocument("letter.docx");

        #region convert
        var pdf = DocxToPdf.Convert("letter.docx");
        pdf.Save("letter.pdf");
        #endregion

        #region options
        var options = new DocxConverterOptions
        {
            Tagged = true,                  // Headings, paragraphs, tables and images in the PDF's structure.
            HeadingBookmarks = true,        // Headings as the PDF's bookmarks (the default).
            Title = "Letter",               // Instead of the title of the document's properties.
            Limits = ResourceLimits.Default with { MaxPages = 500, Timeout = TimeSpan.FromSeconds(30) },
            Diagnostics = d => Console.WriteLine($"  {d.Kind}: {d.Message}"),
        };
        await using (var docx = File.OpenRead("letter.docx"))
        {
            var document = await DocxToPdf.ConvertAsync(docx, options);
            document.Save("letter-tagged.pdf");
        }
        #endregion

        #region fonts
        // Word's own fonts (Calibri, Cambria, Aptos) from the system, or their metric-compatible substitutes Carlito and
        // Caladea when installed; CJK and Arabic text from fallback fonts.
        var fonts = BundledFonts.CreateCollection().AddSystemFonts();
        var withFonts = DocxToPdf.Convert("letter.docx", new DocxConverterOptions
        {
            Fonts = fonts,
            FallbackFontFamilies = ["Noto Sans CJK JP", "Noto Sans Arabic"],
        });
        withFonts.Save("letter-fonts.pdf");
        #endregion

        // An OpenDocument text document to convert, as LibreOffice writes it (a ZIP package, mimetype first).
        WriteOpenDocument("quarterly.odt");

        #region odt
        var report = OdtToPdf.Convert("quarterly.odt", new OdtConverterOptions
        {
            Tagged = true,
            Diagnostics = d => Console.WriteLine($"  {d.Kind}: {d.Message}"),
        });
        report.Save("quarterly.pdf");
        #endregion
    }

    /// <summary>Writes a minimal OpenDocument text document: a heading, a paragraph, a numbered list and a footer.</summary>
    private static void WriteOpenDocument(string path)
    {
        const string ns = "xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\" xmlns:style=\"urn:oasis:names:tc:opendocument:xmlns:style:1.0\" "
            + "xmlns:text=\"urn:oasis:names:tc:opendocument:xmlns:text:1.0\" xmlns:fo=\"urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0\"";
        var parts = new Dictionary<string, string>
        {
            ["mimetype"] = "application/vnd.oasis.opendocument.text",
            ["styles.xml"] = $"<office:document-styles {ns}><office:styles><style:style style:name=\"Heading_20_1\" style:family=\"paragraph\">"
                + "<style:text-properties fo:font-size=\"16pt\" fo:font-weight=\"bold\" fo:color=\"#2f5496\"/></style:style></office:styles><office:automatic-styles>"
                + "<style:page-layout style:name=\"pm1\"><style:page-layout-properties fo:page-width=\"21cm\" fo:page-height=\"29.7cm\" fo:margin=\"2cm\"/></style:page-layout>"
                + "</office:automatic-styles><office:master-styles><style:master-page style:name=\"Standard\" style:page-layout-name=\"pm1\">"
                + "<style:footer><text:p>Page <text:page-number>1</text:page-number></text:p></style:footer></style:master-page></office:master-styles></office:document-styles>",
            ["content.xml"] = $"<office:document-content {ns}><office:automatic-styles><text:list-style style:name=\"L1\">"
                + "<text:list-level-style-number text:level=\"1\" style:num-format=\"1\" style:num-suffix=\".\"/></text:list-style></office:automatic-styles>"
                + "<office:body><office:text><text:h text:style-name=\"Heading_20_1\" text:outline-level=\"1\">Quarterly report</text:h>"
                + "<text:p>Sales grew in every region:</text:p><text:list text:style-name=\"L1\"><text:list-item><text:p>North, 12%</text:p></text:list-item>"
                + "<text:list-item><text:p>South, 8%</text:p></text:list-item></text:list></office:text></office:body></office:document-content>",
        };
        File.Delete(path);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, xml) in parts)
        {
            using var writer = new StreamWriter(zip.CreateEntry(name, name == "mimetype" ? CompressionLevel.NoCompression : CompressionLevel.Optimal).Open());
            writer.Write(xml);
        }
    }

    /// <summary>Writes a minimal Word document: styles with a heading, a paragraph and a numbered list.</summary>
    private static void WriteDocument(string path)
    {
        const string main = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        const string relationships = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        const string package = "http://schemas.openxmlformats.org/package/2006/relationships";
        var parts = new Dictionary<string, string>
        {
            ["_rels/.rels"] = $"<Relationships xmlns=\"{package}\"><Relationship Id=\"r1\" Type=\"{relationships}/officeDocument\" Target=\"word/document.xml\"/></Relationships>",
            ["word/_rels/document.xml.rels"] = $"<Relationships xmlns=\"{package}\"><Relationship Id=\"r1\" Type=\"{relationships}/styles\" Target=\"styles.xml\"/>"
                + $"<Relationship Id=\"r2\" Type=\"{relationships}/numbering\" Target=\"numbering.xml\"/></Relationships>",
            ["word/styles.xml"] = $"<w:styles xmlns:w=\"{main}\"><w:docDefaults><w:rPrDefault><w:rPr><w:rFonts w:ascii=\"Arial\"/><w:sz w:val=\"22\"/></w:rPr></w:rPrDefault>"
                + "<w:pPrDefault><w:pPr><w:spacing w:after=\"160\"/></w:pPr></w:pPrDefault></w:docDefaults>"
                + "<w:style w:type=\"paragraph\" w:styleId=\"Heading1\"><w:name w:val=\"heading 1\"/><w:pPr><w:keepNext/><w:outlineLvl w:val=\"0\"/></w:pPr>"
                + "<w:rPr><w:b/><w:color w:val=\"2F5496\"/><w:sz w:val=\"32\"/></w:rPr></w:style></w:styles>",
            ["word/numbering.xml"] = $"<w:numbering xmlns:w=\"{main}\"><w:abstractNum w:abstractNumId=\"0\"><w:lvl w:ilvl=\"0\"><w:start w:val=\"1\"/><w:numFmt w:val=\"decimal\"/>"
                + "<w:lvlText w:val=\"%1.\"/><w:pPr><w:ind w:left=\"720\" w:hanging=\"360\"/></w:pPr></w:lvl></w:abstractNum><w:num w:numId=\"1\"><w:abstractNumId w:val=\"0\"/></w:num></w:numbering>",
            ["word/document.xml"] = $"<w:document xmlns:w=\"{main}\"><w:body>"
                + "<w:p><w:pPr><w:pStyle w:val=\"Heading1\"/></w:pPr><w:r><w:t>Order confirmation</w:t></w:r></w:p>"
                + "<w:p><w:r><w:t>Thank you for your order. It contains:</w:t></w:r></w:p>"
                + "<w:p><w:pPr><w:numPr><w:ilvl w:val=\"0\"/><w:numId w:val=\"1\"/></w:numPr></w:pPr><w:r><w:t>Coffee, 2 kg</w:t></w:r></w:p>"
                + "<w:p><w:pPr><w:numPr><w:ilvl w:val=\"0\"/><w:numId w:val=\"1\"/></w:numPr></w:pPr><w:r><w:t>Tea, 500 g</w:t></w:r></w:p>"
                + "</w:body></w:document>",
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
