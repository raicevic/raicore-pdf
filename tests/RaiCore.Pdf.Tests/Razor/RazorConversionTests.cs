using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using RaiCore.Pdf.Html;
using RaiCore.Pdf.Layout.Blocks;
using RaiCore.Pdf.Razor;
using RaiCore.Pdf.Tests.Razor.Components;

namespace RaiCore.Pdf.Tests.Razor;

public sealed record Invoice(string Number, string Customer, IReadOnlyList<InvoiceLine> Lines);

public sealed record InvoiceLine(string Description, int Quantity, decimal Price);

public interface IReportData
{
    Task<IReadOnlyList<string>> GetItemsAsync();
}

public sealed class SlowReportData(string prefix) : IReportData
{
    public async Task<IReadOnlyList<string>> GetItemsAsync()
    {
        await Task.Delay(50);
        return [$"{prefix} A", $"{prefix} B"];
    }
}

public class RazorConversionTests
{
    private const double Mm = 72 / 25.4;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Dictionary<string, object?> Params(string name, object? value) => new() { [name] = value };

    [Fact]
    public async Task Renders_a_component_with_parameters_to_html()
    {
        var html = await RazorToPdf.RenderHtmlAsync<Greeting>(Params(nameof(Greeting.Name), "Ada"), Ct);

        Assert.Equal("<p class=\"greeting\">Hello, Ada!</p>", html.Trim());
    }

