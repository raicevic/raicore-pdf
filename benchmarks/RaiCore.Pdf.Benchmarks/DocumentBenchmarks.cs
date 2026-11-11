using BenchmarkDotNet.Attributes;
using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Html;

namespace RaiCore.Pdf.Benchmarks;

/// <summary>
/// End-to-end rendering: from HTML (or the layout API) to the bytes of the PDF file. The inputs are built once in
/// the global setup, so only the conversion is measured. The output goes to <see cref="Stream.Null"/> where the API
/// allows, so the cost of a growing <see cref="MemoryStream"/> does not hide the library's own allocations.
/// </summary>
[MemoryDiagnoser]
[MedianColumn]
[MinColumn]
public class DocumentBenchmarks
{
    private string _invoice = "";
    private string _report = "";
    private string _table = "";
    private string _flex = "";
    private FontCollection _fonts = null!;
    private PdfFont _serif = null!;

    [GlobalSetup]
    public void Setup()
    {
        _invoice = Documents.InvoiceHtml();
        _report = Documents.ReportHtml();
        _table = Documents.TableHtml();
        _flex = Documents.FlexHtml();
        _fonts = BundledFonts.CreateCollection();
        _serif = _fonts.Find("Liberation Serif")!;
    }

    /// <summary>A one-page invoice from HTML (flex header, a 15-line table, page counters).</summary>
    [Benchmark]
    public void Invoice() => HtmlToPdf.Convert(_invoice).Save(Stream.Null);

    /// <summary>A 100-page report from HTML (headings, justified and hyphenated paragraphs, page counters).</summary>
    [Benchmark]
    public void Report100PagesHtml() => HtmlToPdf.Convert(_report).Save(Stream.Null);

    /// <summary>The 100-page report with the layout API (FlowDocument), fonts loaded once (as the bundled fonts are).</summary>
    [Benchmark]
    public void Report100PagesFlow() => Documents.ReportFlow(_fonts).Render().Save(Stream.Null);

    /// <summary>A 10,000-row table from HTML (205 pages, the header row repeated on each).</summary>
    [Benchmark]
    public void Table10000Rows() => HtmlToPdf.Convert(_table).Save(Stream.Null);

    /// <summary>240 wrapped flex cards, each a nested flex column with flex rows.</summary>
    [Benchmark]
    public void FlexDashboard() => HtmlToPdf.Convert(_flex).Save(Stream.Null);

    /// <summary>1,000 pages of 60 lines of text drawn with the canvas (no layout): the cost of writing.</summary>
    [Benchmark]
    public void Canvas1000Pages()
    {
        var document = new PdfDocument();
        Documents.DrawPages(document, _serif);
        document.Save(Stream.Null);
    }
}
