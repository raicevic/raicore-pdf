using System.Xml.Linq;
using RaiCore.Pdf.Xfa.Layout;
using RaiCore.Pdf.Xfa.Template;

namespace RaiCore.Pdf.Xfa.Rendering;

/// <summary>Borders, fills, lines, rectangles, arcs and check marks.</summary>
internal sealed partial class XfaRenderer
{
    /// <summary>The control point distance of a quarter circle drawn as a cubic Bezier curve.</summary>
    private const double Kappa = 0.5522847498;

    /// <summary>Draws a border around a rectangle (y downwards): its fill, then its edges (top, right, bottom, left).</summary>
    private static void DrawBorder(PdfCanvas canvas, Border border, double x, double y, double w, double h, Affine toPage)
    {
        if (border.Fill is { } fill && w > 0 && h > 0)
        {
            canvas.SaveState();
            canvas.SetFillColor(fill);
            RoundedRectangle(canvas, x, y, w, h, border.Radius, toPage);
            canvas.Fill();
            canvas.RestoreState();
        }

        var edges = border.Edges;
        if (edges.All(e => e == edges[0]))
        {
            var edge = edges[0];
            if (!edge.Visible || edge.Thickness <= 0)
            {
                return;
            }

            var inset = Inset(border.Hand, edge.Thickness);
            canvas.SaveState();
            Stroke(canvas, edge);
            RoundedRectangle(canvas, x + inset, y + inset, Math.Max(w - (2 * inset), 0), Math.Max(h - (2 * inset), 0), border.Radius, toPage);
            canvas.Stroke();
            canvas.RestoreState();
            return;
        }

        (double X1, double Y1, double X2, double Y2)[] sides =
        [
            (x, y, x + w, y),
            (x + w, y, x + w, y + h),
            (x + w, y + h, x, y + h),
            (x, y + h, x, y),
        ];
        for (var i = 0; i < 4; i++)
        {
            var edge = edges[i];
            if (!edge.Visible || edge.Thickness <= 0)
            {
                continue;
            }

            var inset = Inset(border.Hand, edge.Thickness);
            var (x1, y1, x2, y2) = sides[i];
            (x1, y1, x2, y2) = i switch
            {
                0 => (x1, y1 + inset, x2, y2 + inset),
                1 => (x1 - inset, y1, x2 - inset, y2),
                2 => (x1, y1 - inset, x2, y2 - inset),
                _ => (x1 + inset, y1, x2 + inset, y2),
            };
            canvas.SaveState();
            Stroke(canvas, edge);
            Move(canvas, toPage, x1, y1);
            Line(canvas, toPage, x2, y2);
            canvas.Stroke();
            canvas.RestoreState();
        }
    }

    /// <summary>How far an edge is moved into its rectangle: half its thickness when it is drawn inside (<c>right</c>), or outside (<c>left</c>).</summary>
    private static double Inset(string hand, double thickness) => hand switch
    {
        "right" => thickness / 2,
        "left" => -thickness / 2,
        _ => 0,
    };

    /// <summary>Sets the stroke of an edge: color, width, dashes and cap.</summary>
    private static void Stroke(PdfCanvas canvas, Edge edge)
    {
        canvas.SetStrokeColor(edge.Stroke is "lowered" or "etched" ? Darken(edge.Color) : edge.Color);
        canvas.SetLineWidth(edge.Thickness);
        var unit = Math.Max(edge.Thickness, 0.5);
        double[]? dashes = edge.Stroke switch
        {
            "dashed" => [3 * unit, 3 * unit],
            "dotted" => [unit, unit],
            "dashDot" => [3 * unit, unit, unit, unit],
            "dashDotDot" => [3 * unit, unit, unit, unit, unit, unit],
            _ => null,
        };
        if (dashes is not null)
        {
            canvas.SetDash(dashes);
        }

        canvas.SetLineCap(edge.Cap switch
        {
            "round" => LineCap.Round,
            "butt" => LineCap.Butt,
            _ => LineCap.Square,
        });
    }

    /// <summary>A lowered or etched edge is drawn a little darker than its color (a 3D edge drawn flat).</summary>
    private static PdfColor Darken(PdfColor color) => color == PdfColor.Black ? PdfColor.FromRgb(64, 64, 64) : color;

    private static void Move(PdfCanvas canvas, Affine toPage, double x, double y)
    {
        var (px, py) = toPage.Map(x, y);
        canvas.MoveTo(px, py);
    }

    private static void Line(PdfCanvas canvas, Affine toPage, double x, double y)
    {
        var (px, py) = toPage.Map(x, y);
        canvas.LineTo(px, py);
    }

    private static void Curve(PdfCanvas canvas, Affine toPage, double x1, double y1, double x2, double y2, double x3, double y3)
    {
        var (a, b) = toPage.Map(x1, y1);
        var (c, d) = toPage.Map(x2, y2);
        var (e, f) = toPage.Map(x3, y3);
        canvas.CurveTo(a, b, c, d, e, f);
    }

