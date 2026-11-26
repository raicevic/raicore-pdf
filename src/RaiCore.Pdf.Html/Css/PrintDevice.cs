using AngleSharp.Css;
using RaiCore.Pdf.Layout;

namespace RaiCore.Pdf.Html.Css;

/// <summary>
/// The printer AngleSharp computes styles for. As in Chrome's print layout, width and height media queries see the
/// paper of the print settings (<see cref="HtmlConverterOptions.Page"/>, whatever size <c>@page</c> gives the pages),
/// while viewport units (<c>vw</c>, <c>vh</c>) are of the page area (the content area of the pages as <c>@page</c>
/// sets them up).
/// </summary>
internal sealed class PrintDevice : IRenderDevice
{
    /// <summary>The paper's width in CSS pixels (<c>device-width</c>).</summary>
    public int DeviceWidth { get; set; }

    /// <summary>The paper's height in CSS pixels (<c>device-height</c>).</summary>
    public int DeviceHeight { get; set; }

    /// <summary>The width width media queries see, in CSS pixels.</summary>
    public int ViewPortWidth { get; set; }

    /// <summary>The height height media queries see, in CSS pixels.</summary>
    public int ViewPortHeight { get; set; }

    /// <summary>The width <c>vw</c> is a hundredth of, in CSS pixels.</summary>
    public double RenderWidth { get; set; }

    /// <summary>The height <c>vh</c> is a hundredth of, in CSS pixels.</summary>
    public double RenderHeight { get; set; }

    public double FontSize => 16;

    public bool IsInterlaced => false;

    public DeviceCategory Category => DeviceCategory.Printer;

    public int Frequency => 0;

    public int Resolution => 96;

    public bool IsGrid => false;

    public bool IsScripting => false;

    public int ColorBits => 24;

    public int MonochromeBits => 0;

    /// <summary>A device for the <paramref name="paper"/> whose media queries and viewport units both see a viewport of the given size (in points).</summary>
    public static PrintDevice For(PageSetup paper, double viewportWidth, double viewportHeight) => new()
    {
        DeviceWidth = Pixels(paper.Size.Width),
        DeviceHeight = Pixels(paper.Size.Height),
        ViewPortWidth = Pixels(viewportWidth),
        ViewPortHeight = Pixels(viewportHeight),
        RenderWidth = viewportWidth / CssValues.PointsPerPixel,
        RenderHeight = viewportHeight / CssValues.PointsPerPixel,
    };

    private static int Pixels(double points) => Math.Max(1, (int)(points / CssValues.PointsPerPixel));
}
