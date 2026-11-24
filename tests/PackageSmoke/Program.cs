// Installs the RaiCore.Pdf packages from artifacts/ (see nuget.config) and runs the README samples against them.
// Usage: dotnet run --project tests/PackageSmoke [output-directory]
// CI also publishes it trimmed and with Native AOT (see the "aot" job in .github/workflows/ci.yml) and runs the binary.
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using PackageSmoke;
using RaiCore.Pdf;
using RaiCore.Pdf.Editing;
using RaiCore.Pdf.Email;
using RaiCore.Pdf.Documents;
using RaiCore.Pdf.Sheets;
using RaiCore.Pdf.Slides;
using RaiCore.Pdf.Extraction;
using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Html;
using RaiCore.Pdf.Layout;
using RaiCore.Pdf.Layout.Text;
using RaiCore.Pdf.Razor;
using RaiCore.Pdf.Xfa;

var output = Directory.CreateDirectory(args.Length > 0 ? args[0] : "smoke-output").FullName;
var failures = 0;

// The assemblies must come from the restored packages, not from project references or the repository's bin folders.
foreach (var assembly in new[] { typeof(PdfDocument), typeof(BundledFonts), typeof(FlowDocument), typeof(HtmlToPdf), typeof(RazorToPdf), typeof(TextExtractor) }.Select(t => t.Assembly))
{
    var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
    Console.WriteLine($"{assembly.GetName().Name} {version}");
}

Console.WriteLine($"{RuntimeInformation.FrameworkDescription} on {RuntimeInformation.RuntimeIdentifier}, " +
    $"Native AOT: {(RuntimeFeature.IsDynamicCodeSupported ? "no" : "yes")}");

// The project file records the package folder it restored into, so the check also works for published binaries.
var packageCache = typeof(Invoice).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "PackageCache").Value!;
Check("packages were restored into the private cache", Directory.Exists(Path.Combine(packageCache, "raicore.pdf.razor")));

// The core packages share internals, so each depends on the others at exactly its own version (#62).
var pins = 0;
foreach (var nuspec in Directory.GetFiles(packageCache, "raicore.pdf*.nuspec", SearchOption.AllDirectories))
{
    var packageVersion = Path.GetFileName(Path.GetDirectoryName(nuspec)!);
    foreach (var dependency in System.Xml.Linq.XDocument.Load(nuspec).Descendants()
        .Where(e => e.Name.LocalName == "dependency" && ((string?)e.Attribute("id"))?.StartsWith("RaiCore.Pdf", StringComparison.Ordinal) == true))
    {
        var range = (string?)dependency.Attribute("version");
        Check($"{Path.GetFileNameWithoutExtension(nuspec)} {packageVersion} depends on {(string?)dependency.Attribute("id")} {range}, exactly its own version",
            string.Equals(range, $"[{packageVersion}]", StringComparison.OrdinalIgnoreCase));
        pins++;
    }
}

Check($"the core packages depend on each other ({pins} dependencies)", pins >= 4);