    /// <summary>Adds a rectangle, with rounded corners when <paramref name="radius"/> is positive, to the current path.</summary>
    private static void RoundedRectangle(PdfCanvas canvas, double x, double y, double w, double h, double radius, Affine toPage)
    {
        var r = Math.Min(radius, Math.Min(w, h) / 2);
        if (r <= 0)
        {
            Move(canvas, toPage, x, y);
            Line(canvas, toPage, x + w, y);
            Line(canvas, toPage, x + w, y + h);
            Line(canvas, toPage, x, y + h);
            canvas.ClosePath();
            return;
        }

        var k = r * Kappa;
        Move(canvas, toPage, x + r, y);
        Line(canvas, toPage, x + w - r, y);
        Curve(canvas, toPage, x + w - r + k, y, x + w, y + r - k, x + w, y + r);
        Line(canvas, toPage, x + w, y + h - r);
        Curve(canvas, toPage, x + w, y + h - r + k, x + w - r + k, y + h, x + w - r, y + h);
        Line(canvas, toPage, x + r, y + h);
        Curve(canvas, toPage, x + r - k, y + h, x, y + h - r + k, x, y + h - r);
        Line(canvas, toPage, x, y + r);
        Curve(canvas, toPage, x, y + r - k, x + r - k, y, x + r, y);
        canvas.ClosePath();
    }

    /// <summary>Adds an ellipse filling a rectangle to the current path.</summary>
    private static void Ellipse(PdfCanvas canvas, double x, double y, double w, double h, Affine toPage)
    {
        var (rx, ry) = (w / 2, h / 2);
        var (cx, cy) = (x + rx, y + ry);
        var (kx, ky) = (rx * Kappa, ry * Kappa);
        Move(canvas, toPage, cx + rx, cy);
        Curve(canvas, toPage, cx + rx, cy + ky, cx + kx, cy + ry, cx, cy + ry);
        Curve(canvas, toPage, cx - kx, cy + ry, cx - rx, cy + ky, cx - rx, cy);
        Curve(canvas, toPage, cx - rx, cy - ky, cx - kx, cy - ry, cx, cy - ry);
        Curve(canvas, toPage, cx + kx, cy - ry, cx + rx, cy - ky, cx + rx, cy);
        canvas.ClosePath();
    }

    /// <summary>A line value: from the top left to the bottom right of its region (<c>slope="/"</c>: bottom left to top right).</summary>
    private static void DrawLine(PdfCanvas canvas, XElement line, (double X, double Y, double W, double H) area, Affine toPage)
    {
        var edge = Props.ReadEdge(line.Child("edge"));
        if (!edge.Visible || edge.Thickness <= 0)
        {
            return;
        }

        canvas.SaveState();
        Stroke(canvas, edge);
        if (line.Attr("slope") == "/")
        {
            Move(canvas, toPage, area.X, area.Y + area.H);
            Line(canvas, toPage, area.X + area.W, area.Y);
        }
        else
        {
            Move(canvas, toPage, area.X, area.Y);
            Line(canvas, toPage, area.X + area.W, area.Y + area.H);
        }

        canvas.Stroke();
        canvas.RestoreState();
    }

    /// <summary>A rectangle value: its edges, corners and fill, like a border.</summary>
    private static void DrawRectangle(PdfCanvas canvas, XElement rectangle, (double X, double Y, double W, double H) area, Affine toPage)
    {
        var border = Props.ReadBorder(new XElement(rectangle.Name, rectangle.Attributes().Where(a => a.Name.LocalName == "hand"), rectangle.Elements()));
        if (border is not null)
        {
            DrawBorder(canvas, border, area.X, area.Y, area.W, area.H, toPage);
        }
    }

