using System.Xml.Linq;
using RaiCore.Pdf.Charts;
using RaiCore.Pdf.Slides.Model;

namespace RaiCore.Pdf.Slides.Pptx;

/// <summary>Charts: a graphic frame's chart part read by the core's chart engine.</summary>
internal sealed partial class PptxReader
{
    /// <summary>The namespace of the chart types of Office 2016 (waterfall, treemap, sunburst...).</summary>
    private const string ChartExUri = "http://schemas.microsoft.com/office/drawing/2014/chartex";

    /// <summary>
    /// The choice of markup compatibility content that holds a newer chart (chartex) the chart engine draws: its part is
    /// there, sound, and of a type it draws (not a region map); else <c>null</c>.
    /// </summary>
    private XElement? DrawableChartEx(XElement alternate, Sheet sheet)
    {
        var choice = alternate.El("Choice");
        var data = choice?.Descendants().FirstOrDefault(e => e.Name.LocalName == "graphicData" && e.Attr("uri") == ChartExUri);
        if (data is null || _package.Relationship(sheet.Path, data.El("chart").RelId("id")) is not { External: false } relationship)
        {
            return null;
        }

        try
        {
            return _package.Xml(relationship.Target) is { } root && root.Name.NamespaceName == ChartExUri
                && ChartReader.Read(root, sheet.Colors.Color, _limits) is { PlotArea.Groups.Count: > 0 } ? choice : null;
        }
        catch (PresentationFormatException)
        {
            return null;
        }
    }

    /// <summary>Reports a newer chart type whose stored picture (the markup compatibility fallback) is drawn instead.</summary>
    private void ReportChartEx(XElement alternate, XElement chosen)
    {
        if (chosen.Name.LocalName == "Fallback"
            && alternate.El("Choice")?.Descendants().Any(e => e.Name.LocalName == "graphicData" && e.Attr("uri") == ChartExUri) == true)
        {
            Report(_slideNumber, "chart", "A chart of a newer type (such as a waterfall or treemap chart) is drawn as its stored picture.");
        }
    }

    /// <summary>
    /// Reads the chart of a graphic frame (<c>c:chart r:id</c>), or reports why it cannot be drawn: the part is
    /// missing or damaged, or it holds only chart types the engine cannot draw.
    /// </summary>
    private ChartFrame? ChartOf(XElement? graphicData, ShapeBox box, XElement? cNvPr, Sheet sheet)
    {
        var reference = graphicData.El("chart");
        if (_package.Relationship(sheet.Path, reference.RelId("id")) is not { External: false } relationship)
        {
            Report(_slideNumber, "chart", "A chart without its chart part is not drawn.");
            return null;
        }

        XElement? root;
        try
        {
            root = _package.Xml(relationship.Target);
        }
        catch (PresentationFormatException ex)
        {
            Report(_slideNumber, "chart", $"The chart part '{relationship.Target}' is damaged and is not drawn: {ex.Message}");
            return null;
        }

        var unsupported = new List<string>();
        var chart = root is null ? null : ChartReader.Read(root, sheet.Colors.Color, _limits, unsupported);
        foreach (var kind in unsupported.Distinct())
        {
            Report(_slideNumber, "chart", $"A {kind} chart is not drawn.");
        }

        if (chart is null || chart.PlotArea.Groups.Count == 0)
        {
            if (unsupported.Count == 0)
            {
                Report(_slideNumber, "chart", $"The chart part '{relationship.Target}' has no chart that can be drawn.");
            }

            return null;
        }

        foreach (var note in chart.Notes.Distinct())
        {
            Report(_slideNumber, "chart", note);
        }

        var theme = sheet.Master.Theme;
        return new ChartFrame
        {
            Box = box,
            Name = cNvPr.Attr("name"),
            AltText = cNvPr.Attr("descr"),
            Chart = chart,
            MinorFont = theme.MinorLatin,
            MajorFont = theme.MajorLatin,
        };
    }
}
