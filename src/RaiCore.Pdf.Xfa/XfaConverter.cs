using RaiCore.Pdf.Xfa.Form;
using RaiCore.Pdf.Xfa.Formatting;
using RaiCore.Pdf.Xfa.Layout;
using RaiCore.Pdf.Xfa.Packets;
using RaiCore.Pdf.Xfa.Rendering;
using RaiCore.Pdf.Xfa.Template;

namespace RaiCore.Pdf.Xfa;

/// <summary>Reads an XFA form within the options' limits, merges its data, lays it out and draws its pages.</summary>
internal static class XfaConverter
{
    /// <summary>The creator the PDFs name.</summary>
    public const string Creator = "RaiCore.Pdf.Xfa";

    public static PdfDocument Convert(byte[] data, XfaConverterOptions options, CancellationToken cancellationToken)
    {
        var limits = Validate(options);
        cancellationToken.ThrowIfCancellationRequested();
        using (limits.Enforce(cancellationToken))
        {
            var report = Report(options);
            var xml = new XfaXml(limits);
            var packets = XfaPackets.Read(data, xml, options, report);
            var form = Merge(packets, options, xml, report);
            return Render(form, packets, options, report);
        }
    }

    /// <summary>Resolves the template's prototypes and merges the data (the options' or the form's own) into it.</summary>
    internal static MergedForm Merge(XfaPackets packets, XfaConverterOptions options, XfaXml xml, Action<XfaDiagnosticKind, string>? report)
    {
        Prototypes.Resolve(packets.Template, xml, report);
        var data = options.Data is { } given ? DataMerger.FindData(xml.Load(given)) : DataMerger.FindData(packets.Datasets);
        return DataMerger.Merge(packets.Template, data, xml, report);
    }

    /// <summary>Lays out a merged form and draws its pages.</summary>
    internal static PdfDocument Render(MergedForm form, XfaPackets packets, XfaConverterOptions options, Action<XfaDiagnosticKind, string>? report)
    {
        var limits = options.Limits;
        var fonts = new XfaFonts(options, packets.Fonts, report);
        var measurer = new Measurer(new TextLayouter(fonts), new LocaleSet(packets.LocaleSet), form, limits, report);
        var paginator = new Paginator(form, limits, report);
        var root = form.Root;
        var rootBox = measurer.Measure(root, paginator.ContentWidth);
        List<LayoutBox> boxes;
        var offsetX = 0.0;
        if ((root.Template.Attr("layout") ?? "position") is "tb" or "lr-tb" or "rl-tb" or "table")
        {
            boxes = rootBox.Children;
            offsetX = rootBox.Margin.Left;
        }
        else
        {
            rootBox.X = root.Template.Measure("x");
            rootBox.Y = root.Template.Measure("y");
            boxes = [rootBox];
        }

        var pages = paginator.Paginate(boxes, offsetX);
        var boilerplate = new Dictionary<FormNode, IReadOnlyList<LayoutBox>>();
        var document = new PdfDocument();
        document.Info.Title = options.Title ?? packets.Title;
        document.Info.Creator = Creator;
        new XfaRenderer(document).Render(pages, pageArea =>
        {
            if (!boilerplate.TryGetValue(pageArea, out var boxes))
            {
                var (width, _) = Paginator.PageSize(pageArea);
                boxes = measurer.Measure(pageArea, width).Children;
                boilerplate[pageArea] = boxes;
            }

            return boxes;
        });
        return document;
    }

    internal static ResourceLimits Validate(XfaConverterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options.FallbackFontFamilies, nameof(options));
        return options.Limits ?? throw new ArgumentException("The resource limits must not be null.", nameof(options));
    }

    /// <summary>The diagnostics callback as the converter calls it (each issue once), or <c>null</c>.</summary>
    internal static Action<XfaDiagnosticKind, string>? Report(XfaConverterOptions options)
    {
        if (options.Diagnostics is not { } diagnostics)
        {
            return null;
        }

        var reported = new HashSet<(XfaDiagnosticKind, string)>();
        return (kind, message) =>
        {
            if (reported.Add((kind, message)))
            {
                diagnostics(new XfaDiagnostic(kind, message));
            }
        };
    }
}