// RaiCore.Pdf: drawing directly, with a bundled font and a PNG.
var logo = PdfImage.Load(Convert.FromBase64String(
    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg=="));
var doc = new PdfDocument();
var page = doc.AddPage(PdfPageSize.A4);
page.Canvas
    .SetFillColor(PdfColor.FromRgb(220, 30, 30))
    .Rectangle(72, 600, 200, 100)
    .Fill()
    .SetFillColor(PdfColor.Black)
    .DrawText("Hello from RaiCore.Pdf", BundledFonts.CreateCollection().Find("Arial")!, 24, 72, page.Height - 80)
    .DrawImage(logo, x: 400, y: 760, width: 120, height: 40);
Save("drawing", doc, expectedPages: 1, embedsFonts: true);

// RaiCore.Pdf.Fonts.
var fonts = BundledFonts.CreateCollection();
Check("Arial bold resolves to Liberation Sans Bold", fonts.Find("Arial", weight: 700)?.PostScriptName == "LiberationSans-Bold");
Check("a fallback list finds a sans-serif font", fonts.Find(["Inter", "Helvetica", "sans-serif"]) is not null);

// RaiCore.Pdf.Layout: HarfBuzz natives must load from the package on this OS.
var body = new TextStyle(fonts.Find("serif")!, 11) { LineHeight = LineHeight.Multiple(1.4) };
var flow = new FlowDocument(new PageSetup(PdfPageSize.A4, Thickness.All(56)));
for (var i = 0; i < 120; i++)
{
    flow.Add(new ParagraphBlock(new Paragraph(body) { Alignment = TextAlignment.Justify }.Add($"Paragraph {i}: text that flows across pages, shaped by HarfBuzz.")));
}

flow.PageDecorator = (canvas, info) => canvas.DrawText($"Page {info.Number} of {info.Count}", fonts.Find("sans-serif")!, 8, 56, 30);
Save("layout", flow.Render(), expectedPages: 2, embedsFonts: true, atLeast: true);

// RaiCore.Pdf.Html.
var html = HtmlToPdf.Convert("""
    <style>
      @page { size: A4; margin: 20mm; @bottom-right { content: "Page " counter(page) " of " counter(pages); } }
      body { font-family: Arial, sans-serif; }
    </style>
    <h1>Invoice 2026-0042</h1>
    <table border="1"><tr><th>Item</th><th>Price</th></tr><tr><td>Consulting</td><td>1.234,50 kr.</td></tr></table>
    """);
Save("html", html, expectedPages: 1, embedsFonts: true);

// Code paths that trimming or Native AOT could break without a build warning: embedded resources (hyphenation
// patterns), CSS layout through AngleSharp, SVG, compression, reading and importing PDFs, and AES encryption.
var english = Hyphenator.ForLanguage("en-US");
Check("hyphenation patterns load from the embedded resources", english?.Hyphenate("hyphenation").Count > 0);
var features = HtmlToPdf.Convert("""
    <html lang="en">
    <style>
      body { font-family: "Times New Roman", serif; }
      p { width: 140px; hyphens: auto; text-align: justify; }
      .row { display: flex; gap: 8px; }
      .row > div { flex: 1; padding: 4px; border: 1px solid #336; border-radius: 4px; background: linear-gradient(#eef, #ccf); }
    </style>
    <h2>Features</h2>
    <p>Internationalization notwithstanding, extraordinarily incomprehensible characteristics hyphenate.</p>
    <div class="row"><div>One</div><div>Two</div><div><svg width="60" height="20"><rect width="60" height="20" rx="4" fill="teal"/></svg></div></div>
    <ol><li>First</li><li>Second</li></ol>
    </html>
    """);
Save("features", features, expectedPages: 1, embedsFonts: true);

var source = PdfReader.Open(html.ToArray());
var merged = new PdfDocument();
merged.ImportPages(source);
merged.AddPage(PdfPageSize.A4).Canvas
    .DrawPage(source.Pages[0], 0, 0)
    .DrawImage(PdfImage.LoadSvg("""<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 10 10"><circle cx="5" cy="5" r="4" fill="red"/></svg>"""), 400, 700, 100, 100);
merged.Encryption = new PdfEncryption { OwnerPassword = "owner", Permissions = PdfPermissions.Print };
Save("import-encrypted", merged, expectedPages: 2, embedsFonts: true);
Check("import-encrypted: is encrypted", System.Text.Encoding.Latin1.GetString(merged.ToArray()).Contains("/Encrypt", StringComparison.Ordinal));

// RaiCore.Pdf.Razor: static API and dependency injection.
var invoice = new Invoice("2026-0042", ["Consulting", "Support"]);
var razor = await RazorToPdf.ConvertAsync<InvoiceDocument>(new Dictionary<string, object?> { [nameof(InvoiceDocument.Invoice)] = invoice });
Check("Razor title", razor.Info.Title == "Invoice 2026-0042");
Save("razor", razor, expectedPages: 1, embedsFonts: true);

var pricedHtml = await RazorToPdf.RenderHtmlAsync<PricedDocument>(new Dictionary<string, object?> { [nameof(PricedDocument.Currency)] = "EUR" });
Check("child component with a cascading parameter", pricedHtml.Contains("Consulting: 1234.50 EUR", StringComparison.Ordinal));

await using var services = new ServiceCollection()
    .AddScoped<ICustomerDirectory, CustomerDirectory>()
    .AddRazorPdf()
    .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
await using (var scope = services.CreateAsyncScope())
{
    var renderer = scope.ServiceProvider.GetRequiredService<RazorPdfRenderer>();
    var injectedHtml = await renderer.RenderHtmlAsync<CustomerInvoice>(new Dictionary<string, object?> { [nameof(CustomerInvoice.CustomerId)] = 7 });
    Check("injected service output", injectedHtml.Contains("Invoice for Contoso A/S", StringComparison.Ordinal));
    Save("razor-di", await renderer.ConvertAsync<CustomerInvoice>(new Dictionary<string, object?> { [nameof(CustomerInvoice.CustomerId)] = 7 }), expectedPages: 1, embedsFonts: true);
}

// RaiCore.Pdf.Extraction: the text of a converted document, read back (its glyph and Unicode tables are embedded).
var extracted = new TextExtractor(PdfReader.Open(HtmlToPdf.ConvertToBytes("<h1>Smoke test</h1><p>Text \uFB01nds its way back, \u05E9\u05DC\u05D5\u05DD.</p>"))).ExtractPage(0);
Check($"text extraction: {extracted.Text.ReplaceLineEndings(" | ")}", extracted.Text == "Smoke test\n\nText finds its way back, \u05E9\u05DC\u05D5\u05DD.");

// Search (plain text and a regular expression) and image extraction with PNG export, which trimming and AOT must keep.
var searchSource = PdfReader.Open(HtmlToPdf.ConvertToBytes(
    "<p>Invoice 1042 for {{customer}}</p><img style='width: 60pt' src='data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg=='>"));
var searchFound = new TextExtractor(searchSource).Find("{{customer}}").Single();
var searchNumber = new TextExtractor(searchSource).Find(new System.Text.RegularExpressions.Regex(@"Invoice \d+")).Single();
var searchImages = new ImageExtractor(searchSource).ExtractPage(0).Images;
var searchPng = searchImages.Count == 1 ? searchImages[0].GetData() : [];
Check(
    $"search and images: {searchFound.Text} at {searchFound.Bounds.X:0},{searchFound.Bounds.Y:0}; {searchNumber.Text}; {searchImages.Count} image, {searchPng.Length} bytes of {searchImages.FirstOrDefault()?.Format}",
    searchFound.Bounds.Width > 0 && searchNumber.Text == "Invoice 1042" && searchImages.Count == 1 && searchPng.AsSpan().StartsWith((byte[])[0x89, (byte)'P', (byte)'N', (byte)'G']));

// RaiCore.Pdf.Editing: delete, insert and move pages of a converted document, and save it twice.
var editor = PdfEditor.Open(HtmlToPdf.ConvertToBytes("<p>One</p><p style='break-before: page'>Two</p>"));
editor.Pages.Delete(0);
editor.Pages.Add(PdfReader.Open(HtmlToPdf.ConvertToBytes("<p>Three</p>")));
editor.Pages.Move(1, 0);
var edited = string.Join(' ', new TextExtractor(PdfReader.Open(editor.ToArray())).ExtractPages().Select(p => p.Text));
Check($"editing: {edited}", edited == "Three Two" && editor.ToArray().AsSpan().SequenceEqual(editor.ToArray()));

// RaiCore.Pdf.Email: a message with a code page subject, a cid: image in its HTML and a PDF attachment, appended.
var png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg=="; // The PNG above.
var message = System.Text.Encoding.ASCII.GetBytes($"""
    From: Smoke <smoke@example.com>
    Subject: =?windows-1252?Q?Caf=E9_=96_smoke?=
    Content-Type: multipart/mixed; boundary=m

    --m
    Content-Type: multipart/related; boundary=r

    --r
    Content-Type: text/html; charset=utf-8

    <p>Body text</p><img src="cid:dot@example.com" width="10" height="10">
    --r
    Content-Type: image/png
    Content-ID: <dot@example.com>
    Content-Transfer-Encoding: base64

    {png}
    --r--
    --m
    Content-Type: application/pdf
    Content-Disposition: attachment; filename*=utf-8''r%C3%A9sum%C3%A9.pdf
    Content-Transfer-Encoding: base64

    {Convert.ToBase64String(HtmlToPdf.ConvertToBytes("<p>Attached</p>"))}
    --m--
    """.ReplaceLineEndings("\r\n"));
var email = EmailToPdf.Convert(message, new EmailConverterOptions { AppendAttachments = true });
var emailText = string.Join(" | ", new TextExtractor(PdfReader.Open(email.ToArray())).ExtractPages().Select(p => p.Text.ReplaceLineEndings(" ")));
Check($"email: {emailText}", emailText.StartsWith("Caf\u00E9 \u2013 smoke", StringComparison.Ordinal) && emailText.EndsWith("| Attached", StringComparison.Ordinal)
    && email.Attachments.Single().FileName == "r\u00E9sum\u00E9.pdf" && new ImageExtractor(PdfReader.Open(email.ToArray())).ExtractPage(0).Images.Count == 1);

// RaiCore.Pdf.Sheets: Windows-1252 CSV with semicolons and decimal commas (all detected), 300 rows: the header repeats.
var csvRows = string.Concat(Enumerable.Range(1, 300).Select(i => $"Caf\u00E9 {i};{i},50\n"));
var csvPdf = CsvToPdf.Convert(System.Text.Encoding.Latin1.GetBytes("Item;Price\n" + csvRows));
var csvPages = new TextExtractor(PdfReader.Open(csvPdf.ToArray())).ExtractPages().Select(p => p.Text).ToList();
Check($"csv: {csvPages.Count} pages, {csvPages[^1].Split('\n').Last()}", csvPages.Count > 1
    && csvPages.All(p => p.StartsWith("Item", StringComparison.Ordinal)) && csvPages[^1].Contains("Caf\u00E9 300", StringComparison.Ordinal));

// RaiCore.Pdf.Sheets: an XLSX workbook (built here as a ZIP package) with a shared string, a number format, a date, a
// merged cell and print titles repeated over 120 rows.
var xlsxParts = new Dictionary<string, string>
{
    ["_rels/.rels"] = "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"r1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>",
    ["xl/workbook.xml"] = "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"Data\" sheetId=\"1\" r:id=\"r1\"/></sheets>"
        + "<definedNames><definedName name=\"_xlnm.Print_Titles\" localSheetId=\"0\">Data!$1:$1</definedName></definedNames></workbook>",
    ["xl/_rels/workbook.xml.rels"] = "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"r1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"sheet1.xml\"/>"
        + "<Relationship Id=\"r2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/><Relationship Id=\"r3\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/sharedStrings\" Target=\"strings.xml\"/></Relationships>",
    ["xl/styles.xml"] = "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><fonts><font><sz val=\"11\"/></font><font><b/></font></fonts><cellXfs><xf/><xf fontId=\"1\"/><xf numFmtId=\"4\"/><xf numFmtId=\"14\"/></cellXfs></styleSheet>",
    ["xl/strings.xml"] = "<sst xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><si><t>Caf\u00E9 header</t></si></sst>",
    ["xl/sheet1.xml"] = "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData><row r=\"1\"><c r=\"A1\" s=\"1\" t=\"s\"><v>0</v></c></row>"
        + string.Concat(Enumerable.Range(2, 120).Select(r => $"<row r=\"{r}\"><c r=\"A{r}\" s=\"2\"><v>{r * 1000.5}</v></c><c r=\"B{r}\" s=\"3\"><v>46345</v></c></row>"))
        + "</sheetData><mergeCells><mergeCell ref=\"A1:B1\"/></mergeCells></worksheet>",
};
using var xlsxBuffer = new MemoryStream();
using (var xlsxZip = new System.IO.Compression.ZipArchive(xlsxBuffer, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
{
    foreach (var (name, xml) in xlsxParts)
    {
        using var writer = new StreamWriter(xlsxZip.CreateEntry(name).Open());
        writer.Write(xml);
    }
}

var xlsxPdf = XlsxToPdf.Convert(xlsxBuffer.ToArray());
var xlsxPages = new TextExtractor(PdfReader.Open(xlsxPdf.ToArray())).ExtractPages().Select(p => p.Text).ToList();
Check($"xlsx: {xlsxPages.Count} pages, {xlsxPages[^1].Split('\n').Last()}", xlsxPages.Count > 1
    && xlsxPages.All(p => p.StartsWith("Caf\u00E9 header", StringComparison.Ordinal)) && xlsxPages[^1].Contains("121,060.50 2026-11-19", StringComparison.Ordinal));

// RaiCore.Pdf.Slides: a PPTX presentation (built here as a ZIP package) of two slides, one hidden, with a theme color, a
// rotated shape and an accented title; the hidden slide is left out.
const string PptxA = "http://schemas.openxmlformats.org/drawingml/2006/main";
const string PptxP = "http://schemas.openxmlformats.org/presentationml/2006/main";
const string PptxR = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
static string PptxSlide(string text, string attributes) =>
    $"<p:sld xmlns:a=\"{PptxA}\" xmlns:p=\"{PptxP}\" {attributes}><p:cSld><p:spTree><p:sp><p:nvSpPr><p:cNvPr id=\"2\" name=\"T\"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr>"
    + "<p:spPr><a:xfrm rot=\"1800000\"><a:off x=\"914400\" y=\"914400\"/><a:ext cx=\"4572000\" cy=\"914400\"/></a:xfrm><a:prstGeom prst=\"roundRect\"/>"
    + "<a:solidFill><a:schemeClr val=\"accent1\"><a:lumMod val=\"75000\"/></a:schemeClr></a:solidFill></p:spPr>"
    + $"<p:txBody><a:bodyPr anchor=\"ctr\"/><a:p><a:r><a:rPr sz=\"2800\"/><a:t>{text}</a:t></a:r></a:p></p:txBody></p:sp></p:spTree></p:cSld></p:sld>";
var pptxParts = new Dictionary<string, string>
{
    ["_rels/.rels"] = $"<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"r1\" Type=\"{PptxR}/officeDocument\" Target=\"ppt/presentation.xml\"/></Relationships>",
    ["ppt/presentation.xml"] = $"<p:presentation xmlns:p=\"{PptxP}\" xmlns:r=\"{PptxR}\"><p:sldIdLst><p:sldId id=\"256\" r:id=\"r1\"/><p:sldId id=\"257\" r:id=\"r2\"/></p:sldIdLst><p:sldSz cx=\"12192000\" cy=\"6858000\"/></p:presentation>",
    ["ppt/_rels/presentation.xml.rels"] = $"<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"r1\" Type=\"{PptxR}/slide\" Target=\"slides/slide1.xml\"/><Relationship Id=\"r2\" Type=\"{PptxR}/slide\" Target=\"slides/slide2.xml\"/></Relationships>",
    ["ppt/slides/slide1.xml"] = PptxSlide("Caf\u00E9 slide", string.Empty),
    ["ppt/slides/slide2.xml"] = PptxSlide("Hidden", "show=\"0\""),
};
using var pptxBuffer = new MemoryStream();
using (var pptxZip = new System.IO.Compression.ZipArchive(pptxBuffer, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
{
    foreach (var (name, xml) in pptxParts)
    {
        using var writer = new StreamWriter(pptxZip.CreateEntry(name).Open());
        writer.Write(xml);
    }
}

var pptxPdf = PptxToPdf.Convert(pptxBuffer.ToArray(), new PptxConverterOptions { Tagged = true });
pptxPdf.Save(Path.Combine(output, "slides.pdf"));
var pptxReader = PdfReader.Open(pptxPdf.ToArray());
var pptxText = new TextExtractor(pptxReader).ExtractText();
Check($"pptx: {pptxReader.Pages.Count} page {pptxReader.Pages[0].MediaBox.Width}x{pptxReader.Pages[0].MediaBox.Height}, {pptxText.Trim()}", pptxReader.Pages.Count == 1
    && Math.Abs(pptxReader.Pages[0].MediaBox.Width - 960) < 0.01 && pptxText.Contains("Caf\u00E9 slide", StringComparison.Ordinal) && pptxPdf.Tagged);

// RaiCore.Pdf.Documents: a Word document (built here as a ZIP package) with a numbered list, a table whose header row
// repeats over 80 rows, and a footer with page numbers.
const string wordMain = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
const string wordRelationships = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
var docxParts = new Dictionary<string, string>
{
    ["_rels/.rels"] = $"<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"r1\" Type=\"{wordRelationships}/officeDocument\" Target=\"word/document.xml\"/></Relationships>",
    ["word/_rels/document.xml.rels"] = $"<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"r1\" Type=\"{wordRelationships}/numbering\" Target=\"numbering.xml\"/>"
        + $"<Relationship Id=\"r2\" Type=\"{wordRelationships}/footer\" Target=\"footer1.xml\"/></Relationships>",
    ["word/numbering.xml"] = $"<w:numbering xmlns:w=\"{wordMain}\"><w:abstractNum w:abstractNumId=\"0\"><w:lvl w:ilvl=\"0\"><w:start w:val=\"1\"/><w:numFmt w:val=\"upperRoman\"/><w:lvlText w:val=\"%1.\"/></w:lvl></w:abstractNum>"
        + "<w:num w:numId=\"1\"><w:abstractNumId w:val=\"0\"/></w:num></w:numbering>",
    ["word/footer1.xml"] = $"<w:ftr xmlns:w=\"{wordMain}\"><w:p><w:r><w:t xml:space=\"preserve\">Page </w:t></w:r><w:fldSimple w:instr=\"PAGE\"><w:r><w:t>1</w:t></w:r></w:fldSimple>"
        + "<w:r><w:t xml:space=\"preserve\"> of </w:t></w:r><w:fldSimple w:instr=\"NUMPAGES\"><w:r><w:t>1</w:t></w:r></w:fldSimple></w:p></w:ftr>",
    ["word/document.xml"] = $"<w:document xmlns:w=\"{wordMain}\" xmlns:r=\"{wordRelationships}\"><w:body>"
        + "<w:p><w:pPr><w:numPr><w:ilvl w:val=\"0\"/><w:numId w:val=\"1\"/></w:numPr></w:pPr><w:r><w:t>Caf\u00E9 item</w:t></w:r></w:p>"
        + "<w:tbl><w:tblGrid><w:gridCol w:w=\"4000\"/><w:gridCol w:w=\"4000\"/></w:tblGrid><w:tr><w:trPr><w:tblHeader/></w:trPr><w:tc><w:p><w:r><w:t>Head</w:t></w:r></w:p></w:tc><w:tc><w:p/></w:tc></w:tr>"
        + string.Concat(Enumerable.Range(1, 80).Select(r => $"<w:tr><w:tc><w:p><w:r><w:t>Row {r}</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>{r}</w:t></w:r></w:p></w:tc></w:tr>"))
        + "</w:tbl><w:sectPr><w:footerReference w:type=\"default\" r:id=\"r2\"/></w:sectPr></w:body></w:document>",
};
using var docxBuffer = new MemoryStream();
using (var docxZip = new System.IO.Compression.ZipArchive(docxBuffer, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
{
    foreach (var (name, xml) in docxParts)
    {
        using var writer = new StreamWriter(docxZip.CreateEntry(name).Open());
        writer.Write(xml);
    }
}

var docxPdf = DocxToPdf.Convert(docxBuffer.ToArray());
var docxPages = new TextExtractor(PdfReader.Open(docxPdf.ToArray())).ExtractPages().Select(p => p.Text).ToList();
Check($"docx: {docxPages.Count} pages, {docxPages[^1].Split('\n').Last()}", docxPages.Count > 1
    && docxPages[0].StartsWith("I. Caf\u00E9 item", StringComparison.Ordinal) && docxPages.Skip(1).All(p => p.Contains("Head", StringComparison.Ordinal))
    && docxPages[^1].Contains($"Page {docxPages.Count} of {docxPages.Count}", StringComparison.Ordinal));

// RaiCore.Pdf.Documents: an OpenDocument text document (built here as a ZIP package, mimetype first) with a styled
// heading, a bulleted list, a table whose header row repeats over 80 rows, and a footer with page numbers.
const string odfNamespaces = "xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\" xmlns:style=\"urn:oasis:names:tc:opendocument:xmlns:style:1.0\" "
    + "xmlns:text=\"urn:oasis:names:tc:opendocument:xmlns:text:1.0\" xmlns:table=\"urn:oasis:names:tc:opendocument:xmlns:table:1.0\" "
    + "xmlns:fo=\"urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0\"";
var odtParts = new Dictionary<string, string>
{
    ["mimetype"] = "application/vnd.oasis.opendocument.text",
    ["styles.xml"] = $"<office:document-styles {odfNamespaces}><office:styles><style:style style:name=\"Heading_20_1\" style:family=\"paragraph\">"
        + "<style:text-properties fo:font-size=\"18pt\" fo:font-weight=\"bold\"/></style:style></office:styles><office:automatic-styles>"
        + "<style:page-layout style:name=\"pm1\"><style:page-layout-properties fo:page-width=\"21cm\" fo:page-height=\"29.7cm\" fo:margin=\"2cm\"/></style:page-layout>"
        + "</office:automatic-styles><office:master-styles><style:master-page style:name=\"Standard\" style:page-layout-name=\"pm1\"><style:footer>"
        + "<text:p>Page <text:page-number>1</text:page-number> of <text:page-count>1</text:page-count></text:p></style:footer></style:master-page></office:master-styles></office:document-styles>",
    ["content.xml"] = $"<office:document-content {odfNamespaces}><office:automatic-styles><text:list-style style:name=\"L1\">"
        + "<text:list-level-style-bullet text:level=\"1\" text:bullet-char=\"\u2022\"/></text:list-style></office:automatic-styles><office:body><office:text>"
        + "<text:h text:style-name=\"Heading_20_1\" text:outline-level=\"1\">Caf\u00E9 heading</text:h><text:list text:style-name=\"L1\"><text:list-item><text:p>Bullet item</text:p></text:list-item></text:list>"
        + "<table:table><table:table-column table:number-columns-repeated=\"2\"/><table:table-header-rows><table:table-row><table:table-cell><text:p>Head</text:p></table:table-cell>"
        + "<table:table-cell/></table:table-row></table:table-header-rows>"
        + string.Concat(Enumerable.Range(1, 80).Select(r => $"<table:table-row><table:table-cell><text:p>Row {r}</text:p></table:table-cell><table:table-cell><text:p>{r}</text:p></table:table-cell></table:table-row>"))
        + "</table:table></office:text></office:body></office:document-content>",
};
using var odtBuffer = new MemoryStream();
using (var odtZip = new System.IO.Compression.ZipArchive(odtBuffer, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
{
    foreach (var (name, xml) in odtParts)
    {
        using var writer = new StreamWriter(odtZip.CreateEntry(name, name == "mimetype" ? System.IO.Compression.CompressionLevel.NoCompression : System.IO.Compression.CompressionLevel.Optimal).Open());
        writer.Write(xml);
    }
}

var odtPdf = OdtToPdf.Convert(odtBuffer.ToArray(), new OdtConverterOptions { Tagged = true });
odtPdf.Save(Path.Combine(output, "opendocument.pdf"));
var odtPages = new TextExtractor(PdfReader.Open(odtPdf.ToArray())).ExtractPages().Select(p => p.Text).ToList();
Check($"odt: {odtPages.Count} pages, {odtPages[^1].Split('\n').Last()}", odtPages.Count > 1 && odtPdf.Outline.Count == 1
    && odtPages[0].StartsWith("Caf\u00E9 heading", StringComparison.Ordinal) && odtPages[0].Contains("\u2022", StringComparison.Ordinal)
    && odtPages.Skip(1).All(p => p.Contains("Head", StringComparison.Ordinal)) && odtPages[^1].Contains($"Page {odtPages.Count} of {odtPages.Count}", StringComparison.Ordinal));

// RTF: a document with a Cyrillic code page, a Unicode escape, a two-row table and a footer with page numbers.
var rtf = System.Text.Encoding.Latin1.GetBytes(@"{\rtf1\ansi\ansicpg1251\deff0{\fonttbl{\f0 Arial;}}{\footer\pard Page {\field{\*\fldinst PAGE}{\fldrslt 1}}\par}"
    + @"\pard \'cf\'f0\'e8\'e2\'e5\'f2 caf\u233?\par\trowd\cellx3000\cellx6000\pard\intbl A\cell B\cell\row\trowd\cellx3000\cellx6000\pard\intbl C\cell D\cell\row\pard end\par}");
var rtfText = new TextExtractor(PdfReader.Open(RtfToPdf.ConvertToBytes(rtf))).ExtractText();
Check($"rtf: {rtfText.Split('\n')[0].Trim()}", rtfText.Contains("\u041F\u0440\u0438\u0432\u0435\u0442 caf\u00E9", StringComparison.Ordinal)
    && rtfText.Contains("Page 1", StringComparison.Ordinal) && rtfText.Contains('D', StringComparison.Ordinal));

// RaiCore.Pdf.Xfa: a dynamic XFA form (as an XDP document) with a repeated subform over 60 data rows, a formatted number
// and a page area header: the rows go on to a second page.
var xfaRows = string.Concat(Enumerable.Range(1, 60).Select(r => $"<Row><Name>Row {r}</Name><Amount>{r * 1000}</Amount></Row>"));
var xdp = "<xdp:xdp xmlns:xdp=\"http://ns.adobe.com/xdp/\"><template xmlns=\"http://www.xfa.org/schema/xfa-template/3.3/\"><subform name=\"form1\" layout=\"tb\">"
    + "<pageSet><pageArea name=\"P\"><contentArea x=\"0.5in\" y=\"1in\" w=\"7.5in\" h=\"9in\"/><medium stock=\"letter\" short=\"8.5in\" long=\"11in\"/>"
    + "<draw x=\"0.5in\" y=\"0.4in\" w=\"4in\" h=\"0.3in\"><value><text>Caf\u00E9 form</text></value></draw></pageArea></pageSet>"
    + "<subform name=\"Row\" layout=\"lr-tb\" w=\"7.5in\"><occur min=\"0\" max=\"-1\"/><field name=\"Name\" w=\"4in\" h=\"0.25in\"><ui><textEdit/></ui></field>"
    + "<field name=\"Amount\" w=\"3in\" h=\"0.25in\"><ui><numericEdit/></ui><format><picture>num{$z,zz9.99}</picture></format></field></subform></subform></template>"
    + $"<xfa:datasets xmlns:xfa=\"http://www.xfa.org/schema/xfa-data/1.0/\"><xfa:data><form1>{xfaRows}</form1></xfa:data></xfa:datasets></xdp:xdp>";
var xfaPdf = XfaToPdf.Convert(System.Text.Encoding.UTF8.GetBytes(xdp));
xfaPdf.Save(Path.Combine(output, "xfa.pdf"));
var xfaPages = new TextExtractor(PdfReader.Open(xfaPdf.ToArray())).ExtractPages().Select(p => p.Text).ToList();
Check($"xfa: {xfaPages.Count} pages", xfaPages.Count == 2 && xfaPages.All(p => p.Contains("Caf\u00E9 form", StringComparison.Ordinal))
    && xfaPages[1].Contains("Row 60", StringComparison.Ordinal) && xfaPages[1].Contains("$60,000.00", StringComparison.Ordinal));

Console.WriteLine(failures == 0 ? $"Package smoke test passed. PDFs in {output}" : $"Package smoke test FAILED ({failures} checks).");
return failures == 0 ? 0 : 1;

void Check(string name, bool condition)
{
    Console.WriteLine($"  {(condition ? "ok  " : "FAIL")} {name}");
    failures += condition ? 0 : 1;
}

void Save(string name, PdfDocument pdf, int expectedPages, bool embedsFonts, bool atLeast = false)
{
    var bytes = pdf.ToArray();
    File.WriteAllBytes(Path.Combine(output, name + ".pdf"), bytes);
    var text = System.Text.Encoding.Latin1.GetString(bytes);
    Check($"{name}: is a PDF", text.StartsWith("%PDF-", StringComparison.Ordinal) && text.TrimEnd().EndsWith("%%EOF", StringComparison.Ordinal));
    Check($"{name}: {(atLeast ? "at least " : "")}{expectedPages} page(s), got {pdf.Pages.Count}", atLeast ? pdf.Pages.Count >= expectedPages : pdf.Pages.Count == expectedPages);
    Check($"{name}: embeds fonts", !embedsFonts || text.Contains("/FontFile2", StringComparison.Ordinal));
}

namespace PackageSmoke
{
    public sealed record Invoice(string Number, IReadOnlyList<string> Lines);

    public interface ICustomerDirectory
    {
        Task<string> GetNameAsync(int id);
    }

    public sealed class CustomerDirectory : ICustomerDirectory
    {
        public async Task<string> GetNameAsync(int id)
        {
            await Task.Yield();
            return id == 7 ? "Contoso A/S" : "unknown";
        }
    }
}
