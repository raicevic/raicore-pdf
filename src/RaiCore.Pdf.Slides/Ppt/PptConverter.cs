using System.Diagnostics;
using RaiCore.Pdf.Slides.Model;
using RaiCore.Pdf.Slides.Pptx;
using RaiCore.Pdf.Slides.Rendering;

namespace RaiCore.Pdf.Slides.Ppt;

/// <summary>Reads a PowerPoint 97-2003 presentation within the options' limits and renders its slides.</summary>
internal static class PptConverter
{
    /// <summary>Converts a presentation.</summary>
    public static PdfDocument Convert(byte[] data, PptxConverterOptions options, CancellationToken cancellationToken)
    {
        var limits = PptxConverter.Validate(options);
        cancellationToken.ThrowIfCancellationRequested();
        using (limits.Enforce(cancellationToken))
        {
            var clock = Stopwatch.StartNew();
            var checkpoint = PptxConverter.Checkpoint(limits, clock, cancellationToken);
            var report = PptxConverter.Report(options);
            var presentation = Read(data, options, checkpoint, report);
            return SlideRenderer.Render(presentation, options, report, checkpoint);
        }
    }

    /// <summary>Reads a presentation into the slide model (for tests): the slides the options select.</summary>
    internal static Presentation Read(byte[] data, PptxConverterOptions options, Action? checkpoint = null, Action<int, string, string>? report = null)
    {
        var limits = PptxConverter.Validate(options);
        var file = PptFile.Open(data, limits);
        var numbers = options.SlideNumbers is { } selected ? new HashSet<int>(selected) : null;
        return PptReader.Read(
            file,
            (number, hidden) => numbers is not null ? numbers.Contains(number) : !hidden || options.IncludeHiddenSlides,
            limits,
            report ?? PptxConverter.Report(options),
            checkpoint);
    }
}
