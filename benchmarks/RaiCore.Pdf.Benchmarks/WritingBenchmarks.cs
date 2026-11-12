using BenchmarkDotNet.Attributes;
using RaiCore.Pdf.Fonts;

namespace RaiCore.Pdf.Benchmarks;

/// <summary>
/// Writing page by page (<see cref="PdfDocument.BeginSave"/>): 1,000 pages of text drawn with the canvas, and the
/// 100-page report with the layout API. <see cref="DocumentBenchmarks"/> has the same documents saved at once.
/// </summary>
[MemoryDiagnoser]
[MedianColumn]
[MinColumn]
public class WritingBenchmarks
{
    private PdfFont _font = null!;
    private FontCollection _fonts = null!;

    [GlobalSetup]
    public void Setup()
    {
        _fonts = BundledFonts.CreateCollection();
        _font = _fonts.Find("Liberation Serif")!;
    }

    [Benchmark]
    public void Canvas1000PagesPageByPage()
    {
        var document = new PdfDocument();
        document.BeginSave(Stream.Null);
        Documents.DrawPages(document, _font, d => d.FlushPages());
        document.EndSave();
    }

    [Benchmark]
    public void Report100PagesPageByPage()
    {
        var document = new PdfDocument();
        document.BeginSave(Stream.Null);
        Documents.ReportFlow(_fonts).RenderTo(document);
        document.EndSave();
    }
}
