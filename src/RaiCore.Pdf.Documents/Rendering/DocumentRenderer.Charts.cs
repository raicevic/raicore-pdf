using RaiCore.Pdf.Charts;
using RaiCore.Pdf.Documents.Model;

namespace RaiCore.Pdf.Documents.Rendering;

/// <summary>Charts, drawn by the core's chart engine as vector images of their size in the document.</summary>
internal sealed partial class DocumentRenderer
{
    /// <summary>The size of a chart whose drawing gives none: Word's default 6 by 3.5 inches.</summary>
    private const double DefaultChartWidth = 432;

    private const double DefaultChartHeight = 252;

    /// <summary>The font of chart text in a document without a theme: Office's body font.</summary>
    private const string OfficeFont = "Calibri";

    private readonly Dictionary<EmbeddedChart, PdfImage> _charts = new(ReferenceEqualityComparer.Instance);

    /// <summary>A chart drawn once as an image of the size its drawing gives (in the fonts of the document).</summary>
    private PdfImage ChartImage(ImageInline image, EmbeddedChart chart)
    {
        if (!_charts.TryGetValue(chart, out var drawn))
        {
            var width = image.Width > 0 ? image.Width : DefaultChartWidth;
            var height = image.Height > 0 ? image.Height : DefaultChartHeight;
            var options = new ChartRenderOptions
            {
                Fonts = (typeface, bold, italic) => _styles.Fonts(typeface switch
                {
                    null or "" or "+mn-lt" or "+mn-ea" or "+mn-cs" => chart.MinorFont ?? OfficeFont,
                    "+mj-lt" or "+mj-ea" or "+mj-cs" => chart.MajorFont ?? chart.MinorFont ?? OfficeFont,
                    _ => typeface,
                }, bold, italic),
                DefaultFontSize = 10,
                OpaqueChartArea = true,
            };
            drawn = ChartRenderer.ToImage(chart.Chart, width, height, options);
            _charts[chart] = drawn;
        }

        return drawn;
    }
}
