using System.Xml.Linq;
using RaiCore.Pdf.Charts;
using RaiCore.Pdf.Documents.Model;

namespace RaiCore.Pdf.Documents.Docx;

/// <summary>Charts: a drawing's chart part, read by the core's chart engine and drawn as a vector image.</summary>
internal sealed partial class DocxReader
{
    /// <summary>The namespace of the chart types of Office 2016 (waterfall, treemap, sunburst...).</summary>
    private const string ChartExNamespace = "http://schemas.microsoft.com/office/drawing/2014/chartex";

    /// <summary>
    /// The chart of a drawing (<c>c:chart r:id</c>) as an image placed like a picture, or <c>null</c> (with a
    /// diagnostic) when its part is missing or damaged or holds no chart type the engine can draw.
    /// </summary>
    private ImageInline? Chart(XElement? graphicData, double width, double height, ImagePlacement placement, WordAlignment alignment, double? offset, string? alternative)
    {
        var reference = graphicData?.Elements().FirstOrDefault(e => e.Name.LocalName == "chart");
        if (reference is null || RelationshipId(reference) is not { } id || !_relationships.TryGetValue(id, out var relationship) || relationship.External)
        {
            _context.Report(DocumentDiagnosticKind.UnsupportedContent, "A chart without its chart part is left out.");
            return null;
        }

        XElement? root;
        try
        {
            root = _package.Load(relationship.Target);
        }
        catch (DocumentFormatException ex)
        {
            _context.Report(DocumentDiagnosticKind.UnsupportedContent, $"The chart part '{relationship.Target}' is damaged; the chart is left out: {ex.Message}");
            return null;
        }

        var unsupported = new List<string>();
        var chart = root is null ? null : ChartReader.Read(root, _theme.Color, _context.Limits, unsupported);
        foreach (var kind in unsupported.Distinct())
        {
            _context.Report(DocumentDiagnosticKind.UnsupportedContent, $"A {kind} chart is not drawn.");
        }

        if (chart is null || chart.PlotArea.Groups.Count == 0)
        {
            if (unsupported.Count == 0)
            {
                _context.Report(DocumentDiagnosticKind.UnsupportedContent, $"The chart part '{relationship.Target}' has no chart that can be drawn; the chart is left out.");
            }

            return null;
        }

        foreach (var note in chart.Notes.Distinct())
        {
            _context.Report(DocumentDiagnosticKind.UnsupportedContent, note);
        }

        return new ImageInline([], Sane(width), Sane(height))
        {
            Placement = placement,
            Alignment = alignment,
            Offset = offset,
            AlternativeText = alternative ?? (chart.Title?.Text is { Length: > 0 } title ? "Chart: " + title : "Chart"),
            Name = relationship.Target,
            Chart = new EmbeddedChart(chart, _theme.MinorFont, _theme.MajorFont),
        };
    }

    /// <summary>
    /// Whether a markup compatibility choice holds a chart of a newer type (a chartex graphic), whose stored picture in
    /// the fallback is shown instead. (The package reader keeps no namespace declarations, so the choice's content
    /// tells, not the prefixes it requires.)
    /// </summary>
    private static bool RequiresChartEx(XElement choice) =>
        choice.Descendants().Any(e => e.Name.LocalName == "graphicData" && Ooxml.Attr(e, "uri") == ChartExNamespace);

    /// <summary>
    /// Whether the chart engine draws a choice's newer chart: its chartex part is there, sound, and of a type it draws
    /// (not a region map).
    /// </summary>
    private bool CanDrawChartEx(XElement choice)
    {
        var data = choice.Descendants().First(e => e.Name.LocalName == "graphicData" && Ooxml.Attr(e, "uri") == ChartExNamespace);
        var reference = data.Elements().FirstOrDefault(e => e.Name.LocalName == "chart");
        if (reference is null || RelationshipId(reference) is not { } id || !_relationships.TryGetValue(id, out var relationship) || relationship.External)
        {
            return false;
        }

        try
        {
            return _package.Load(relationship.Target) is { } root && root.Name.NamespaceName == ChartExNamespace && ChartReader.Read(root, _theme.Color, _context.Limits) is { PlotArea.Groups.Count: > 0 };
        }
        catch (DocumentFormatException)
        {
            return false;
        }
    }
}
