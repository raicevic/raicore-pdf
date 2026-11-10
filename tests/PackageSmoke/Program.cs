// Installs the RaiCore.Pdf packages from artifacts/ (see nuget.config) and runs the README samples against them.
// Usage: dotnet run --project tests/PackageSmoke [output-directory]
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using PackageSmoke;
using RaiCore.Pdf;
using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Html;
using RaiCore.Pdf.Layout;
using RaiCore.Pdf.Layout.Blocks;
using RaiCore.Pdf.Razor;

var output = Directory.CreateDirectory(args.Length > 0 ? args[0] : "smoke-output").FullName;
var failures = 0;

// The assemblies must come from the restored packages, not from project references or the repository's bin folders.
foreach (var assembly in new[] { typeof(PdfDocument), typeof(BundledFonts), typeof(FlowDocument), typeof(HtmlToPdf), typeof(RazorToPdf) }.Select(t => t.Assembly))
{
    var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
    Console.WriteLine($"{assembly.GetName().Name} {version}");
}

var packageCache = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".packages");
Check("packages were restored into the private cache", Directory.Exists(Path.Combine(packageCache, "raicore.pdf.razor")));

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

// RaiCore.Pdf.Razor: static API and dependency injection.
var invoice = new Invoice("2026-0042", ["Consulting", "Support"]);
var razor = await RazorToPdf.ConvertAsync<InvoiceDocument>(new Dictionary<string, object?> { [nameof(InvoiceDocument.Invoice)] = invoice });
Check("Razor title", razor.Info.Title == "Invoice 2026-0042");
Save("razor", razor, expectedPages: 1, embedsFonts: true);

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
