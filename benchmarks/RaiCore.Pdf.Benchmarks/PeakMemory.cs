using System.Diagnostics;
using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Html;

namespace RaiCore.Pdf.Benchmarks;

/// <summary>
/// Peak memory of writing a long document to a file, all at once (render, then <see cref="PdfDocument.Save(Stream)"/>)
/// or page by page (<see cref="PdfDocument.BeginSave"/>). BenchmarkDotNet reports what is allocated in total, not
/// the most that is alive at once, so this samples the live managed heap (after a full collection) every few
/// milliseconds while the document is written, which slows it down. Run each
/// scenario in a fresh process ("memory &lt;scenario&gt;"), e.g. under <c>/usr/bin/time -l</c> for the peak resident set.
/// </summary>
internal static class PeakMemory
{
    private static readonly Dictionary<string, Action<Stream>> Scenarios = new(StringComparer.OrdinalIgnoreCase)
    {
        ["report-flow-save"] = output => Documents.ReportFlow(BundledFonts.CreateCollection()).Render().Save(output),
        ["report-flow-stream"] = output =>
        {
            var document = new PdfDocument();
            document.BeginSave(output);
            Documents.ReportFlow(BundledFonts.CreateCollection()).RenderTo(document);
            document.EndSave();
        },
        ["report-html-save"] = output => HtmlToPdf.Convert(Documents.ReportHtml()).Save(output),
        ["report-html-stream"] = output => HtmlToPdf.ConvertToStream(Documents.ReportHtml(), output),
        ["table-save"] = output => HtmlToPdf.Convert(Documents.TableHtml()).Save(output),
        ["table-stream"] = output => HtmlToPdf.ConvertToStream(Documents.TableHtml(), output),
    };

    public static void Run(string scenario)
    {
        var write = Scenarios[scenario];
        write(Stream.Null); // Warm up: JIT, fonts, AngleSharp.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var baseline = GC.GetTotalMemory(forceFullCollection: true);

        var peak = 0L;
        using var done = new ManualResetEventSlim();
        var sampler = new Thread(() =>
        {
            while (!done.IsSet)
            {
                // A full collection each time, so that only what is alive counts (slow, but exact at each sample).
                peak = Math.Max(peak, GC.GetTotalMemory(forceFullCollection: true));
                Thread.Sleep(2);
            }
        });
        sampler.Start();

        var path = Path.GetTempFileName();
        var watch = Stopwatch.StartNew();
        try
        {
            using (var file = File.Create(path))
            {
                write(file);
            }

            watch.Stop();
            done.Set();
            sampler.Join();
            Console.WriteLine(
                $"{scenario}: {new FileInfo(path).Length / 1024.0:N0} KB written in {watch.ElapsedMilliseconds} ms, " +
                $"peak live managed heap {(peak - baseline) / (1024.0 * 1024):N1} MB above the {baseline / (1024.0 * 1024):N1} MB at the start");
        }
        finally
        {
            File.Delete(path);
        }
    }
}
