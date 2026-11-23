using RaiCore.Pdf.Charts;

namespace RaiCore.Pdf.Slides.Model;

/// <summary>A chart (a graphic frame with a chart part), drawn as vector graphics by the core's chart engine.</summary>
internal sealed class ChartFrame : Shape
{
    /// <summary>The chart, its colors resolved through the slide's theme.</summary>
    public required Chart Chart { get; init; }

    /// <summary>The theme's body (minor) Latin font, for chart text that names no font or <c>+mn-lt</c>.</summary>
    public string MinorFont { get; init; } = "Calibri";

    /// <summary>The theme's heading (major) Latin font, for <c>+mj-lt</c>.</summary>
    public string MajorFont { get; init; } = "Calibri Light";
}
