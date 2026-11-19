using System.Diagnostics;
using RaiCore.Pdf.Slides.Model;
using RaiCore.Pdf.Slides.Rendering;

namespace RaiCore.Pdf.Slides.Pptx;

/// <summary>Reads a presentation within the options' limits and renders its slides.</summary>
internal static class PptxConverter
{
    /// <summary>The creator the PDFs name.</summary>
    public const string Creator = "RaiCore.Pdf.Slides";

    /// <summary>Converts a presentation.</summary>
    public static PdfDocument Convert(byte[] data, PptxConverterOptions options, CancellationToken cancellationToken)
    {
        var limits = Validate(options);
        cancellationToken.ThrowIfCancellationRequested();
        using (limits.Enforce(cancellationToken))
        {
            var clock = Stopwatch.StartNew();
            var checkpoint = Checkpoint(limits, clock, cancellationToken);
            var report = Report(options);
            var presentation = Read(data, options, checkpoint, report);
            return SlideRenderer.Render(presentation, options, report, checkpoint);
        }
    }

    /// <summary>Reads a presentation into the slide model (for tests): the slides the options select.</summary>
    internal static Presentation Read(byte[] data, PptxConverterOptions options, Action? checkpoint = null, Action<int, string, string>? report = null)
    {
        var limits = Validate(options);
        using var package = PptxPackage.Open(data, limits, checkpoint);
        var numbers = options.SlideNumbers is { } selected ? new HashSet<int>(selected) : null;
        return PptxReader.Read(
            package,
            (number, hidden) => numbers is not null ? numbers.Contains(number) : !hidden || options.IncludeHiddenSlides,
            limits,
            report ?? Report(options),
            checkpoint);
    }

    private static ResourceLimits Validate(PptxConverterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options.FallbackFontFamilies, nameof(options));
        return options.Limits ?? throw new ArgumentException("The resource limits must not be null.", nameof(options));
    }

    /// <summary>The diagnostics callback as the reader and renderer call it (each issue once per slide), or <c>null</c>.</summary>
    private static Action<int, string, string>? Report(PptxConverterOptions options)
    {
        if (options.Diagnostics is not { } diagnostics)
        {
            return null;
        }

        var reported = new HashSet<(int, string, string)>();
        return (slide, subject, message) =>
        {
            if (reported.Add((slide, subject, message)))
            {
                diagnostics(new SlideDiagnostic(slide, subject, message));
            }
        };
    }

    /// <summary>
    /// A checkpoint for the work outside the layout engine: throws when the conversion is canceled or has taken longer
    /// than <see cref="ResourceLimits.Timeout"/>.
    /// </summary>
    internal static Action Checkpoint(ResourceLimits limits, Stopwatch clock, CancellationToken cancellationToken) => () =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (limits.Timeout != Timeout.InfiniteTimeSpan && clock.Elapsed > limits.Timeout)
        {
            throw new ResourceLimitExceededException(nameof(ResourceLimits.Timeout), (long)limits.Timeout.TotalMilliseconds,
                $"The conversion took longer than ResourceLimits.Timeout ({limits.Timeout}).");
        }
    };
}
