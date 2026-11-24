using System.Globalization;
using RaiCore.Pdf.Xfa.Form;
using RaiCore.Pdf.Xfa.Formatting;
using RaiCore.Pdf.Xfa.Template;

namespace RaiCore.Pdf.Xfa.Layout;

/// <summary>
/// Lays out form objects (XFA 3.3, "Layout for Growable Objects" and "Layout for Dynamic Forms"): containers place their
/// children by position (<c>x</c>, <c>y</c>, <c>anchorType</c>, <c>rotate</c>), top to bottom (<c>tb</c>), in lines
/// left to right or right to left (<c>lr-tb</c>, <c>rl-tb</c>), or as tables of rows with column widths and spans;
/// objects without a width or height grow to fit their content within their minimum and maximum.
/// </summary>
/// <remarks>Partial: draws' and fields' content in <c>Measurer.Widgets.cs</c>.</remarks>
internal sealed partial class Measurer
{
    private const double Unbounded = 1e6;

    private readonly TextLayouter _text;
    private readonly LocaleSet _locales;
    private readonly MergedForm _form;
    private readonly ResourceLimits _limits;
    private readonly Action<XfaDiagnosticKind, string>? _report;
    private readonly Dictionary<string, PdfImage?> _images = new(StringComparer.Ordinal);

    public Measurer(TextLayouter text, LocaleSet locales, MergedForm form, ResourceLimits limits, Action<XfaDiagnosticKind, string>? report)
    {
        _text = text;
        _locales = locales;
        _form = form;
        _limits = limits;
        _report = report;
    }

    /// <summary>Lays out an object; <paramref name="available"/> is the width a growable flow wraps at.</summary>
    public LayoutBox Measure(FormNode node, double available, double? forcedWidth = null, double? forcedHeight = null)
    {
        ResourceLimits.Checkpoint();
        using var nesting = ResourceLimits.EnterNesting();
        return node.Kind is NodeKind.Field or NodeKind.Draw
            ? Widget(node, available, forcedWidth, forcedHeight)
            : Container(node, available, forcedWidth, forcedHeight);
    }

    /// <summary>The children of a container that take part in layout.</summary>
    internal static IEnumerable<FormNode> Laid(FormNode node) => node.Children.Where(c => c.IsLaidOut && !c.IsLeaderOrTrailer);

    /// <summary>The nominal size rules: fixed width and height, or growable within minimum and maximum.</summary>
    private static (double? W, double? H, double MinW, double MaxW, double MinH, double MaxH) Size(FormNode node)
    {
        var t = node.Template;
        var minW = Math.Max(t.Measure("minW"), 0);
        var minH = Math.Max(t.Measure("minH"), 0);
        var maxW = t.MeasureOrNull("maxW") is { } mw and > 0 ? mw : Unbounded;
        var maxH = t.MeasureOrNull("maxH") is { } mh and > 0 ? mh : Unbounded;
        var w = t.MeasureOrNull("w") is { } fw ? Math.Max(fw, 0) : (double?)null;
        var h = t.MeasureOrNull("h") is { } fh ? Math.Max(fh, 0) : (double?)null;
        return (w, h, minW, Math.Max(maxW, minW), minH, Math.Max(maxH, minH));
    }

