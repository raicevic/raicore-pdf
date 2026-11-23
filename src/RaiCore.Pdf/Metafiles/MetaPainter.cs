using System.Globalization;
using System.Text;
using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.IO;
using RaiCore.Pdf.Objects;
using RaiCore.Pdf.Svg;

namespace RaiCore.Pdf.Metafiles;

/// <summary>How an area is filled.</summary>
internal abstract record MetaBrush;

/// <summary>One color.</summary>
internal sealed record SolidBrush(PdfColor Color) : MetaBrush;

/// <summary>
/// A hatch ([MS-WMF] HatchStyle: 0 horizontal, 1 vertical, 2 down from the left, 3 up from the left, 4 cross, 5
/// diagonal cross) of lines <paramref name="Spacing"/> apart (in form units), on a background unless it is null.
/// </summary>
internal sealed record HatchBrush(int Style, PdfColor Color, PdfColor? Background, double Spacing, double LineWidth) : MetaBrush;

/// <summary>A bitmap repeated over the area: RGB pixels, top row first, each pixel <paramref name="PixelSize"/> form units.</summary>
internal sealed record PatternBrush(int Width, int Height, byte[] Rgb, double PixelSize, PdfColor Average) : MetaBrush;

/// <summary>A gradient in the space that <paramref name="Transform"/> maps to form space (form space when it is null).</summary>
internal sealed record GradientBrush(ShadingGeometry Geometry, IReadOnlyList<(double Offset, PdfColor Color)> Stops, SvgMatrix? Transform = null) : MetaBrush;

/// <summary>How a line is drawn: color, width and dashes in form units.</summary>
internal sealed record MetaStroke(PdfColor Color, double Width, IReadOnlyList<double>? Dash, LineCap Cap, LineJoin Join, double MiterLimit);

/// <summary>
/// Draws metafile output onto the form's canvas. Each drawing operation is clipped by its clip region: the region is
/// set in a saved graphics state that stays open while the region does not change, so a run of operations with the
/// same clip shares it. Colors and line styles are written only when they change.
/// </summary>
internal sealed class MetaPainter(PdfCanvas canvas, double width, double height)
{
    private readonly PdfCanvas _canvas = canvas;
    private readonly Dictionary<HatchBrush, PdfStreamObject> _hatches = [];
    private readonly Dictionary<PatternBrush, PdfStreamObject> _patterns = [];
    private ClipRegion? _emittedClip;
    private bool _clipOpen;
    private PdfColor? _fill;
    private PdfColor? _stroke;
    private double? _lineWidth;
    private string? _dash;
    private LineCap? _cap;
    private LineJoin? _join;
    private double? _miter;

    /// <summary>The form's size in points.</summary>
    public double Width { get; } = width;

    public double Height { get; } = height;

    /// <summary>A rectangle far larger than the form, for complements of regions.</summary>
    public MetaPath Far
    {
        get
        {
            var margin = 10 * (Width + Height) + 10000;
            return MetaPath.Rectangle((-margin, -margin), (Width + margin, Height + margin));
        }
    }

    /// <summary>Fills a path.</summary>
    public void Fill(MetaPath path, MetaBrush brush, bool evenOdd, ClipRegion? clip)
    {
        if (path.IsEmpty || !path.IsFinite || !Clip(clip))
        {
            return;
        }

        switch (brush)
        {
            case SolidBrush solid:
                SetFill(solid.Color);
                path.Emit(_canvas);
                Paint(evenOdd);
                break;
            case HatchBrush hatch:
                if (hatch.Background is { } background)
                {
                    SetFill(background);
                    path.Emit(_canvas);
                    Paint(evenOdd);
                }

                _canvas.SetFillPattern(HatchPattern(hatch));
                _fill = null;
                path.Emit(_canvas);
                Paint(evenOdd);
                break;
            case PatternBrush pattern:
                if (pattern.Width * pattern.Height > 256 * 256)
                {
                    SetFill(pattern.Average);
                }
                else
                {
                    _canvas.SetFillPattern(TilingPattern(pattern));
                    _fill = null;
                }

                path.Emit(_canvas);
                Paint(evenOdd);
                break;
            case GradientBrush gradient:
                _canvas.SaveState();
                path.Emit(_canvas);
                _canvas.Clip(evenOdd);
                if (gradient.Transform is { } t)
                {
                    if (!t.IsFinite || t.IsSingular)
                    {
                        _canvas.RestoreState();
                        break;
                    }

                    _canvas.Transform(t.A, t.B, t.C, t.D, t.E, t.F);
                }

                _canvas.PaintShading(gradient.Geometry, gradient.Stops);
                _canvas.RestoreState();
                break;
        }
    }