    /// <summary>An arc value: an ellipse in its region (or a circle, when <c>circular</c>), or a part of one.</summary>
    private static void DrawArc(PdfCanvas canvas, XElement arc, (double X, double Y, double W, double H) area, Affine toPage)
    {
        var edge = Props.ReadEdge(arc.Child("edge"));
        var fill = Props.Fill(arc.Child("fill"));
        var (x, y, w, h) = area;
        if (arc.Attr("circular") == "1")
        {
            var d = Math.Min(w, h);
            (x, y, w, h) = (x + ((w - d) / 2), y + ((h - d) / 2), d, d);
        }

        var sweep = double.TryParse(arc.Attr("sweepAngle"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s) ? s : 360;
        var start = double.TryParse(arc.Attr("startAngle"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var a) ? a : 0;
        void Path()
        {
            if (Math.Abs(sweep) >= 360)
            {
                Ellipse(canvas, x, y, w, h, toPage);
                return;
            }

            // A part of the ellipse, counterclockwise from the start angle, as short line segments.
            var steps = Math.Clamp((int)Math.Ceiling(Math.Abs(sweep) / 5), 1, 72);
            for (var i = 0; i <= steps; i++)
            {
                var angle = (start + (sweep * i / steps)) * Math.PI / 180;
                var (px, py) = (x + (w / 2) + (w / 2 * Math.Cos(angle)), y + (h / 2) - (h / 2 * Math.Sin(angle)));
                if (i == 0)
                {
                    Move(canvas, toPage, px, py);
                }
                else
                {
                    Line(canvas, toPage, px, py);
                }
            }
        }

        if (fill is { } color)
        {
            canvas.SaveState();
            canvas.SetFillColor(color);
            Path();
            canvas.Fill();
            canvas.RestoreState();
        }

        if (edge.Visible && edge.Thickness > 0)
        {
            canvas.SaveState();
            Stroke(canvas, edge);
            Path();
            canvas.Stroke();
            canvas.RestoreState();
        }
    }

    /// <summary>A check box or radio button: its box (or circle) in the value region, and its mark when it is on.</summary>
    private static void DrawCheck(PdfCanvas canvas, WidgetContent content, (double X, double Y, double W, double H) value, Affine toPage)
    {
        var size = content.CheckSize > 0 ? content.CheckSize : Math.Min(value.W, value.H);
        var x = content.Para.HAlign switch
        {
            "center" => value.X + ((value.W - size) / 2),
            "right" => value.X + value.W - size,
            _ => value.X,
        };
        var y = content.Para.VAlign switch
        {
            "middle" => value.Y + ((value.H - size) / 2),
            "bottom" => value.Y + value.H - size,
            _ => value.Y,
        };
        var round = content.CheckShape == "round";
        if (content.UiBorder is { } border)
        {
            if (round)
            {
                var edge = border.Edges[0];
                if (border.Fill is { } fill)
                {
                    canvas.SaveState();
                    canvas.SetFillColor(fill);
                    Ellipse(canvas, x, y, size, size, toPage);
                    canvas.Fill();
                    canvas.RestoreState();
                }

                if (edge.Visible && edge.Thickness > 0)
                {
                    canvas.SaveState();
                    Stroke(canvas, edge);
                    Ellipse(canvas, x, y, size, size, toPage);
                    canvas.Stroke();
                    canvas.RestoreState();
                }
            }
            else
            {
                DrawBorder(canvas, border, x, y, size, size, toPage);
            }
        }

        if (!content.Checked)
        {
            return;
        }

        var color = content.Font.Color;
        canvas.SaveState();
        canvas.SetFillColor(color);
        canvas.SetStrokeColor(color);
        var (cx, cy, r) = (x + (size / 2), y + (size / 2), size / 2);
        switch (content.Mark)
        {
            case "circle":
                Ellipse(canvas, cx - (r * 0.5), cy - (r * 0.5), r, r, toPage);
                canvas.Fill();
                break;
            case "square":
                Move(canvas, toPage, cx - (r * 0.5), cy - (r * 0.5));
                Line(canvas, toPage, cx + (r * 0.5), cy - (r * 0.5));
                Line(canvas, toPage, cx + (r * 0.5), cy + (r * 0.5));
                Line(canvas, toPage, cx - (r * 0.5), cy + (r * 0.5));
                canvas.ClosePath();
                canvas.Fill();
                break;
            case "diamond":
                Move(canvas, toPage, cx, cy - (r * 0.6));
                Line(canvas, toPage, cx + (r * 0.6), cy);
                Line(canvas, toPage, cx, cy + (r * 0.6));
                Line(canvas, toPage, cx - (r * 0.6), cy);
                canvas.ClosePath();
                canvas.Fill();
                break;
            case "star":
                for (var i = 0; i < 10; i++)
                {
                    var angle = (Math.PI / 2) + (i * Math.PI / 5);
                    var radius = i % 2 == 0 ? r * 0.7 : r * 0.3;
                    var (px, py) = (cx + (radius * Math.Cos(angle)), cy - (radius * Math.Sin(angle)));
                    if (i == 0)
                    {
                        Move(canvas, toPage, px, py);
                    }
                    else
                    {
                        Line(canvas, toPage, px, py);
                    }
                }

                canvas.ClosePath();
                canvas.Fill();
                break;
            case "cross":
                canvas.SetLineWidth(Math.Max(size / 10, 0.5));
                Move(canvas, toPage, x + (size * 0.2), y + (size * 0.2));
                Line(canvas, toPage, x + (size * 0.8), y + (size * 0.8));
                Move(canvas, toPage, x + (size * 0.8), y + (size * 0.2));
                Line(canvas, toPage, x + (size * 0.2), y + (size * 0.8));
                canvas.Stroke();
                break;
            default:
                canvas.SetLineWidth(Math.Max(size / 8, 0.5));
                canvas.SetLineJoin(LineJoin.Round);
                Move(canvas, toPage, x + (size * 0.2), y + (size * 0.55));
                Line(canvas, toPage, x + (size * 0.42), y + (size * 0.78));
                Line(canvas, toPage, x + (size * 0.82), y + (size * 0.25));
                canvas.Stroke();
                break;
        }

        canvas.RestoreState();
    }
}