    [Fact]
    public async Task Uses_parameter_defaults_when_none_are_given()
    {
        Assert.Contains("Hello, world!", await RazorToPdf.RenderHtmlAsync<Greeting>(cancellationToken: Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Encodes_parameter_values_as_text_not_markup()
    {
        var name = "<b>Ada & \u00C6blegr\u00F8d</b>";

        var html = await RazorToPdf.RenderHtmlAsync<Greeting>(Params(nameof(Greeting.Name), name), Ct);
        var flow = HtmlToPdf.CreateFlowDocument(html);

        Assert.DoesNotContain("<b>", html, StringComparison.Ordinal);
        Assert.Contains($"Hello, {name}!", Texts(flow.Blocks));
    }

    [Fact]
    public async Task Converts_a_full_document_with_paged_media_css()
    {
        var pdf = await RazorToPdf.ConvertAsync<InvoiceDocument>(Params(nameof(InvoiceDocument.Invoice), SampleInvoice(80)), cancellationToken: Ct);

        Assert.Equal("Invoice 2026-0042", pdf.Info.Title);
        Assert.Equal("RaiCore.Pdf.Razor", pdf.Info.Creator);
        Assert.True(pdf.Pages.Count >= 2, $"Expected the 80-line invoice to need several pages, got {pdf.Pages.Count}.");
    }

    [Fact]
    public async Task Escaped_at_rules_in_razor_style_blocks_reach_the_css()
    {
        // In .razor files CSS at-rules are written @@page / @@media; the page margins prove @page was applied.
        var flow = await new RazorPdfRenderer().CreateFlowDocumentAsync<InvoiceDocument>(
            Params(nameof(InvoiceDocument.Invoice), SampleInvoice(3)), cancellationToken: Ct);

        Assert.Equal(25 * Mm, flow.Setup.Margins.Top, 2);
        Assert.Equal(20 * Mm, flow.Setup.Margins.Left, 2);
        Assert.NotNull(flow.PageDecorator); // @bottom-right margin box
        var heading = Flatten(flow.Blocks).OfType<ParagraphBlock>().First(p => Text(p) == "Invoice 2026-0042");
        Assert.Equal(PdfColor.FromRgb(0x1a, 0x4f, 0x8b), heading.Paragraph.Spans[0].Style.Color);
    }

    [Fact]
    public async Task Converted_text_includes_child_components_and_page_numbers()
    {
        var pdf = await RazorToPdf.ConvertAsync<InvoiceDocument>(Params(nameof(InvoiceDocument.Invoice), SampleInvoice(80)), cancellationToken: Ct);

        var text = ExternalTools.ExtractText(pdf.ToArray());
        Assert.Contains("Invoice 2026-0042", text, StringComparison.Ordinal);
        Assert.Matches(@"Item 1\s", text);
        Assert.Matches(@"Item 80\s", text);
        Assert.Contains($"Page 1 of {pdf.Pages.Count}", text, StringComparison.Ordinal);
        Assert.Contains("Total", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Renders_child_content_through_layout_components()
    {
        var html = await RazorToPdf.RenderHtmlAsync<LayoutReport>(cancellationToken: Ct);
        var pdf = HtmlToPdf.Convert(html);

        Assert.Contains("<section class=\"content\">", html, StringComparison.Ordinal);
        Assert.Contains("<p>Revenue grew.</p>", html, StringComparison.Ordinal);
        Assert.Equal("Quarterly report", pdf.Info.Title);
    }

    [Fact]
    public async Task Waits_for_async_initialization_and_injects_services()
    {
        var services = new ServiceCollection()
            .AddSingleton<IReportData>(new SlowReportData("Row"))
            .BuildServiceProvider();

        var html = await new RazorPdfRenderer(services).RenderHtmlAsync<AsyncReport>(cancellationToken: Ct);

        Assert.Contains("Row A, Row B", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Loading", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Add_razor_pdf_resolves_scoped_services_from_the_callers_scope()
    {
        var counter = 0;
        await using var provider = new ServiceCollection()
            .AddScoped<IReportData>(_ => new SlowReportData($"Scope{Interlocked.Increment(ref counter)}"))
            .AddRazorPdf()
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        string first, second;
        await using (var scope = provider.CreateAsyncScope())
        {
            var renderer = scope.ServiceProvider.GetRequiredService<RazorPdfRenderer>();
            first = await renderer.RenderHtmlAsync<AsyncReport>(cancellationToken: Ct);
            Assert.Contains("Scope1 A", await renderer.RenderHtmlAsync<AsyncReport>(cancellationToken: Ct), StringComparison.Ordinal);
        }

        await using (var scope = provider.CreateAsyncScope())
        {
            second = await scope.ServiceProvider.GetRequiredService<RazorPdfRenderer>().RenderHtmlAsync<AsyncReport>(cancellationToken: Ct);
        }

        Assert.Contains("Scope1 A", first, StringComparison.Ordinal);
        Assert.Contains("Scope2 A", second, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_services_fail_with_the_component_error()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => RazorToPdf.RenderHtmlAsync<AsyncReport>(cancellationToken: Ct));

        Assert.Contains(nameof(IReportData), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Exceptions_thrown_by_components_propagate()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => RazorToPdf.ConvertAsync<Failing>(cancellationToken: Ct));

        Assert.Equal("Template failed", error.Message);
    }

    [Fact]
    public void Synchronous_conversion_works()
    {
        var bytes = RazorToPdf.ConvertToBytes<Greeting>(Params(nameof(Greeting.Name), "Sync"));

        Assert.StartsWith("%PDF-", System.Text.Encoding.ASCII.GetString(bytes, 0, 5), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rejects_types_that_are_not_components()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => new RazorPdfRenderer().RenderHtmlAsync(typeof(string), cancellationToken: Ct));
    }

    [Fact]
    public async Task Honors_cancellation()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => RazorToPdf.ConvertAsync<Greeting>(cancellationToken: cts.Token));
    }

    [Fact]
    public async Task Concurrent_conversions_are_independent()
    {
        var names = Enumerable.Range(1, 16).Select(i => "Name" + i.ToString(CultureInfo.InvariantCulture)).ToList();

        var htmls = await Task.WhenAll(names.Select(n => RazorToPdf.RenderHtmlAsync<Greeting>(Params(nameof(Greeting.Name), n), Ct)));

        Assert.Equal(names.Select(n => $"<p class=\"greeting\">Hello, {n}!</p>"), htmls.Select(h => h.Trim()));
    }

    [Fact]
    public async Task Language_and_meta_elements_of_the_component_become_the_metadata()
    {
        var pdf = await RazorToPdf.ConvertAsync<LocalizedReport>(Params(nameof(LocalizedReport.Author), "Ada"), cancellationToken: Ct);

        Assert.Equal("de-CH", pdf.Language);
        Assert.Equal("Bericht", pdf.Info.Title);
        Assert.Equal("Ada", pdf.Info.Author);
        Assert.Equal("RaiCore.Pdf.Razor", pdf.Info.Creator);
        Assert.Contains("/Lang (de-CH)", PdfMetadataReader.Catalog(System.Text.Encoding.Latin1.GetString(pdf.ToArray())), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Options_override_the_metadata_of_the_component()
    {
        var pdf = await RazorToPdf.ConvertAsync<LocalizedReport>(
            options: new HtmlConverterOptions { Language = "fr-CH", Creator = "Billing", Title = "Rapport" },
            cancellationToken: Ct);

        Assert.Equal("fr-CH", pdf.Language);
        Assert.Equal("Rapport", pdf.Info.Title);
        Assert.Equal("Billing", pdf.Info.Creator);
        Assert.Null(pdf.Info.Author);
    }

    internal static Invoice SampleInvoice(int lines) => new(
        "2026-0042",
        "Contoso A/S",
        [.. Enumerable.Range(1, lines).Select(i => new InvoiceLine($"Item {i}", i % 5 + 1, 10m * i + 0.5m))]);

    private static IEnumerable<Block> Flatten(IEnumerable<Block> blocks) =>
        blocks.SelectMany(b => b is ContainerBlock c ? Flatten(c.Children).Prepend(b) : [b]);

    private static string Text(ParagraphBlock block) => string.Concat(block.Paragraph.Spans.Select(s => s.Text)).Trim();

    private static List<string> Texts(IEnumerable<Block> blocks) => [.. Flatten(blocks).OfType<ParagraphBlock>().Select(Text)];
}