    /// <summary>The rotation, a multiple of 90 degrees.</summary>
    private static int Rotation(FormNode node) =>
        ((int.TryParse(node.Template.Attr("rotate"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var r) ? r : 0) % 360 + 360) % 360 / 90 * 90;

    private LayoutBox Container(FormNode node, double available, double? forcedWidth, double? forcedHeight)
    {
        var t = node.Template;
        var layout = node.Kind == NodeKind.Area ? "position" : t.Attr("layout") ?? "position";
        var margin = Props.Margin(t);
        var (w, h, minW, maxW, minH, maxH) = Size(node);
        w = forcedWidth ?? w;
        h = forcedHeight ?? h;
        var inner = Math.Max((w ?? Math.Min(maxW, available)) - margin.Horizontal, 0);
        var children = new List<LayoutBox>();
        double contentW = 0, contentH = 0;
        switch (layout)
        {
            case "tb":
            {
                foreach (var child in Laid(node))
                {
                    var box = Measure(child, inner);
                    box.X = 0;
                    box.Y = contentH;
                    contentH += box.ExtentHeight;
                    contentW = Math.Max(contentW, box.ExtentWidth);
                    children.Add(box);
                }

                break;
            }

            case "lr-tb" or "rl-tb":
                (contentW, contentH) = Lines(node, inner, layout == "rl-tb", children);
                break;
            case "table":
                (contentW, contentH) = Table(node, inner, children);
                break;
            case "row":
                (contentW, contentH) = Row(node, inner, children);
                break;
            default:
            {
                foreach (var child in Laid(node))
                {
                    var box = Measure(child, inner);
                    Position(box, child);
                    contentW = Math.Max(contentW, box.X + box.ExtentWidth);
                    contentH = Math.Max(contentH, box.Y + box.ExtentHeight);
                    children.Add(box);
                }

                break;
            }
        }

        var width = w ?? Math.Clamp(contentW + margin.Horizontal, minW, maxW);
        var height = h ?? Math.Clamp(contentH + margin.Vertical, minH, maxH);
        if (layout == "row")
        {
            // Cells are as tall as their row.
            foreach (var cell in children.Where(c => c.Rotate == 0))
            {
                cell.H = height - margin.Vertical;
            }
        }

        var flowed = layout is "tb" or "lr-tb" or "rl-tb" or "table";
        var keep = t.Child("keep");
        var intact = keep.Attr("intact") is "contentArea" or "pageArea";
        var (leader, trailer) = LayoutRules.Overflow(t);
        var box2 = new LayoutBox
        {
            Node = node,
            W = width,
            H = height,
            Rotate = Rotation(node),
            Margin = margin,
            Children = children,
            Splittable = flowed && !intact && h is null && Rotation(node) == 0,
            BreakBefore = LayoutRules.Before(t),
            BreakAfter = LayoutRules.After(t),
            KeepNext = keep.Attr("next") is "contentArea" or "pageArea",
            KeepPrevious = keep.Attr("previous") is "contentArea" or "pageArea",
            Leader = leader is null ? null : Overflow(node, leader, inner),
            Trailer = trailer is null ? null : Overflow(node, trailer, inner),
        };
        if (flowed && children.Count > 0)
        {
            // A break on the first or last object of a flow is the flow's own.
            box2.BreakBefore ??= children[0].BreakBefore;
            box2.BreakAfter ??= children[^1].BreakAfter;
            box2.ContainsBreak = children.Skip(1).Any(c => c.BreakBefore is not null) || children.SkipLast(1).Any(c => c.BreakAfter is not null)
                || children.Any(c => c.ContainsBreak);
        }

        return box2;
    }

    /// <summary>Places a positioned child: its anchor point at (<c>x</c>, <c>y</c>), rotated about it.</summary>
    private static void Position(LayoutBox box, FormNode node)
    {
        var t = node.Template;
        var x = t.Measure("x");
        var y = t.Measure("y");
        var (ax, ay) = (t.Attr("anchorType") ?? "topLeft") switch
        {
            "topCenter" => (box.W / 2, 0.0),
            "topRight" => (box.W, 0.0),
            "middleLeft" => (0.0, box.H / 2),
            "middleCenter" => (box.W / 2, box.H / 2),
            "middleRight" => (box.W, box.H / 2),
            "bottomLeft" => (0.0, box.H),
            "bottomCenter" => (box.W / 2, box.H),
            "bottomRight" => (box.W, box.H),
            _ => (0.0, 0.0),
        };

        // The nominal frame's corners relative to the anchor, rotated counterclockwise (y downwards).
        double minX = double.MaxValue, minY = double.MaxValue;
        foreach (var (cx, cy) in new[] { (0.0, 0.0), (box.W, 0.0), (0.0, box.H), (box.W, box.H) })
        {
            var (u, v) = (cx - ax, cy - ay);
            var (rx, ry) = box.Rotate switch
            {
                90 => (v, -u),
                180 => (-u, -v),
                270 => (-v, u),
                _ => (u, v),
            };
            minX = Math.Min(minX, rx);
            minY = Math.Min(minY, ry);
        }

        box.X = x + minX;
        box.Y = y + minY;
    }

    /// <summary>Lays out children in lines (<c>lr-tb</c>, or <c>rl-tb</c> from the right), each line a box of its own.</summary>
    private (double Width, double Height) Lines(FormNode node, double inner, bool rightToLeft, List<LayoutBox> lines)
    {
        var line = new List<LayoutBox>();
        double x = 0, y = 0, lineHeight = 0, widest = 0;
        void Flush()
        {
            if (line.Count == 0)
            {
                return;
            }

            foreach (var item in line.Where(_ => rightToLeft))
            {
                item.X = inner - item.X - item.ExtentWidth;
            }

            lines.Add(new LayoutBox
            {
                Y = y,
                W = rightToLeft ? inner : x,
                H = lineHeight,
                Children = [.. line],
                BreakBefore = line[0].BreakBefore,
                BreakAfter = line[^1].BreakAfter,
                KeepNext = line.Any(b => b.KeepNext),
                KeepPrevious = line[0].KeepPrevious,
            });
            widest = Math.Max(widest, x);
            y += lineHeight;
            (x, lineHeight) = (0, 0);
            line.Clear();
        }

        foreach (var child in Laid(node))
        {
            var box = Measure(child, inner);
            if ((line.Count > 0 && x + box.ExtentWidth > inner + 0.01) || box.BreakBefore is not null || (line.Count > 0 && line[^1].BreakAfter is not null))
            {
                Flush();
            }

            box.X = x;
            box.Y = 0;
            x += box.ExtentWidth;
            lineHeight = Math.Max(lineHeight, box.ExtentHeight);
            line.Add(box);
        }

        Flush();
        return (rightToLeft ? Math.Max(widest, inner) : widest, y);
    }
}