    /// <summary>Strokes a path.</summary>
    public void Stroke(MetaPath path, MetaStroke stroke, ClipRegion? clip)
    {
        if (path.IsEmpty || !path.IsFinite || !Clip(clip))
        {
            return;
        }

        SetStroke(stroke);
        path.Emit(_canvas);
        _canvas.Stroke();
    }

    /// <summary>Fills and strokes a path: one operation for a solid fill.</summary>
    public void FillAndStroke(MetaPath path, MetaBrush? brush, MetaStroke? stroke, bool evenOdd, ClipRegion? clip)
    {
        if (brush is SolidBrush solid && stroke is not null)
        {
            if (path.IsEmpty || !path.IsFinite || !Clip(clip))
            {
                return;
            }

            SetFill(solid.Color);
            SetStroke(stroke);
            path.Emit(_canvas);
            if (evenOdd)
            {
                _canvas.FillAndStrokeEvenOdd();
            }
            else
            {
                _canvas.FillAndStroke();
            }

            return;
        }

        if (brush is not null)
        {
            Fill(path, brush, evenOdd, clip);
        }

        if (stroke is not null)
        {
            Stroke(path, stroke, clip);
        }
    }

    /// <summary>Draws an image into the parallelogram the unit square maps to, clipped also by <paramref name="extra"/>.</summary>
    public void Image(PdfImage image, SvgMatrix unitToForm, ClipRegion? clip, MetaPath? extra = null)
    {
        if (!unitToForm.IsFinite || unitToForm.IsSingular || !Clip(clip))
        {
            return;
        }

        _canvas.SaveState();
        if (extra is not null)
        {
            extra.Emit(_canvas);
            _canvas.Clip();
        }

        _canvas.Transform(unitToForm.A, unitToForm.B, unitToForm.C, unitToForm.D, unitToForm.E, unitToForm.F);
        _canvas.DrawImage(image, 0, 0, 1, 1);
        _canvas.RestoreState();
    }

    /// <summary>
    /// Draws runs of text in a local space (x along the baseline, y up) that <paramref name="frame"/> maps to form
    /// space, clipped also by <paramref name="extra"/> (in form space).
    /// </summary>
    public void Text(SvgMatrix frame, IReadOnlyList<(PdfFont Font, double X, List<(string Text, double Advance)> Clusters)> runs, double size, PdfColor color, ClipRegion? clip, MetaPath? extra = null)
    {
        if (runs.Count == 0 || !frame.IsFinite || frame.IsSingular || !Clip(clip))
        {
            return;
        }

        _canvas.SaveState();
        if (extra is not null)
        {
            extra.Emit(_canvas);
            _canvas.Clip();
        }

        _canvas.Transform(frame.A, frame.B, frame.C, frame.D, frame.E, frame.F);
        _canvas.SetFillColor(color);
        foreach (var (font, x, clusters) in runs)
        {
            _canvas.DrawTextRun(clusters, font, size, x, 0);
        }

        _canvas.RestoreState();
    }

