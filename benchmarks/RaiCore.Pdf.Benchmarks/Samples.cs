using System.Diagnostics;
using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Html;

namespace RaiCore.Pdf.Benchmarks;

/// <summary>The benchmark documents by name, to write them to files or render them outside BenchmarkDotNet.</summary>
internal static class Samples
{
    private static readonly Dictionary<string, Func<PdfDocument>> Scenarios = new(StringComparer.OrdinalIgnoreCase)
    {
        ["invoice"] = () => HtmlToPdf.Convert(Documents.InvoiceHtml()),
        ["report-html"] = () => HtmlToPdf.Convert(Documents.ReportHtml()),
        ["report-flow"] = () => Documents.ReportFlow(BundledFonts.CreateCollection()).Render(),
        ["table"] = () => HtmlToPdf.Convert(Documents.TableHtml()),
        ["flex"] = () => HtmlToPdf.Convert(Documents.FlexHtml()),
        ["sheet"] = () => Sheets.CsvToPdf.Convert(Documents.Csv()),
    };

    /// <summary>Writes every document to <paramref name="directory"/>, printing its page count and time.</summary>
    public static void WriteAll(string directory)
    {
        foreach (var (name, create) in Scenarios)
        {
            var watch = Stopwatch.StartNew();
            var document = create();
            document.Save(Path.Combine(directory, name + ".pdf"));
            Console.WriteLine($"{name}: {document.Pages.Count} pages, {watch.ElapsedMilliseconds} ms");
        }
    }

    /// <summary>Renders one document <paramref name="times"/> times, printing each time.</summary>
    public static void Run(string scenario, int times)
    {
        // "table-<rows>" renders a table of another size, "sheet-<rows>" a CSV file of another size.
        Func<PdfDocument> create = scenario switch
        {
            _ when scenario.StartsWith("table-", StringComparison.Ordinal) =>
                () => HtmlToPdf.Convert(Documents.TableHtml(int.Parse(scenario[6..], System.Globalization.CultureInfo.InvariantCulture))),
            _ when scenario.StartsWith("sheet-", StringComparison.Ordinal) =>
                () => Sheets.CsvToPdf.Convert(Documents.Csv(int.Parse(scenario[6..], System.Globalization.CultureInfo.InvariantCulture))),
            _ => Scenarios[scenario],
        };
        for (var i = 0; i < times; i++)
        {
            var watch = Stopwatch.StartNew();
            create().Save(Stream.Null);
            Console.WriteLine($"{scenario}: {watch.ElapsedMilliseconds} ms");
        }
    }
}
