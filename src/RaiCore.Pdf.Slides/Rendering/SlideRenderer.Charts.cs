using RaiCore.Pdf.Charts;
using RaiCore.Pdf.Slides.Model;

namespace RaiCore.Pdf.Slides.Rendering;

/// <summary>Charts, drawn by the core's chart engine in the frame's box with the presentation's fonts.</summary>
internal sealed partial class SlideRenderer
{
    /// <summary>The size of chart text no format gives a size: PowerPoint's 18 points.</summary>
    private const double ChartFontSize = 18;

    /// <summary>Draws a chart in its frame (tagged as a figure with its alternative text, or its title).</summary>
    private void DrawChart(PdfCanvas canvas, ChartFrame frame, Affine toPage, PdfStructureElement? tags)
    {
        var (w, h) = (frame.Box.Width, frame.Box.Height);
        if (w <= 1 || h <= 1)
        {
            return;
        }

        var alternative = frame.AltText is { Length: > 0 } alt ? alt : frame.Chart.Title?.Text is { Length: > 0 } title ? "Chart: " + title : "Chart";
        var figure = tags?.AddChild("Figure");
        if (figure is not null)
        {
            figure.AlternateText = alternative;
            var (bx, by, bw, bh) = Bounds(toPage, w, h);
            figure.BoundingBox = new PdfRectangle(bx, by, bw, bh);
            canvas.BeginTag(figure);
        }

        var options = new ChartRenderOptions
        {
            Fonts = (typeface, bold, italic) => _fonts.Get(ChartFont(typeface, frame), null, null, bold, italic),
            DefaultFontSize = ChartFontSize,
            OpaqueChartArea = false,
        };

        // The chart is drawn y upwards from the frame's bottom-left corner.
        var toChart = new Affine(1, 0, 0, -1, 0, h).Then(toPage);
        canvas.SaveState();
        canvas.Transform(toChart.A, toChart.B, toChart.C, toChart.D, toChart.E, toChart.F);
        ChartRenderer.Draw(canvas, frame.Chart, 0, 0, w, h, options);
        canvas.RestoreState();
        if (figure is not null)
        {
            canvas.EndTag();
        }
    }

    /// <summary>The family of chart text: the typeface, with theme font references resolved.</summary>
    private static string ChartFont(string? typeface, ChartFrame frame) => typeface switch
    {
        null or "" or "+mn-lt" or "+mn-ea" or "+mn-cs" => frame.MinorFont,
        "+mj-lt" or "+mj-ea" or "+mj-cs" => frame.MajorFont,
        _ => typeface,
    };
}