    /// <summary>Draws glyphs by number in a local space (x along the baseline, y up) that <paramref name="frame"/> maps to form space.</summary>
    public void Glyphs(SvgMatrix frame, IReadOnlyList<PositionedGlyph> glyphs, Fonts.OpenTypePdfFont font, double size, PdfColor color, ClipRegion? clip, MetaPath? extra = null)
    {
        if (glyphs.Count == 0 || !frame.IsFinite || frame.IsSingular || !Clip(clip))
        {
            return;
        }

        _canvas.SaveState();
        if (extra is not null)
        {
            extra.Emit(_canvas);
            _canvas.Clip();
        }

        _canvas.Transform(frame.A, frame.B, frame.C, frame.D, frame.E, frame.F);
        _canvas.SetFillColor(color);
        _canvas.DrawGlyphs(glyphs, font, size, 0, 0);
        _canvas.RestoreState();
    }

    /// <summary>Closes the clip's saved state at the end of the drawing.</summary>
    public void Finish()
    {
        if (_clipOpen)
        {
            _canvas.RestoreState();
            _clipOpen = false;
        }
    }

    /// <summary>Makes the clip region current; false when nothing can be drawn in it.</summary>
    private bool Clip(ClipRegion? clip)
    {
        if (ReferenceEquals(clip, _emittedClip) && (_clipOpen || clip is null))
        {
            return clip?.Bounds() is not null || clip is null;
        }

        if (_clipOpen)
        {
            _canvas.RestoreState();
            _clipOpen = false;
            Invalidate();
        }

        _emittedClip = clip;
        if (clip is null)
        {
            return true;
        }

        if (clip.Bounds() is null)
        {
            return false;
        }

        _canvas.SaveState();
        _clipOpen = true;
        foreach (var (path, evenOdd) in clip.Parts)
        {
            path.Emit(_canvas);
            _canvas.Clip(evenOdd);
        }

        return true;
    }

    private void Invalidate()
    {
        _fill = null;
        _stroke = null;
        _lineWidth = null;
        _dash = null;
        _cap = null;
        _join = null;
        _miter = null;
    }

    private void Paint(bool evenOdd)
    {
        if (evenOdd)
        {
            _canvas.FillEvenOdd();
        }
        else
        {
            _canvas.Fill();
        }
    }

    private void SetFill(PdfColor color)
    {
        if (_fill is not { } current || !Same(current, color))
        {
            _canvas.SetFillColor(color);
            _fill = color;
        }
    }

    private void SetStroke(MetaStroke stroke)
    {
        if (_stroke is not { } current || !Same(current, stroke.Color))
        {
            _canvas.SetStrokeColor(stroke.Color);
            _stroke = stroke.Color;
        }

        var width = double.IsFinite(stroke.Width) ? Math.Clamp(stroke.Width, 0, 10000) : 0;
        if (_lineWidth != width)
        {
            _canvas.SetLineWidth(width);
            _lineWidth = width;
        }

        var dash = stroke.Dash is { Count: > 0 } d && d.All(v => v >= 0 && double.IsFinite(v)) && d.Any(v => v > 0)
            ? string.Join(' ', d.Select(v => v.ToString("R", CultureInfo.InvariantCulture)))
            : string.Empty;
        if (_dash != dash)
        {
            _canvas.SetDash(dash.Length == 0 ? [] : stroke.Dash!);
            _dash = dash;
        }

        if (_cap != stroke.Cap)
        {
            _canvas.SetLineCap(stroke.Cap);
            _cap = stroke.Cap;
        }

        if (_join != stroke.Join)
        {
            _canvas.SetLineJoin(stroke.Join);
            _join = stroke.Join;
        }

        var miter = double.IsFinite(stroke.MiterLimit) ? Math.Clamp(stroke.MiterLimit, 1, 1000) : 10;
        if (stroke.Join == LineJoin.Miter && _miter != miter)
        {
            _canvas.SetMiterLimit(miter);
            _miter = miter;
        }
    }

    private static bool Same(PdfColor a, PdfColor b) => a.Red == b.Red && a.Green == b.Green && a.Blue == b.Blue && a.Alpha == b.Alpha;

