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
using RaiCore.Pdf.Sheets;
using RaiCore.Pdf.Extraction;
using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Html;
using RaiCore.Pdf.Layout;
using RaiCore.Pdf.Layout.Text;
using RaiCore.Pdf.Razor;

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