    /// <summary>A colored tiling pattern of hatch lines, one cell of <see cref="HatchBrush.Spacing"/> square.</summary>
    private PdfStreamObject HatchPattern(HatchBrush hatch)
    {
        if (_hatches.TryGetValue(hatch, out var pattern))
        {
            return pattern;
        }

        var s = hatch.Spacing > 0 && double.IsFinite(hatch.Spacing) ? hatch.Spacing : 6;
        var content = new StringBuilder();
        content.Append(N(hatch.Color.Red)).Append(' ').Append(N(hatch.Color.Green)).Append(' ').Append(N(hatch.Color.Blue)).Append(" RG ")
            .Append(N(Math.Clamp(hatch.LineWidth, 0, s))).Append(" w 0 J\n");
        void Line(double x0, double y0, double x1, double y1) =>
            content.Append(N(x0)).Append(' ').Append(N(y0)).Append(" m ").Append(N(x1)).Append(' ').Append(N(y1)).Append(" l S\n");
        var h = s / 2;
        switch (hatch.Style)
        {
            case 0:
                Line(0, h, s, h);
                break;
            case 1:
                Line(h, 0, h, s);
                break;
            case 2:
                // Down from the left, as on screen: from the top left to the bottom right (y is up here).
                Line(-1, s + 1, s + 1, -1);
                Line(-1, 1, 1, -1);
                Line(s - 1, s + 1, s + 1, s - 1);
                break;
            case 3:
                Line(-1, -1, s + 1, s + 1);
                Line(-1, s - 1, 1, s + 1);
                Line(s - 1, -1, s + 1, 1);
                break;
            case 4:
                Line(0, h, s, h);
                Line(h, 0, h, s);
                break;
            default:
                Line(-1, s + 1, s + 1, -1);
                Line(-1, -1, s + 1, s + 1);
                break;
        }

        pattern = TilingStream(Encoding.ASCII.GetBytes(content.ToString()), s, s);
        _hatches[hatch] = pattern;
        return pattern;
    }

    /// <summary>A colored tiling pattern of a bitmap, as an inline image (it needs no resources).</summary>
    private PdfStreamObject TilingPattern(PatternBrush brush)
    {
        if (_patterns.TryGetValue(brush, out var pattern))
        {
            return pattern;
        }

        var (w, h) = (brush.Width * brush.PixelSize, brush.Height * brush.PixelSize);
        var content = new StringBuilder();
        content.Append("q ").Append(N(w)).Append(" 0 0 ").Append(N(h)).Append(" 0 0 cm BI /W ").Append(brush.Width.ToString(CultureInfo.InvariantCulture))
            .Append(" /H ").Append(brush.Height.ToString(CultureInfo.InvariantCulture)).Append(" /BPC 8 /CS /RGB /F /AHx ID\n");
        content.Append(Convert.ToHexString(brush.Rgb)).Append(">\nEI Q\n");
        pattern = TilingStream(Encoding.ASCII.GetBytes(content.ToString()), w, h);
        _patterns[brush] = pattern;
        return pattern;
    }

    private static PdfStreamObject TilingStream(byte[] content, double width, double height)
    {
        var stream = PdfStreamObject.CreateCompressed(content);
        var dictionary = stream.Dictionary;
        dictionary["Type"] = new PdfName("Pattern");
        dictionary["PatternType"] = new PdfInteger(1);
        dictionary["PaintType"] = new PdfInteger(1);
        dictionary["TilingType"] = new PdfInteger(1);
        dictionary["BBox"] = PdfArray.OfNumbers(0, 0, width, height);
        dictionary["XStep"] = new PdfReal(width);
        dictionary["YStep"] = new PdfReal(height);
        dictionary["Resources"] = new PdfDictionary();
        return stream;
    }

    private static string N(double value) => PdfNumberFormat.Format(value);
}
